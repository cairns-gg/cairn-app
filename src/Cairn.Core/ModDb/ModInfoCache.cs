using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cairn.Core.ModDb;

/// <summary>The few things about a mod worth remembering between runs.</summary>
public sealed class ModInfo
{
    [JsonPropertyName("logo")] public string? Logo { get; set; }
    [JsonPropertyName("assetId")] public int AssetId { get; set; }
    [JsonPropertyName("urlAlias")] public string? UrlAlias { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

/// <summary>
/// Remembers what ModDB says about a mod, keyed by mod id.
///
/// A pack's manifest holds only ids, so drawing its rows with icons would otherwise cost
/// one API call per mod on every launch. The answers barely change, so they are kept on
/// disk beside the icons themselves — the second launch draws the list with no network at
/// all, and the same entries answer "open this mod's page" without a lookup.
///
/// Nothing here throws: this backs decoration, and a failure means "no icon", not a
/// broken pack.
/// </summary>
public sealed class ModInfoCache(ModDbClient moddb, string? root = null)
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly SemaphoreSlim _gate = new(1, 1);

    private Dictionary<string, ModInfo>? _entries;

    /// <summary>
    /// Read through to <see cref="CairnPaths"/> unless a root was given, rather than settled
    /// when the cache was built: the root can move while Cairn is running.
    /// </summary>
    private string Root => root ?? CairnPaths.CacheRoot;

    public string Path => System.IO.Path.Combine(Root, "mods.json");

    /// <summary>
    /// What is known about <paramref name="modId"/>, asking ModDB only if this is the
    /// first time. Null when the mod cannot be found or the network is unavailable.
    /// </summary>
    public async Task<ModInfo?> GetAsync(string modId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modId)) return null;

        var key = modId.Trim().ToLowerInvariant();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _entries ??= Load();
            if (_entries.TryGetValue(key, out var known)) return known;
        }
        finally
        {
            _gate.Release();
        }

        ModInfo info;
        try
        {
            var mod = await moddb.GetModAsync(modId, ct).ConfigureAwait(false);
            info = new ModInfo
            {
                Logo = mod.Logo,
                AssetId = mod.AssetId,
                UrlAlias = mod.UrlAlias,
                Name = mod.Name,
            };
        }
        catch (Exception e) when (e is ModDbException or HttpRequestException
                                      or TaskCanceledException or JsonException)
        {
            // Not remembered: a mod missing today may be found tomorrow, and caching the
            // absence would make that permanent.
            return null;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _entries ??= Load();
            _entries[key] = info;
            Save(_entries);
        }
        finally
        {
            _gate.Release();
        }

        return info;
    }

    /// <summary>What is already known, without asking ModDB.</summary>
    public ModInfo? Peek(string modId)
    {
        _entries ??= Load();
        return _entries.TryGetValue(modId.Trim().ToLowerInvariant(), out var info) ? info : null;
    }

    public void Clear()
    {
        _entries = [];

        // The client remembers mod documents for a few minutes of its own, and this is a
        // clear somebody asked for: leaving that in place would answer the next lookup
        // without asking ModDB, which is what the button exists to make happen.
        moddb.Forget();

        try
        {
            if (File.Exists(Path)) File.Delete(Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A cache that will not clear is not worth failing over.
        }
    }

    private Dictionary<string, ModInfo> Load()
    {
        try
        {
            if (!File.Exists(Path)) return new Dictionary<string, ModInfo>(StringComparer.OrdinalIgnoreCase);

            var parsed = JsonSerializer.Deserialize<Dictionary<string, ModInfo>>(
                File.ReadAllText(Path), Json);

            return parsed is null
                ? new Dictionary<string, ModInfo>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ModInfo>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable: start over rather than refusing to run.
            return new Dictionary<string, ModInfo>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save(Dictionary<string, ModInfo> entries)
    {
        try
        {
            Directory.CreateDirectory(Root);

            // Written aside and moved, so a crash mid-write cannot leave a half file that
            // then fails to parse on the next run.
            var staging = System.IO.Path.Combine(Root, System.IO.Path.GetRandomFileName());
            File.WriteAllText(staging, JsonSerializer.Serialize(entries, Json));
            File.Move(staging, Path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Remembering is an optimisation; failing to is survivable.
        }
    }
}
