using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cairn.Core.Updates;

/// <summary>One build in the published manifest.</summary>
public sealed record ReleaseFile(
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256)
{
    public string SizeText => $"{Size / 1024d / 1024d:F0} MB";

    /// <summary>"macos-arm64" → "macOS (Apple silicon)". Matches what the site says.</summary>
    public string Label => Platform switch
    {
        "macos-arm64" => "macOS (Apple silicon)",
        "macos-x64" => "macOS (Intel)",
        "windows-x64" => "Windows",
        "linux-x64" => "Linux",
        _ => Platform,
    };
}

/// <summary>What the release workflow last published.</summary>
public sealed record LatestRelease(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("publishedAt")] string? PublishedAt,
    [property: JsonPropertyName("files")] IReadOnlyList<ReleaseFile>? Files);

/// <summary>
/// A newer version than the one running, and the build to offer for this machine.
/// </summary>
/// <param name="File">
/// Null when the manifest carries nothing for this platform — which is worth offering
/// anyway, pointed at the site, rather than staying silent about a release that exists.
/// </param>
public sealed record UpdateAvailable(string Version, ReleaseFile? File)
{
    public string DownloadUrl => File?.Url ?? "https://cairns.gg";

    /// <summary>Names the platform, so the button says what pressing it fetches.</summary>
    public string ButtonLabel => File is null ? "Open cairns.gg" : $"Download for {File.Label}";
}

/// <summary>
/// Asks whether there is a newer Cairn, at most once a day.
///
/// Everything needed already existed and nothing read it: the release workflow publishes
/// <c>releases/latest.json</c> and promotes it only when the macOS builds were notarised,
/// and cairns.gg serves its download page from the same file. What was missing was the
/// installed copy knowing — so somebody who downloaded once had no way to hear about a
/// release, which matters more here than in most apps because the ModDB listing and any
/// link a friend sent are equally frozen.
///
/// The whole of the remembered state is one Unix timestamp in one file. There is no record
/// of which release has already been mentioned, so an update that somebody declines is
/// raised again the next day, and every day until they take it or it is superseded. That
/// is a deliberate trade for having nothing to keep in step: the alternative remembers a
/// version string as well, and a version string that goes stale or unparseable is a popup
/// that either never appears again or appears forever.
/// </summary>
public sealed class UpdateChecker(
    HttpClient http,
    string? manifestUrl = null,
    string? statePath = null,
    Func<DateTimeOffset>? clock = null,
    string? currentVersion = null)
{
    public const string DefaultManifest = "https://download.cairns.gg/releases/latest.json";

    /// <summary>Long enough that it is not a background task anyone would notice.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private readonly string _manifest = manifestUrl ?? DefaultManifest;
    private readonly string _statePath = statePath ?? CairnPaths.LastUpdateCheckPath;
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>
    /// What this build calls itself. A parameter rather than a direct read of
    /// <see cref="CairnVersion"/> so the offer can be tested at all: under a test runner
    /// the assembly is unstamped and reports "dev", which is exactly the case that
    /// suppresses everything below.
    /// </summary>
    private readonly string _current = currentVersion ?? CairnVersion.Current;

    /// <summary>
    /// The platform key this build would be replaced by, matching the names the release
    /// workflow writes into the manifest.
    ///
    /// Deliberately the architecture this process is running as rather than the machine's:
    /// an x64 build under Rosetta is offered the x64 build again. Silently moving somebody
    /// to a different architecture is a bigger change than an update, and one they should
    /// make on the download page where it is spelled out.
    /// </summary>
    public static string ThisPlatform =>
        (OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), RuntimeInformation.ProcessArchitecture) switch
        {
            (true, _, _) => "windows-x64",
            (_, true, Architecture.Arm64) => "macos-arm64",
            (_, true, _) => "macos-x64",
            _ => "linux-x64",
        };

    /// <summary>
    /// When the server was last asked, or null if it never has been.
    ///
    /// A bare Unix timestamp in seconds. Anything unreadable — a truncated write, a file
    /// somebody edited, an empty one — reads as never, which costs one extra request and
    /// is repaired by the next successful check.
    /// </summary>
    public static DateTimeOffset? LastChecked(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var text = File.ReadAllText(path).Trim();
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch)
                ? DateTimeOffset.FromUnixTimeSeconds(epoch)
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private void RecordCheck(string path, DateTimeOffset when)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Staged and moved, as the caches and settings are. It is one number, but it is
            // written from a background timer and a half-written one reads as garbage —
            // which is survivable here, and still worth not doing.
            var staging = path + "." + Path.GetRandomFileName();
            File.WriteAllText(staging, when.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            File.Move(staging, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing this costs one extra request.
        }
    }

    /// <summary>
    /// Whether a check is due. Cheap and offline, so a caller can skip spinning anything
    /// up at all — which is what the hourly timer does 23 times out of 24.
    /// </summary>
    public bool IsDue()
    {
        // An unstamped build has no version to compare against, and telling a developer
        // their working copy is out of date is noise. See CairnVersion.
        if (_current == "dev") return false;

        return LastChecked(_statePath) is not { } last || _now() - last >= Interval;
    }

    /// <summary>
    /// Returns something only when there is a newer version than this build. Never throws:
    /// an update check is the least important thing the app does, and it runs while
    /// somebody is trying to play a game.
    /// </summary>
    public async Task<UpdateAvailable?> CheckAsync(CancellationToken ct = default)
    {
        if (!IsDue()) return null;

        LatestRelease? latest;
        try
        {
            latest = await http.GetFromJsonAsync<LatestRelease>(_manifest, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException
                                      or NotSupportedException or InvalidOperationException)
        {
            // Not reachable, or not what we expected. Deliberately not recorded: a check
            // that learned nothing should not buy the server a day of quiet, and the next
            // tick is an hour away rather than a moment.
            return null;
        }

        if (latest is null || string.IsNullOrWhiteSpace(latest.Version)) return null;

        // Recorded whatever the answer turns out to be: the interval exists to stop asking
        // the server, and it did get asked.
        RecordCheck(_statePath, _now());

        if (!GameVersions.IsNewerVersionThan(latest.Version, _current)) return null;

        var file = latest.Files?.FirstOrDefault(
            f => string.Equals(f.Platform, ThisPlatform, StringComparison.OrdinalIgnoreCase));

        return new UpdateAvailable(latest.Version, file);
    }
}
