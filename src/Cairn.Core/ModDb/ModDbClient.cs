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
}

public sealed record ResolvedRelease(
    string ModId,
    string ModVersion,
    string FileName,
    string Url,
    int ReleaseId,
    int FileId,
    MatchQuality Quality,
    string? Side);

public sealed class ModDbException(string message) : Exception(message);

/// <summary>
/// A search result, and whether it has a release the pack's game version can use.
///
/// Both are reported rather than the incompatible ones being dropped: "this mod exists
/// but has no 1.22.x release yet" is useful, and silently missing results just move the
/// confusion somewhere harder to see.
/// </summary>
public sealed record ModSearchResult(ModDbSearchEntry Mod, bool Compatible);

public sealed class ModDbClient(HttpClient http)
{
    private const string ApiBase = "https://mods.vintagestory.at/api";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The site's game-version list, fetched once per process. It is 245 entries that
    /// change only when the game ships a release, so re-fetching it per search would be
    /// pure waste.
    /// </summary>
    private IReadOnlyList<ModDbGameVersion>? _gameVersions;

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
            throw new ModDbException($"ModDB returned a response Cairn could not read: {e.Message}");
        }
    }

    public async Task<ModDbMod> GetModAsync(string modId, CancellationToken ct = default)
    {
        var resp = await GetAsync<ModDbModResponse>(
                           $"{ApiBase}/mod/{Uri.EscapeDataString(modId)}", ct)
                       .ConfigureAwait(false)
                   ?? throw new ModDbException($"ModDB returned no body for '{modId}'.");

        if (resp.Mod is null)
            throw new ModDbException($"ModDB has no mod with id '{modId}' (status {resp.StatusCode}).");

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
        var resp = await GetAsync<ModDbModResponse>(
                $"{ApiBase}/mod/{Uri.EscapeDataString(modId)}", ct)
            .ConfigureAwait(false);

        return resp?.Mod is not null;
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
            .Select(x => ToResolved(mod, x.Release, x.Quality!.Value))
            .ToList();
    }

    /// <summary>
    /// Pick the best release of <paramref name="modId"/> for <paramref name="gameVersion"/>.
    /// Exact game-version matches win; among equals the highest mod version wins.
    /// Returns null when the mod has no release for this game version at all.
    /// </summary>
    public async Task<ResolvedRelease?> ResolveAsync(
        string modId, string gameVersion, string? pinnedVersion = null, CancellationToken ct = default)
    {
        var mod = await GetModAsync(modId, ct).ConfigureAwait(false);

        var candidates = Candidates(mod, gameVersion);

        if (pinnedVersion is not null)
        {
            // A pin is authoritative: take that version even if it is not the newest,
            // but still refuse one that is not marked for this game version.
            var pinned = candidates.FirstOrDefault(x => x.Release.ModVersion == pinnedVersion);
            if (pinned.Release is null)
            {
                var exists = mod.Releases.Any(r => r.ModVersion == pinnedVersion);
                throw new ModDbException(exists
                    ? $"{modId} {pinnedVersion} exists but is not marked for game {gameVersion}."
                    : $"{modId} has no release {pinnedVersion}.");
            }

            return ToResolved(mod, pinned.Release, pinned.Quality!.Value);
        }

        var best = Rank(candidates).FirstOrDefault();

        return best.Release is null ? null : ToResolved(mod, best.Release, best.Quality!.Value);
    }

    private static List<(ModDbRelease Release, MatchQuality? Quality)> Candidates(
        ModDbMod mod, string gameVersion) =>
        mod.Releases
            // ModDB keeps the row for a release whose file has gone, serving it with an
            // empty mainfile. It is not a thing that can be installed, so it must not win
            // a resolve — picking one would fail the download for a mod that does have a
            // usable release sitting right behind it.
            .Where(r => !string.IsNullOrWhiteSpace(r.MainFile))
            .Select(r => (Release: r, Quality: Classify(r, gameVersion)))
            .Where(x => x.Quality is not null)
            .ToList();

    private static IEnumerable<(ModDbRelease Release, MatchQuality? Quality)> Rank(
        List<(ModDbRelease Release, MatchQuality? Quality)> candidates) =>
        candidates
            .OrderBy(x => x.Quality == MatchQuality.Exact ? 0 : 1)
            .ThenByDescending(x => x.Release.ModVersion, GameVersionComparer.Ascending);

    private static ResolvedRelease ToResolved(ModDbMod mod, ModDbRelease r, MatchQuality q) =>
        new(string.IsNullOrEmpty(r.ModIdStr) ? mod.Name : r.ModIdStr,
            r.ModVersion, r.FileName, r.MainFile, r.ReleaseId ?? 0, r.FileId ?? 0, q, mod.Side);

    private static MatchQuality? Classify(ModDbRelease r, string gameVersion)
    {
        if (r.Tags.Any(t => t == gameVersion)) return MatchQuality.Exact;

        foreach (var t in r.Tags)
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
