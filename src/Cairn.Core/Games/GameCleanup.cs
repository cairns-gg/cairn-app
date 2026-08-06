using Cairn.Core.Packs;
using Cairn.Core.Runtime;

namespace Cairn.Core.Games;

/// <summary>One thing cleanup would delete.</summary>
public sealed record CleanupTarget(string Label, string Directory, long Bytes);

/// <summary>
/// What cleanup would remove, worked out without deleting anything.
///
/// Installing a game version per pack adds up fast — 600 MB each, and a pack retargeted
/// twice leaves two behind that nothing points at any more. Nothing here is irreplaceable:
/// every version is re-downloadable and Play fetches whatever a pack needs. That is what
/// makes this safe to offer, and it is still shown in full before anything happens.
/// </summary>
public sealed record CleanupPlan(
    IReadOnlyList<CleanupTarget> Versions,
    IReadOnlyList<CleanupTarget> Runtimes,
    IReadOnlyList<string> Kept,
    string? Blocked = null)
{
    public bool AnythingToDo => Versions.Count > 0 || Runtimes.Count > 0 || Caches.Count > 0;

    /// <summary>
    /// Re-fetchable caches — icons, mod details. Not a game concern, so they are supplied
    /// by the caller rather than discovered here, but they belong in the same sweep: two
    /// buttons for "delete things that come back on their own" is one too many.
    /// </summary>
    public IReadOnlyList<CleanupTarget> Caches { get; init; } = [];

    /// <summary>
    /// Working trees for clients built from source — reported, never swept.
    ///
    /// The largest thing Cairn writes and the only one that does not come back on its own,
    /// so it fails this sweep's test on both counts at once: too big to leave unmentioned,
    /// too expensive to delete without being asked. Listed so the disk it uses is visible
    /// and can be reclaimed deliberately.
    /// </summary>
    public IReadOnlyList<CleanupTarget> BuildTrees { get; init; } = [];

    /// <summary>Set when the question could not be answered, as distinct from "nothing to do".</summary>
    public bool IsBlocked => Blocked is not null;

    public long TotalBytes =>
        Versions.Sum(v => v.Bytes) + Runtimes.Sum(r => r.Bytes) + Caches.Sum(c => c.Bytes);

    /// <summary>The lines a confirmation shows: every item, named, with its size.</summary>
    public IReadOnlyList<string> Describe()
    {
        var lines = new List<string>();

        foreach (var v in Versions)
            lines.Add($"Vintage Story {v.Label} ({Bytes.Human(v.Bytes)})");

        foreach (var r in Runtimes)
            lines.Add($".NET {r.Label} ({Bytes.Human(r.Bytes)}) — nothing left needs it");

        foreach (var c in Caches)
            lines.Add($"{c.Label} ({Bytes.Human(c.Bytes)})");

        return lines;
    }
}

public static class GameCleanup
{
    /// <summary>
    /// Game versions no pack targets, plus any private .NET runtime left with nothing to
    /// run — removing a version and leaving its runtime behind is not much of a cleanup.
    ///
    /// Only ever installs Cairn made. An install found on the machine is not Cairn's to
    /// delete, and GameStore.ListInstalled is the only thing consulted here.
    /// </summary>
    public static CleanupPlan Plan(GameStore games, RuntimeStore runtimes, PackStore packs)
    {
        var (used, unreadable) = VersionsUsedBy(packs);

        // A pack whose manifest will not load might need any version at all. Sweeping on
        // that basis could delete the one thing it was about to launch, so this refuses to
        // guess rather than quietly treating the pack as needing nothing.
        if (unreadable.Count > 0)
            return new CleanupPlan([], [], [],
                Blocked: $"Cannot read {string.Join(", ", unreadable)}, so what is unused is unknown.");

        return Plan(games, runtimes, used) with { BuildTrees = BuildTreesUnder(CairnPaths.BuildsRoot) };
    }

    /// <summary>
    /// Working trees under the builds root, one entry each, with what they occupy.
    ///
    /// Measured rather than assumed: a tree that has been bootstrapped is several
    /// gigabytes and one that was cancelled early may be a few hundred megabytes, and the
    /// point of listing them is to say which.
    /// </summary>
    public static IReadOnlyList<CleanupTarget> BuildTreesUnder(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return [];

            return
            [
                .. Directory.EnumerateDirectories(root)
                    .OrderBy(d => d, StringComparer.Ordinal)
                    .Select(d => new CleanupTarget(
                        Path.GetFileName(d), d, DirectoryGrowth.Measure(d)))
                    .Where(t => t.Bytes > 0)
            ];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static CleanupPlan Plan(
        GameStore games, RuntimeStore runtimes, IEnumerable<string> keepVersions)
    {
        var keep = new HashSet<string>(keepVersions, StringComparer.OrdinalIgnoreCase);

        var installed = games.ListInstalled().ToList();

        // A variant is kept whatever any pack targets. This sweep's whole licence is that
        // nothing in it is irreplaceable — every version is a re-download, and Play fetches
        // what a pack needs — and a client built from source is the one thing here that is
        // not: it costs twenty minutes of compiling and several gigabytes of working tree
        // to make again. Swept on the same rule as a download, it would vanish the moment
        // the last pack using it was retargeted, from a button offering to tidy up.
        var kept = installed
            .Where(i => i.IsVariant || keep.Contains(i.Version))
            .ToList();

        var versions = installed
            .Where(i => !i.IsVariant && !keep.Contains(i.Version))
            .Select(i => new CleanupTarget(i.Version, i.Directory, DirectoryGrowth.Measure(i.Directory)))
            .ToList();

        var orphaned = runtimes.ListInstalled()
            // Kept rather than deleted-from: a runtime is worth keeping if anything that
            // survives could use it. Over-keeping costs disk; over-deleting costs a launch.
            .Where(r => !kept.Any(i => Serves(r, i)))
            .Select(r => new CleanupTarget(
                Path.GetFileName(r.Root), r.Root, DirectoryGrowth.Measure(r.Root)))
            .ToList();

        // Distinct, because a variant reports the version it was built from and would
        // otherwise list "1.22.5" twice beside the stock install of the same version.
        return new CleanupPlan(versions, orphaned,
            [.. kept.Select(i => i.Describe).Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    private static bool Serves(DotnetRuntime runtime, GameInstall install) =>
        runtime.Satisfies(install.RequiredFramework)
        && (runtime.Arch == install.Architecture || runtime.Arch == ExecutableArch.Unknown);

    /// <summary>Every game version some pack points at, and the packs that could not be read.</summary>
    private static (List<string> Used, List<string> Unreadable) VersionsUsedBy(PackStore packs)
    {
        List<string> used = [];
        List<string> unreadable = [];

        foreach (var id in packs.ListIds())
        {
            try
            {
                used.Add(packs.Load(id).GameVersion);
            }
            catch (Exception e) when (e is IOException or InvalidDataException
                                          or System.Text.Json.JsonException)
            {
                unreadable.Add(id);
            }
        }

        return (used, unreadable);
    }
}
