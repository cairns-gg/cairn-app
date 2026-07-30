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

public sealed class ModDbClient(HttpClient http)
{
    private const string ApiBase = "https://mods.vintagestory.at/api";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ModDbMod> GetModAsync(string modId, CancellationToken ct = default)
    {
        var resp = await http.GetFromJsonAsync<ModDbModResponse>(
                           $"{ApiBase}/mod/{Uri.EscapeDataString(modId)}", Json, ct)
                       .ConfigureAwait(false)
                   ?? throw new ModDbException($"ModDB returned no body for '{modId}'.");

        if (resp.Mod is null)
            throw new ModDbException($"ModDB has no mod with id '{modId}' (status {resp.StatusCode}).");

        return resp.Mod;
    }

    public async Task<List<ModDbSearchEntry>> SearchAsync(string text, CancellationToken ct = default)
    {
        var resp = await http.GetFromJsonAsync<ModDbSearchResponse>(
                $"{ApiBase}/mods?text={Uri.EscapeDataString(text)}", Json, ct)
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
    public async Task<List<ModDbSearchEntry>> SearchRankedAsync(
        string text, CancellationToken ct = default)
    {
        var query = text.Trim();
        var ranked = Rank(await SearchAsync(query, ct).ConfigureAwait(false), query);

        // A mod may exist under exactly this id and still be missing from the text
        // results, so confirm directly rather than reporting "no results".
        if (!ranked.Any(r => HasExactId(r, query)))
        {
            try
            {
                var mod = await GetModAsync(query, ct).ConfigureAwait(false);
                ranked.Insert(0, AsSearchEntry(mod, query));
            }
            catch (Exception e) when (e is ModDbException or HttpRequestException)
            {
                // No mod with that id; the ranked text results stand on their own.
            }
        }

        return ranked;
    }

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
            r.ModVersion, r.FileName, r.MainFile, r.ReleaseId, r.FileId, q, mod.Side);

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
