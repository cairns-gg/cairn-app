using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cairn.Core.Packs;

/// <summary>What a check found, and enough to know whether it still applies.</summary>
public sealed class CachedUpdates
{
    [JsonPropertyName("checkedAt")] public DateTimeOffset CheckedAt { get; set; }

    /// <summary>
    /// Identifies the pack this answer was computed for. See
    /// <see cref="ModUpdateCache.Fingerprint"/>.
    /// </summary>
    [JsonPropertyName("fingerprint")] public string Fingerprint { get; set; } = "";

    [JsonPropertyName("updates")] public List<ModUpdate> Updates { get; set; } = [];
}

/// <summary>
/// Remembers what "check for mod updates" answered, briefly.
///
/// The check costs one ModDB request per unpinned mod — thirty for an ordinary pack, every
/// time the button is pressed. Nothing about the answer changes second to second, so
/// pressing it twice is thirty requests spent to be told the same thing, against an API
/// that publishes no rate limit and whose bandwidth somebody else pays for.
///
/// Deliberately caches the <em>answer</em> and not the underlying resolutions. A cached
/// resolution would reach <see cref="PackSyncer.SyncAsync"/>, which installs mods — and
/// stale data on the install path is a different order of mistake from stale data in a
/// report. This only ever short-circuits a read.
/// </summary>
public sealed class ModUpdateCache
{
    /// <summary>
    /// How long an answer stands.
    ///
    /// Long enough to cover pressing the button again, opening another pack and coming
    /// back, or a launcher restart mid-session. Short enough that an author publishing a
    /// release and a user looking for it are not far apart — and <see cref="Clear()"/>
    /// exists for when they are.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly string? _root;

    /// <param name="root">
    /// Somewhere other than Cairn's own cache, or null for that one — which is then read on
    /// every access rather than settled here, because the root can move while Cairn is
    /// running. See <see cref="CairnPaths"/>.
    /// </param>
    public ModUpdateCache(string? root = null) => _root = root;

    public string Root => _root ?? Path.Combine(CairnPaths.CacheRoot, "update-checks");

    private string PathFor(string packId) => Path.Combine(Root, packId + ".json");

    /// <summary>
    /// What the pack looked like when the answer was computed.
    ///
    /// Time alone is not enough to know an answer still applies: adding a mod, pinning one,
    /// retargeting the game version or syncing all change what the check would say, and any
    /// of them can happen well inside the lifetime. Keying on the pack's shape means an
    /// edit misses the cache by construction rather than by remembering to invalidate it.
    /// </summary>
    public static string Fingerprint(PackManifest manifest, PackLock? locked)
    {
        var text = new StringBuilder();
        text.Append(manifest.GameVersion).Append('\n');

        foreach (var mod in manifest.Mods.OrderBy(m => m.ModId, StringComparer.OrdinalIgnoreCase))
            text.Append(mod.ModId).Append('@').Append(mod.Version ?? "*").Append('\n');

        text.Append("--\n");

        foreach (var mod in (locked?.Mods ?? []).OrderBy(m => m.ModId, StringComparer.OrdinalIgnoreCase))
            text.Append(mod.ModId).Append('@').Append(mod.Version).Append('\n');

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }

    /// <summary>The remembered answer, or null when there is none that still applies.</summary>
    public List<ModUpdate>? Get(string packId, string fingerprint, DateTimeOffset now)
    {
        try
        {
            var path = PathFor(packId);
            if (!File.Exists(path)) return null;

            var cached = JsonSerializer.Deserialize<CachedUpdates>(File.ReadAllText(path));
            if (cached is null) return null;

            if (!string.Equals(cached.Fingerprint, fingerprint, StringComparison.Ordinal)) return null;
            if (now - cached.CheckedAt > Lifetime) return null;

            // A clock that has gone backwards — a correction, a suspended laptop — would
            // otherwise make an answer look freshly made for as long as the skew lasts.
            if (cached.CheckedAt > now) return null;

            return cached.Updates;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable cache means the same as no cache, and it is not worth failing
            // a check over.
            return null;
        }
    }

    public void Save(string packId, string fingerprint, List<ModUpdate> updates, DateTimeOffset now)
    {
        try
        {
            Directory.CreateDirectory(Root);

            File.WriteAllText(PathFor(packId), JsonSerializer.Serialize(new CachedUpdates
            {
                CheckedAt = now,
                Fingerprint = fingerprint,
                Updates = updates,
            }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not being able to remember an answer is not a reason to discard it.
        }
    }

    /// <summary>Forgets one pack's answer, so the next check asks ModDB again.</summary>
    public void Clear(string packId)
    {
        try
        {
            var path = PathFor(packId);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Forgets everything. Returns the bytes freed.</summary>
    public long Clear()
    {
        var freed = Size();

        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        return freed;
    }

    /// <summary>What the remembered answers occupy, for the storage view.</summary>
    public long Size()
    {
        try
        {
            if (!Directory.Exists(Root)) return 0;

            return Directory.EnumerateFiles(Root, "*.json", SearchOption.AllDirectories)
                .Sum(f => { try { return new FileInfo(f).Length; } catch (IOException) { return 0; } });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>How many packs have a remembered answer, expired or not.</summary>
    public int Count()
    {
        try
        {
            return Directory.Exists(Root)
                ? Directory.EnumerateFiles(Root, "*.json").Count()
                : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
