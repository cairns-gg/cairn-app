using Cairn.Core.ModDb;

namespace Cairn.Core.Packs;

/// <summary>What retargeting a pack at another game version does to one of its mods.</summary>
public enum ModOutcome
{
    /// <summary>The release already installed is also the one for the target version.</summary>
    Unchanged,

    /// <summary>A different release will be installed.</summary>
    Moves,

    /// <summary>
    /// Installs, but the release is marked for another version in the same minor series
    /// rather than the target exactly. Usually fine — the game itself treats same-minor
    /// releases as installable — which is why this warns rather than blocks.
    /// </summary>
    Approximate,

    /// <summary>Nothing on ModDB serves the target version. This mod stops working.</summary>
    Unavailable,

    /// <summary>The pack pins a version of this mod, and that version has nothing for the target.</summary>
    PinUnavailable,

    /// <summary>
    /// ModDB could not be reached, so nothing is known about this mod. Deliberately not
    /// folded in with Unavailable: "it will break" and "we could not find out" lead to
    /// different decisions, and this is the screen the decision gets made on.
    /// </summary>
    Unknown,
}

/// <summary>What one mod does if the change is applied.</summary>
public sealed record ModVerdict(string ModId, string? From, string? To, ModOutcome Outcome, string Note)
{
    public bool Breaks => Outcome is ModOutcome.Unavailable or ModOutcome.PinUnavailable;
    public bool Warns => Outcome is ModOutcome.Approximate;
    public bool Changes => Outcome is ModOutcome.Moves;
    public bool Unknown => Outcome is ModOutcome.Unknown;
}

/// <summary>
/// What changing a pack's game version would do, worked out without downloading anything
/// or writing to the pack.
///
/// Retargeting invalidates the lockfile for every mod — PackSyncer's lockApplies compares
/// the locked game version against the manifest's — so every mod is re-resolved. That is
/// the right behaviour and it is also why the change deserves a preview: it can silently
/// move several mods at once, or leave one behind entirely.
/// </summary>
public sealed record VersionChangePlan(
    string From,
    string To,
    IReadOnlyList<ModVerdict> Mods,
    IReadOnlyList<string> Worlds)
{
    public bool IsDowngrade => GameVersions.IsLowerVersionThan(To, From);
    public bool IsUpgrade => GameVersions.IsNewerVersionThan(To, From);

    /// <summary>Nothing to preview when the target is what the pack already targets.</summary>
    public bool IsNoChange => !IsDowngrade && !IsUpgrade;

    public IEnumerable<ModVerdict> Breaking => Mods.Where(m => m.Breaks);
    public IEnumerable<ModVerdict> Warning => Mods.Where(m => m.Warns);
    public IEnumerable<ModVerdict> Moving => Mods.Where(m => m.Changes);
    public IEnumerable<ModVerdict> Unchecked => Mods.Where(m => m.Unknown);

    public bool AnythingBreaks => Breaking.Any();

    /// <summary>Some mod could not be checked, so the preview is incomplete.</summary>
    public bool IsIncomplete => Unchecked.Any();

    /// <summary>
    /// A world saved by a newer build generally will not open on an older one, and Vintage
    /// Story upgrades a save's format on load rather than asking. Only worth saying when
    /// the pack actually has worlds of its own to lose.
    /// </summary>
    public bool RisksWorlds => IsDowngrade && Worlds.Count > 0;

    public string Summary()
    {
        if (IsNoChange) return Lang.Get("versionchange-no-change", To);

        var direction = IsDowngrade ? Lang.Get("versionchange-downgrade") : Lang.Get("versionchange-upgrade");
        var parts = new List<string>();

        if (AnythingBreaks) parts.Add(Lang.Get("versionchange-would-break", Breaking.Count()));
        if (Moving.Any()) parts.Add(Lang.Get("versionchange-would-move", Moving.Count()));
        if (Warning.Any()) parts.Add(Lang.Get("versionchange-not-marked", Warning.Count(), To));
        if (IsIncomplete) parts.Add(Lang.Get("versionchange-unchecked", Unchecked.Count()));

        var mods = parts.Count == 0
            ? Mods.Count == 0 ? Lang.Get("versionchange-no-mods") : Lang.Get("versionchange-all-keep")
            : string.Join(", ", parts) + ".";

        return Lang.Get("versionchange-summary", direction, From, To, mods);
    }
}

public static class GameVersionChange
{
    /// <summary>
    /// Resolves every mod against <paramref name="target"/> exactly as a sync would after
    /// the retarget, but downloads nothing and writes nothing.
    ///
    /// Kept deliberately parallel to PackSyncer's resolve step: same call, same arguments,
    /// same same-minor rule. A preview that disagrees with the sync it predicts is worse
    /// than no preview at all.
    /// </summary>
    public static async Task<VersionChangePlan> PreviewAsync(
        ModDbClient moddb,
        PackManifest manifest,
        PackLock? locked,
        string target,
        IReadOnlyList<string>? worlds = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var verdicts = new List<ModVerdict>();

        foreach (var want in manifest.Mods)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(want.ModId);

            var installed = locked?.Mods.FirstOrDefault(
                m => string.Equals(m.ModId, want.ModId, StringComparison.OrdinalIgnoreCase))?.Version;

            ResolvedRelease? release;
            try
            {
                // want.Version is the manifest's pin, or null for "newest". This mirrors
                // the `wanted` PackSyncer computes once the lock no longer applies.
                release = await moddb.ResolveAsync(want.ModId, target, want.Version, ct).ConfigureAwait(false);
            }
            catch (ModDbException e)
            {
                // ModDB answering "no" is a verdict, and its own message is more specific
                // than anything reconstructed here — an unmeetable pin arrives this way
                // rather than as a null, as does a mod that is not on ModDB at all.
                verdicts.Add(new ModVerdict(want.ModId, installed, null,
                    want.Version is null ? ModOutcome.Unavailable : ModOutcome.PinUnavailable,
                    e.Message));
                continue;
            }
            catch (HttpRequestException)
            {
                // Not reaching ModDB says nothing about the mod. Reporting that as "breaks"
                // would be a guess presented as a finding.
                //
                // The exception's own text ("Response status code does not indicate
                // success: 404 (Not Found)") is transport detail in a row about a mod, so
                // it goes to the log instead.
                verdicts.Add(new ModVerdict(want.ModId, installed, null, ModOutcome.Unknown,
                    Lang.Get("versionchange-no-answer")));
                continue;
            }

            verdicts.Add(Judge(want, installed, release, manifest.GameVersion, target));
        }

        return new VersionChangePlan(manifest.GameVersion, target, verdicts, worlds ?? []);
    }

    /// <param name="from">The version the pack is on now. See the approximate case below.</param>
    private static ModVerdict Judge(
        PackMod want, string? installed, ResolvedRelease? release, string from, string target)
    {
        if (release is null)
            return want.Version is null
                ? new ModVerdict(want.ModId, installed, null, ModOutcome.Unavailable,
                    Lang.Get("versionchange-no-release", target))
                : new ModVerdict(want.ModId, installed, null, ModOutcome.PinUnavailable,
                    Lang.Get("versionchange-pin-no-release", want.Version, target));

        // Approximate is worth reporting when the move causes it, and not otherwise. A
        // release marked for 1.22.3 is no more approximate at 1.22.5 than it already was at
        // 1.22.6 — flagging it as a consequence of the change says something untrue about
        // the change, and on a pack whose mods mostly trail the game version it is most of
        // the preview. A warning that appears against two thirds of the rows every time is
        // one nobody reads by the third version change.
        //
        // Null here means the release does not serve the version being left at all, which
        // is a real difference and stays reported: moving 1.21.7 → 1.22.5 genuinely puts
        // the mod on a release it was not on.
        var alreadyApproximate = release.QualityFor(from) == MatchQuality.SameMinor;

        if (release.Quality == MatchQuality.SameMinor && !alreadyApproximate)
            return new ModVerdict(want.ModId, installed, release.ModVersion, ModOutcome.Approximate,
                Lang.Get("versionchange-other-minor", Minor(target), target));

        // Still worth a word when it stays approximate — it is a standing fact about the
        // mod — but as part of "nothing happens" rather than as a consequence.
        var stays = release.Quality == MatchQuality.SameMinor
            ? $"; still marked for another {Minor(target)} release, as it was for {from}"
            : "";

        if (string.Equals(installed, release.ModVersion, StringComparison.OrdinalIgnoreCase))
            return new ModVerdict(want.ModId, installed, release.ModVersion, ModOutcome.Unchanged,
                Lang.Get("versionchange-already-on", target, stays));

        return new ModVerdict(want.ModId, installed, release.ModVersion, ModOutcome.Moves,
            (installed is null
                // Naming the version either way: the row shows this note and nothing else,
                // so "not installed yet" alone would hide what is about to be installed.
                ? Lang.Get("versionchange-would-install", release.ModVersion)
                : $"{installed} → {release.ModVersion}") + stays);
    }

    private static string Minor(string version)
    {
        var parts = version.Split('.');
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}.x" : version;
    }
}
