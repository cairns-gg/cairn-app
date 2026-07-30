using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cairn.Core.Games;

/// <summary>One downloadable artifact, as published in the version manifest.</summary>
public sealed class CatalogArtifact
{
    [JsonPropertyName("filename")] public string FileName { get; set; } = "";

    /// <summary>Human string such as "613.5 MB" — display only, not a byte count.</summary>
    [JsonPropertyName("filesize")] public string FileSize { get; set; } = "";

    [JsonPropertyName("md5")] public string Md5 { get; set; } = "";
    [JsonPropertyName("urls")] public Dictionary<string, string> Urls { get; set; } = [];

    public string? DownloadUrl =>
        Urls.TryGetValue("cdn", out var cdn) && !string.IsNullOrWhiteSpace(cdn)
            ? cdn
            : Urls.TryGetValue("local", out var local) ? local : null;
}

/// <summary>A game version paired with the artifact for this machine's platform.</summary>
public sealed record GameRelease(string Version, string Platform, CatalogArtifact Artifact)
{
    /// <summary>
    /// macOS and Linux publish a client tarball, which is unpacked into a versioned
    /// directory. Windows does not — see <see cref="IsWindowsInstaller"/>.
    /// </summary>
    public bool IsArchive =>
        Artifact.FileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Windows publishes the client only as an installer executable. It is an Inno Setup
    /// installer, so it still takes a target directory and runs headless — the platform
    /// is installable, just not by unpacking.
    /// </summary>
    public bool IsWindowsInstaller =>
        Platform == "windows"
        && Artifact.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether Cairn can install this itself, however it happens to be packaged.</summary>
    public bool CanInstall => IsArchive || IsWindowsInstaller;

    public bool IsPreRelease => Version.Contains('-');
}

/// <summary>
/// Reads the official version manifest at api.vintagestory.at.
///
/// Downloads are unauthenticated: the licence check happens at in-game login, not at
/// download time, so Cairn can fetch the game but the player still needs an account.
/// </summary>
public sealed class GameCatalog(HttpClient http)
{
    public const string StableUrl = "https://api.vintagestory.at/stable.json";
    public const string StableAndUnstableUrl = "https://api.vintagestory.at/stable-unstable.json";
    public const string LatestStableUrl = "https://api.vintagestory.at/lateststable.txt";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Manifest key for this machine. The published clients are x64 on every platform,
    /// so macOS resolves to mac-x64.
    /// </summary>
    public static string PlatformKey
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "mac-x64";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
            return "linux";
        }
    }

    public async Task<string?> GetLatestStableAsync(CancellationToken ct = default)
    {
        var text = await http.GetStringAsync(LatestStableUrl, ct).ConfigureAwait(false);
        text = text.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>
    /// Every version offering an artifact for this platform, newest first.
    /// </summary>
    public async Task<List<GameRelease>> ListReleasesAsync(
        bool includePreReleases = false, string? platformKey = null, CancellationToken ct = default)
    {
        var url = includePreReleases ? StableAndUnstableUrl : StableUrl;
        var platform = platformKey ?? PlatformKey;

        var raw = await http
            .GetFromJsonAsync<Dictionary<string, Dictionary<string, JsonElement>>>(url, Json, ct)
            .ConfigureAwait(false);

        return Parse(raw, platform);
    }

    /// <summary>Split out so it can be tested against a captured manifest without network.</summary>
    public static List<GameRelease> Parse(
        Dictionary<string, Dictionary<string, JsonElement>>? raw, string platform)
    {
        var releases = new List<GameRelease>();
        if (raw is null) return releases;

        foreach (var (version, platforms) in raw)
        {
            if (!platforms.TryGetValue(platform, out var element)) continue;
            if (element.ValueKind != JsonValueKind.Object) continue;

            CatalogArtifact? artifact;
            try
            {
                artifact = element.Deserialize<CatalogArtifact>(Json);
            }
            catch (JsonException)
            {
                // One malformed entry must not lose the whole catalog.
                continue;
            }

            if (artifact is null || string.IsNullOrWhiteSpace(artifact.FileName)) continue;
            if (artifact.DownloadUrl is null) continue;

            releases.Add(new GameRelease(version, platform, artifact));
        }

        // Newest first. Uses the shared comparer rather than a bespoke one: comparing by
        // "is newer" alone is not a total order and can order inconsistently.
        releases.Sort((a, b) => GameVersionComparer.Ascending.Compare(b.Version, a.Version));
        return releases;
    }
}
