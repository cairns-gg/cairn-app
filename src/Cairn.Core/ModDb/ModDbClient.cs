using System.Net.Http.Json;
using System.Text.Json;

namespace Cairn.Core.ModDb;

/// <summary>How well a release matches the game version we are resolving for.</summary>
public enum MatchQuality
{
    /// <summary>The release explicitly lists this exact game version.</summary>
    Exact,

    /// <summary>
    /// The release lists a version sharing our major.minor. The game itself treats
    /// same-minor releases as installable (ModDbUtil tracks sameMinorVersionIds),
    /// so we accept these but rank them below an exact match.
    /// </summary>
    SameMinor,

    /// <summary>
    /// The release is marked for no version resembling this pack's, and is being installed
    /// anyway because somebody said so.
    /// </summary>
    /// <remarks>
    /// Never reached by resolving alone: a resolve refuses these, and only a mod carrying an
    /// acceptance in the manifest asks for them. It exists as a quality rather than a
    /// bypass flag so that everything downstream — the sync report, the lock, the version
    /// change preview — can see what it got, instead of an unmarked release arriving
    /// indistinguishable from a matched one.
    /// </remarks>
    Unmarked,
}

/// <param name="ModId">
/// The id this was asked for by, which is the id the pack names it by and the one the lock
/// must record. Deliberately not the release's own <c>modidstr</c>: ModDB answers to more
/// than one id per mod, and "hpspinningwheel" returns releases declaring "spinningwheel".
/// Locking the declared id meant the manifest and the lock could never be compared — every
/// sync re-downloaded the mod, and sharing refused a pack that had just synced.
/// </param>
/// <param name="DeclaredModId">
/// What the release calls itself, when that differs. Only used to recognise the same mod
/// arriving again as somebody else's dependency; nothing is keyed on it.
/// </param>
public sealed record ResolvedRelease(
    string ModId,
    string ModVersion,
    string FileName,
    string Url,
    int ReleaseId,
    int FileId,
    MatchQuality Quality,
    string? Side,
    string? DeclaredModId = null,

    /// <summary>
    /// The game versions this release is marked for, as ModDB lists them.
    ///
    /// Carried so a caller can ask how well it matches some *other* version without a
    /// second request. <see cref="Quality"/> only ever describes the version that was
    /// resolved for, and "would this release be as good a match for the version we are
    /// leaving?" is a question the version-change preview has to answer for every mod.
    /// </summary>
    IReadOnlyList<string>? GameVersions = null,

    /// <summary>When it was published, as ModDB formats it. Null when it did not say.</summary>
    string? Created = null)
{
    /// <summary>
    /// How well this release matches an arbitrary game version, or null if it does not
    /// serve it at all. Null also when the tags were not carried.
    /// </summary>
    public MatchQuality? QualityFor(string gameVersion) =>
        GameVersions is null ? null : ModDbClient.Classify(GameVersions, gameVersion);
}

public sealed class ModDbException(string message) : Exception(message);

/// <summary>
/// A search result, and whether it has a release the pack's game version can use.
///
/// Both are reported rather than the incompatible ones being dropped: "this mod exists
/// but has no 1.22.x release yet" is useful, and silently missing results just move the
/// confusion somewhere harder to see.
/// </summary>
public sealed record ModSearchResult(ModDbSearchEntry Mod, bool Compatible);

/// <param name="now">
/// Where <see cref="ExistsAsync"/> reads the time from. Injected so a test can age an
/// answer out without sleeping through <see cref="ExistsLifetime"/>.
/// </param>
public sealed class ModDbClient(HttpClient http, Func<DateTimeOffset>? now = null)
{
    private const string ApiBase = "https://mods.vintagestory.at/api";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    /// <summary>
    /// The site's game-version list, fetched once per process. It is 245 entries that
    /// change only when the game ships a release, so re-fetching it per search would be
    /// pure waste.
    /// </summary>
    private IReadOnlyList<ModDbGameVersion>? _gameVersions;

    /// <summary>
    /// How long a fetched mod document stands before it is asked for again.
    ///
    /// The same ten minutes <see cref="Packs.ModUpdateCache"/> keeps an update check for,
    /// and for the same reason: long enough to cover a preview and the sync that follows
    /// it, short enough that a mod published just now and somebody looking for it are not
    /// far apart.
    /// </summary>
    public static readonly TimeSpan DocumentLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// What ModDB last said about each mod, keyed by the id it was asked for.
    ///
    /// One pack's worth of these is small — measured over the 7,900-mod corpus
    /// <c>tools/moddb-audit.cs</c> keeps, the median document is under 4KB and the mean
    /// around 9KB, so a seventy-mod pack is well under a megabyte. In memory rather than on
    /// disk, so every caller gets it without being wired up to anything, including
    /// <c>cairn-cli</c>, and the storage page has nothing new to explain.
    ///
    /// This does reach the install path, which <see cref="Packs.ModUpdateCache"/> refuses
    /// for itself — and the distinction is worth being exact about, because the two are not
    /// the same trade. That cache remembers a *decision* ("carryon should move to 1.2.1")
    /// and replays it later under conditions that may no longer hold. This remembers the
    /// *document* a decision is made from; the resolve still runs fresh every time, against
    /// the game version, pin and acceptance in force at that moment.
    ///
    /// It is also what makes a preview true. <c>GameVersionChange.PreviewAsync</c> resolves
    /// every mod against the target and promises to be "the same call, same arguments" as
    /// the sync that follows — and used to re-fetch every document to do it. An author
    /// publishing in between made the sync install something the preview never showed.
    /// Sharing the document is what makes them agree rather than merely intend to.
    ///
    /// What must never come from here is a resolve whose whole purpose is to find the
    /// newest release: see the <c>fresh</c> parameter on <see cref="GetModAsync"/>.
    /// </summary>
    private readonly Dictionary<string, (ModDbMod Mod, DateTimeOffset At)> _documents =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fetches and parses one API response, reporting a body we cannot read as a
    /// <see cref="ModDbException"/> rather than a <c>JsonException</c>.
    ///
    /// The shapes here mirror a third party's API, so an unreadable field is the same
    /// class of problem as an unreachable host — something ModDB did, not a bug in the
    /// caller. Callers already handle ModDbException by failing just the mod they asked
    /// about; a raw JsonException escaped them all and took the whole operation with it.
    /// </summary>
    private async Task<T?> GetAsync<T>(string url, CancellationToken ct)
    {
        try
        {
            return await http.GetFromJsonAsync<T>(url, Json, ct).ConfigureAwait(false);
        }
        catch (JsonException e)
        {
            throw new ModDbException(Lang.Get("moddb-unreadable", e.Message));
        }
    }

    /// <param name="fresh">
    /// Ignore anything remembered and ask ModDB. For the callers whose question is "what is
    /// the newest release" — an explicit update, above all — where being handed an answer
    /// from ten minutes ago is not a saving but a wrong answer to what was asked, recorded
    /// in the lock and then reported back as up to date.
    /// </param>
    public async Task<ModDbMod> GetModAsync(
        string modId, CancellationToken ct = default, bool fresh = false)
    {
        if (!fresh && Remembered(modId) is { } known) return known;

        var resp = await GetAsync<ModDbModResponse>(
                           $"{ApiBase}/mod/{Uri.EscapeDataString(modId)}", ct)
                       .ConfigureAwait(false)
                   ?? throw new ModDbException(Lang.Get("moddb-no-body", modId));

        if (resp.Mod is null)
            throw new ModDbException(Lang.Get("moddb-no-such-mod", modId, resp.StatusCode));

        Remember(modId, resp.Mod);

        return resp.Mod;
    }

    /// <summary>
    /// Whether ModDB publishes this mod at all — not whether it has a release for any
    /// particular game version.
    ///
    /// Separate from <see cref="GetModAsync"/> because "no such mod" and "ModDB could not
    /// be reached" both arrive as a ModDbException there, and a caller asking this question
    /// needs them apart: one means recipients cannot install it, the other means we do not
    /// know. Transport failures are rethrown rather than reported as absence.
    /// </summary>
    public async Task<bool> ExistsAsync(string modId, CancellationToken ct = default)
    {
        // A document in hand is the answer. This is what makes publishing cheap: the sync
        // resolves every mod, and the existence sweep that follows asks ModDB nothing.
        if (Remembered(modId) is not null) return true;

        var resp = await GetAsync<ModDbModResponse>(
                $"{ApiBase}/mod/{Uri.EscapeDataString(modId)}", ct)
            .ConfigureAwait(false);

        if (resp?.Mod is null) return false;

        // Only mods that were found. An absence is the answer that goes stale in the
        // direction that matters — a mod author publishes the mod their pack names and is
        // told for another ten minutes that recipients cannot install it — and it is also
        // the cheap case, because a pack whose ids are all real never records one.
        Remember(modId, resp.Mod);

        return true;
    }

    /// <summary>What was fetched recently enough to still stand, or null.</summary>
    private ModDbMod? Remembered(string modId)
    {
        lock (_documents)
        {
            if (!_documents.TryGetValue(modId, out var known)) return null;

            var now = _now();

            // A clock that has gone backwards — a correction, a laptop resumed — would
            // otherwise hold a document open for as long as the skew lasts. The same guard
            // ModUpdateCache.Get makes, for the same reason.
            if (known.At > now || now - known.At > DocumentLifetime)
            {
                _documents.Remove(modId);
                return null;
            }

            return known.Mod;
        }
    }

    private void Remember(string modId, ModDbMod mod)
    {
        // Locked because nothing else about this class is: the launcher can be drawing mod
        // rows on one thread while a sync runs on another, both through this client, and a
        // Dictionary written from two at once does not merely lose an entry.
        lock (_documents) _documents[modId] = (mod, _now());
    }

    /// <summary>
    /// Forgets every remembered document, so the next question reaches ModDB.
    ///
    /// For clearing that somebody asked for. <see cref="ModInfoCache.Clear"/> deletes what
    /// it knows about mods so the next lookup asks again — and this would go on answering
    /// from memory for another ten minutes, which makes the button somebody pressed a lie.
    /// </summary>
    public void Forget()
    {
        lock (_documents) _documents.Clear();
    }

    /// <summary>Every game version ModDB knows, with the tag ids searching by version needs.</summary>
    public async Task<IReadOnlyList<ModDbGameVersion>> GetGameVersionsAsync(CancellationToken ct = default)
    {
        if (_gameVersions is not null) return _gameVersions;

        var resp = await GetAsync<ModDbGameVersionsResponse>(
            $"{ApiBase}/gameversions", ct).ConfigureAwait(false);

        return _gameVersions = resp?.GameVersions ?? [];
    }

    /// <summary>
    /// Tag ids for every version sharing <paramref name="gameVersion"/>'s major.minor.
    ///
    /// Deliberately the whole minor rather than the exact patch. The engine ships patch
    /// releases that rarely break anything, so most mods are marked for x.y.0 and never
    /// re-tagged — measured against the live API, filtering "olla" to exactly 1.22.5 gave
    /// 49 results where the full 1.22.x range gave 248, and Cairn installs every one of
    /// those 248 quite happily. This is the same rule Classify already applies when
    /// resolving a release, so search and install agree.
    /// </summary>
    public async Task<IReadOnlyList<long>> GameVersionTagsForMinorAsync(
        string gameVersion, CancellationToken ct = default)
    {
        var all = await GetGameVersionsAsync(ct).ConfigureAwait(false);

        return all.Where(v => IsSameMinor(v.Name, gameVersion))
            .Select(v => v.TagId)
            .ToList();
    }

    private static bool IsSameMinor(string candidate, string gameVersion)
    {
        try
        {
            return GameVersions.IsSameMajorMinor(candidate, gameVersion);
        }
        catch (ArgumentNullException)
        {
            return false;   // an empty name in the list; not a match for anything
        }
    }

    /// <summary>
    /// Text search, optionally restricted to mods with a release for
    /// <paramref name="gameVersion"/>'s minor.
    /// </summary>
    public async Task<List<ModDbSearchEntry>> SearchAsync(
        string text, string? gameVersion = null, CancellationToken ct = default)
    {
        var query = $"{ApiBase}/mods?text={Uri.EscapeDataString(text)}";

        if (gameVersion is not null)
        {
            var tags = await GameVersionTagsForMinorAsync(gameVersion, ct).ConfigureAwait(false);

            // No tags means the version is unknown to ModDB. Searching unfiltered beats
            // sending no tags, which the API answers with everything anyway.
            foreach (var tag in tags) query += $"&gameversions[]={tag}";
        }

        var resp = await GetAsync<ModDbSearchResponse>(query, ct)
            .ConfigureAwait(false);

        return resp?.Mods ?? [];
    }

    /// <summary>
    /// Search, ordered so the thing you typed comes first.
    ///
    /// ModDB's text search matches mod descriptions, so a short query like "olla" returns
    /// hundreds of hits ("collar", "follower", …) in an order that ignores relevance
    /// entirely — the mod actually called Olla comes back 194th. Ranking has to happen
    /// here.
    /// </summary>
    public async Task<List<ModSearchResult>> SearchRankedAsync(
        string text, string? gameVersion = null, CancellationToken ct = default)
    {
        var query = text.Trim();

        // Two searches rather than one: the same query filtered to the game version, and
        // unfiltered. The difference between them is exactly the set of mods with no
        // usable release, which the per-result API does not otherwise tell us — and it
        // costs two requests instead of one lookup per result.
        var all = SearchAsync(query, null, ct);
        var usable = gameVersion is null ? null : SearchAsync(query, gameVersion, ct);

        var entries = await all.ConfigureAwait(false);
        var compatibleIds = usable is null
            ? null
            : (await usable.ConfigureAwait(false)).Select(m => m.AssetId).ToHashSet();

        bool IsCompatible(ModDbSearchEntry m) => compatibleIds is null || compatibleIds.Contains(m.AssetId);

        var ranked = Rank(entries, query)
            .Select(m => new ModSearchResult(m, IsCompatible(m)))
            // Usable first, and within each group the relevance order Rank already chose.
            .OrderBy(r => r.Compatible ? 0 : 1)
            .ToList();

        // A mod may exist under exactly this id and still be missing from the text
        // results, so confirm directly rather than reporting "no results".
        if (!ranked.Any(r => HasExactId(r.Mod, query)))
        {
            try
            {
                var mod = await GetModAsync(query, ct).ConfigureAwait(false);
                var compatible = gameVersion is null || HasReleaseFor(mod, gameVersion);

                // Named directly, so it goes to the top of its group either way.
                ranked.Insert(compatible ? 0 : ranked.Count,
                    new ModSearchResult(AsSearchEntry(mod, query), compatible));
            }
            catch (Exception e) when (e is ModDbException or HttpRequestException)
            {
                // No mod with that id; the ranked text results stand on their own.
            }
        }

        return ranked;
    }

    /// <summary>Whether any release of the mod is usable on that game version.</summary>
    public static bool HasReleaseFor(ModDbMod mod, string gameVersion) =>
        mod.Releases.Any(r => Classify(r, gameVersion) is not null);

    /// <summary>Best match first. Pure, so the ordering is testable without network.</summary>
    public static List<ModDbSearchEntry> Rank(IEnumerable<ModDbSearchEntry> results, string query) =>
        results
            .OrderBy(r => Relevance(r, query))
            .ThenByDescending(r => r.Downloads)
            .ToList();

    /// <summary>Lower is better.</summary>
    public static int Relevance(ModDbSearchEntry entry, string query)
    {
        var q = query.Trim();
        if (q.Length == 0) return 9;

        var ids = entry.ModIdStrs ?? [];
        var name = entry.Name ?? "";

        if (ids.Any(i => string.Equals(i, q, StringComparison.OrdinalIgnoreCase))) return 0;
        if (string.Equals(name, q, StringComparison.OrdinalIgnoreCase)) return 1;
        if (ids.Any(i => i.StartsWith(q, StringComparison.OrdinalIgnoreCase))) return 2;
        if (name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 3;
        if (ids.Any(i => i.Contains(q, StringComparison.OrdinalIgnoreCase))) return 4;
        if (name.Contains(q, StringComparison.OrdinalIgnoreCase)) return 5;

        // Matched only somewhere in the description.
        return 6;
    }

    private static bool HasExactId(ModDbSearchEntry entry, string query) =>
        (entry.ModIdStrs ?? []).Any(i => string.Equals(i, query, StringComparison.OrdinalIgnoreCase));

    private static ModDbSearchEntry AsSearchEntry(ModDbMod mod, string modId) => new()
    {
        NumericId = mod.NumericId,
        Name = mod.Name,
        Author = mod.Author,
        Side = mod.Side,
        ModIdStrs = [modId],
        Summary = null,
    };

    /// <summary>
    /// Every release of <paramref name="modId"/> usable on <paramref name="gameVersion"/>,
    /// best first. Backs the version picker in the launcher.
    /// </summary>
    public async Task<List<ResolvedRelease>> ListCompatibleReleasesAsync(
        string modId, string gameVersion, CancellationToken ct = default)
    {
        var mod = await GetModAsync(modId, ct).ConfigureAwait(false);

        return Rank(Candidates(mod, gameVersion))
            .Select(x => ToResolved(mod, x.Release, x.Quality!.Value, modId))
            .ToList();
    }

    /// <summary>
    /// Pick the best release of <paramref name="modId"/> for <paramref name="gameVersion"/>.
    /// Exact game-version matches win; among equals the highest mod version wins.
    /// Returns null when the mod has no release for this game version at all.
    /// </summary>
    /// <param name="acceptUnmarked">
    /// Whether to consider releases marked for no version like this pack's. Off unless
    /// somebody has vouched for this mod — the manifest carrying an acceptance, or another
    /// mod requiring it by id. Testimony, in other words, rather than a thing to infer;
    /// <c>PackSyncer.PendingMod.AcceptsUnmarked</c> is where it is decided.
    /// </param>
    /// <param name="fresh">
    /// Ask ModDB rather than reuse a remembered document. Set by callers looking for the
    /// newest release — see <see cref="GetModAsync"/>.
    /// </param>
    public async Task<ResolvedRelease?> ResolveAsync(
        string modId, string gameVersion, string? pinnedVersion = null,
        CancellationToken ct = default, bool acceptUnmarked = false, bool fresh = false)
    {
        // Asked before the fetch, because after one the document is always remembered and
        // the answer would be yes however it arrived.
        var reused = !fresh && Remembered(modId) is not null;

        var mod = await GetModAsync(modId, ct, fresh).ConfigureAwait(false);

        var candidates = Candidates(mod, gameVersion, acceptUnmarked);

        if (pinnedVersion is not null)
        {
            var pinned = candidates.FirstOrDefault(x => x.Release.ModVersion == pinnedVersion);

            // A remembered document that cannot satisfy a pin is asked again before the pin
            // is refused. Otherwise pinning a release published in the last few minutes —
            // by hand in pack.json, or from a list drawn before it landed — is told the
            // release does not exist, which is both wrong and unactionable. Only ever one
            // retry, and only when the document was reused: a pin that is genuinely not
            // there must still cost one request, not two.
            if (pinned.Release is null && reused)
            {
                mod = await GetModAsync(modId, ct, fresh: true).ConfigureAwait(false);
                candidates = Candidates(mod, gameVersion, acceptUnmarked);
                pinned = candidates.FirstOrDefault(x => x.Release.ModVersion == pinnedVersion);
            }

            // A pin is authoritative: take that version even if it is not the newest,
            // but still refuse one that is not marked for this game version.
            if (pinned.Release is null)
            {
                var exists = mod.Releases.Any(r => r.ModVersion == pinnedVersion);
                throw new ModDbException(exists
                    ? Lang.Get("moddb-not-marked", modId, pinnedVersion, gameVersion)
                    : Lang.Get("moddb-no-release", modId, pinnedVersion));
            }

            return ToResolved(mod, pinned.Release, pinned.Quality!.Value, modId);
        }

        var best = Rank(candidates).FirstOrDefault();

        return best.Release is null ? null : ToResolved(mod, best.Release, best.Quality!.Value, modId);
    }

    private static List<(ModDbRelease Release, MatchQuality? Quality)> Candidates(
        ModDbMod mod, string gameVersion, bool acceptUnmarked = false) =>
        mod.Releases
            // ModDB keeps the row for a release whose file has gone, serving it with an
            // empty mainfile. It is not a thing that can be installed, so it must not win
            // a resolve — picking one would fail the download for a mod that does have a
            // usable release sitting right behind it.
            .Where(r => !string.IsNullOrWhiteSpace(r.MainFile))
            .Select(r => (Release: r,
                Quality: Classify(r, gameVersion)
                         ?? (acceptUnmarked ? MatchQuality.Unmarked : null)))
            .Where(x => x.Quality is not null)
            .ToList();

    /// <summary>
    /// Best first: an exact match, then the same minor, then — only ever when asked for —
    /// something marked for neither. Newest within each.
    /// </summary>
    private static IEnumerable<(ModDbRelease Release, MatchQuality? Quality)> Rank(
        List<(ModDbRelease Release, MatchQuality? Quality)> candidates) =>
        candidates
            .OrderBy(x => x.Quality switch
            {
                MatchQuality.Exact => 0,
                MatchQuality.SameMinor => 1,
                _ => 2,
            })
            .ThenByDescending(x => x.Release.ModVersion, GameVersionComparer.Ascending);

    private static ResolvedRelease ToResolved(
        ModDbMod mod, ModDbRelease r, MatchQuality q, string requestedId) =>
        new(string.IsNullOrWhiteSpace(requestedId) ? mod.Name : requestedId,
            r.ModVersion, r.FileName, r.MainFile, r.ReleaseId ?? 0, r.FileId ?? 0, q, mod.Side,
            DeclaredModId: string.IsNullOrWhiteSpace(r.ModIdStr) ? null : r.ModIdStr,
            GameVersions: r.Tags,
            Created: r.Created);

    private static MatchQuality? Classify(ModDbRelease r, string gameVersion) =>
        Classify(r.Tags, gameVersion);

    /// <summary>
    /// How well a set of game-version tags matches one version. Public so
    /// <see cref="ResolvedRelease.QualityFor"/> answers it the same way a resolve does —
    /// two implementations of "does this release serve 1.22.5" is one too many.
    /// </summary>
    public static MatchQuality? Classify(IEnumerable<string> tags, string gameVersion)
    {
        var all = tags as IReadOnlyList<string> ?? [.. tags];

        if (all.Any(t => t == gameVersion)) return MatchQuality.Exact;

        foreach (var t in all)
        {
            try
            {
                if (GameVersions.IsSameMajorMinor(t, gameVersion)) return MatchQuality.SameMinor;
            }
            catch (ArgumentNullException)
            {
                // Empty tag on ModDB; ignore rather than fail the whole resolve.
            }
        }

        return null;
    }
}
