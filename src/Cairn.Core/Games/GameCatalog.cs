using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cairn.Core.Runtime;

namespace Cairn.Core.Games;

/// <summary>One downloadable artifact, as published in the version manifest.</summary>
public sealed class CatalogArtifact
{
    [JsonPropertyName("filename")] public string FileName { get; set; } = "";

    /// <summary>Human string such as "613.5 MB" — display only, not a byte count.</summary>
    [JsonPropertyName("filesize")] public string FileSize { get; set; } = "";

    [JsonPropertyName("md5")] public string Md5 { get; set; } = "";
    [JsonPropertyName("urls")] public Dictionary<string, string> Urls { get; set; } = [];

    /// <summary>
    /// Where to fetch this artifact, or null when the catalogue does not name a place
    /// Cairn is willing to fetch from.
    ///
    /// Filtered rather than merely picked. The catalogue supplies both the URL and the MD5
    /// to check it against, so on its own it authenticates nothing: whoever rewrites one
    /// rewrites the other. <see cref="GameCatalog.IsKnownDownloadHost"/> is the part that
    /// does not come out of the document, and running it here rather than at the call site
    /// means a caller cannot hold a URL that never passed it. A poisoned "cdn" entry falls
    /// through to "local" rather than taking the artifact down with it.
    /// </summary>
    public string? DownloadUrl => Allowed("cdn") ?? Allowed("local");

    private string? Allowed(string key) =>
        Urls.TryGetValue(key, out var url) && GameCatalog.IsKnownDownloadHost(url) ? url : null;
}

/// <summary>A game version paired with the artifact for this machine's platform.</summary>
public sealed record GameRelease(string Version, string Platform, CatalogArtifact Artifact)
{
    /// <summary>
    /// Something Cairn can unpack into a versioned directory: a tarball everywhere for the
    /// client, and on Windows a zip for the server. Only the Windows *client* is not one —
    /// see <see cref="IsWindowsInstaller"/>.
    /// </summary>
    public bool IsArchive => ArchiveExtractor.IsSupported(Artifact.FileName);

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
    /// Hosts the vendor serves game downloads from.
    ///
    /// Both observed on every one of the 300 artifacts the stable catalogue publishes:
    /// "cdn" is always cdn.vintagestory.at and "local" is always account.vintagestory.at.
    /// Kept as a constant here, in Cairn's own source, precisely because that is the one
    /// part of the decision the catalogue cannot rewrite — the URL and the MD5 beside it
    /// both come out of the same document, so neither constrains the other.
    ///
    /// The same shape as <see cref="ModDb.ModDbUrls"/>' list, and it can go stale the same
    /// way. It failing is a version that cannot be installed with a reason worth printing,
    /// which is the right direction for a list whose job is to bound where a binary Cairn
    /// executes may come from.
    /// </summary>
    private static readonly string[] DownloadHosts =
    [
        "cdn.vintagestory.at",
        "account.vintagestory.at",
    ];

    /// <summary>Whether a catalogue URL points somewhere the vendor actually serves from.</summary>
    public static bool IsKnownDownloadHost(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && DownloadHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Manifest keys for this machine, best first.
    ///
    /// A list because macOS has two. The published clients were x64 on every platform until
    /// 1.22, which is why one key was the whole truth when this was written; 1.22 added a
    /// native <c>mac-arm64</c> build, and on Apple Silicon that is the one to install. An
    /// x64 client there runs under Rosetta and has to be hosted by an x64 .NET, which is a
    /// second runtime to find on a machine whose own is arm64.
    ///
    /// The x64 key is kept as a fallback rather than replaced. It is still the only mac
    /// artifact any version before 1.22 publishes, and releases are filtered by these keys
    /// — so preferring arm64 without falling back would not merely install the wrong
    /// client, it would drop every pre-1.22 version out of the list of versions that can be
    /// installed at all.
    /// </summary>
    public static IReadOnlyList<string> PlatformKeys =>
        KeysFor(Host.This, ExecutableImage.NativeArchitecture);

    /// <param name="os">Taken rather than asked, so all three are checkable from one host.</param>
    /// <param name="arch">Only consulted on macOS, the one platform publishing two.</param>
    public static IReadOnlyList<string> KeysFor(HostOs os, ExecutableArch arch) => os switch
    {
        HostOs.MacOs => arch == ExecutableArch.Arm64 ? ["mac-arm64", "mac-x64"] : ["mac-x64"],
        HostOs.Windows => ["windows"],
        _ => ["linux"],
    };

    /// <summary>This machine's platform, for a message about a version that has none.</summary>
    public static string PlatformDescription => string.Join(" or ", PlatformKeys);

    /// <summary>
    /// Manifest keys for a dedicated server on this machine, best first.
    ///
    /// A server is published for Linux and Windows and nowhere else — mac-arm64 is the only
    /// arm64 artifact in the whole manifest, and there has never been a mac server at all.
    /// macOS gets the client keys back, because a client install ships VintagestoryServer
    /// beside its own binary and is the only way to run a server there.
    ///
    /// The generic "server" key is the fallback rather than a curiosity: it is what every
    /// version before 1.18.15 published instead of the two platform ones, and a tool that
    /// filtered on "linuxserver" alone would report those versions as having no download.
    /// </summary>
    public static IReadOnlyList<string> ServerPlatformKeys =>
        ServerKeysFor(Host.This, ExecutableImage.NativeArchitecture);

    /// <param name="os">Taken rather than asked; see <see cref="KeysFor"/>.</param>
    /// <param name="arch">Only reaches macOS, which falls back to the client keys.</param>
    public static IReadOnlyList<string> ServerKeysFor(HostOs os, ExecutableArch arch) => os switch
    {
        HostOs.Windows => ["windowsserver", "server"],
        HostOs.MacOs => KeysFor(os, arch),
        _ => ["linuxserver", "server"],
    };

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
        bool includePreReleases = false,
        IReadOnlyList<string>? platformKeys = null,
        CancellationToken ct = default)
    {
        var url = includePreReleases ? StableAndUnstableUrl : StableUrl;
        var platform = platformKeys ?? PlatformKeys;

        var raw = await http
            .GetFromJsonAsync<Dictionary<string, Dictionary<string, JsonElement>>>(url, Json, ct)
            .ConfigureAwait(false);

        return Parse(raw, platform);
    }

    /// <summary>Split out so it can be tested against a captured manifest without network.</summary>
    public static List<GameRelease> Parse(
        Dictionary<string, Dictionary<string, JsonElement>>? raw, string platform)
        => Parse(raw, [platform]);

    /// <summary>
    /// The releases published for any of <paramref name="platforms"/>, one per version,
    /// taking the first key that yields a usable artifact.
    ///
    /// Per version rather than per manifest, because the keys a version publishes changed
    /// mid-life: 1.22 added mac-arm64 and nothing before it has one. Choosing a key once
    /// for the whole catalogue would either hide every older version or install an emulated
    /// client for every newer one.
    ///
    /// "Yields a usable artifact" rather than "is present" so a malformed or unreachable
    /// preferred entry falls through to the next key instead of losing the version.
    /// </summary>
    public static List<GameRelease> Parse(
        Dictionary<string, Dictionary<string, JsonElement>>? raw, IReadOnlyList<string> platforms)
    {
        var releases = new List<GameRelease>();
        if (raw is null) return releases;

        foreach (var (version, published) in raw)
        {
            foreach (var platform in platforms)
            {
                if (!published.TryGetValue(platform, out var element)) continue;
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

                if (artifact is null) continue;

                // The filename decides where the download lands — GameInstaller combines
                // it with the store root — and on Windows that same path is what gets
                // handed to Process.Start. A name of "..\..\..\evil.exe" would therefore
                // both escape the store and choose what runs. Checked at the one place a
                // GameRelease is constructed, so no caller can hold one whose filename
                // never passed: the same reason DownloadUrl filters inside the property
                // rather than at the call site.
                if (!BareFileName.IsBare(artifact.FileName)) continue;

                if (artifact.DownloadUrl is null) continue;

                releases.Add(new GameRelease(version, platform, artifact));
                break;
            }
        }

        // Newest first. Uses the shared comparer rather than a bespoke one: comparing by
        // "is newer" alone is not a total order and can order inconsistently.
        releases.Sort((a, b) => GameVersionComparer.Ascending.Compare(b.Version, a.Version));
        return releases;
    }
}
