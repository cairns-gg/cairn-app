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
/// Asks whether there is a newer Cairn, occasionally, and never more than once per
/// version.
///
/// Everything needed already existed and nothing read it: the release workflow publishes
/// <c>releases/latest.json</c> and promotes it only when the macOS builds were notarised,
/// and cairns.gg serves its download page from the same file. What was missing was the
/// installed copy knowing — so somebody who downloaded once had no way to hear about a
/// release, which matters more here than in most apps because the ModDB listing and any
/// link a friend sent are equally frozen.
///
/// Two separate restraints, doing different jobs. The daily interval keeps the network
/// alone; remembering the version already mentioned keeps the *person* alone, because a
/// popup that returns every day for a release they have decided to skip is a popup people
/// learn to dismiss without reading.
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
    private readonly string _statePath = statePath ?? CairnPaths.UpdateStatePath;
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
    /// Whether a check is due. Cheap and offline, so a caller can skip spinning anything
    /// up at all.
    /// </summary>
    public bool IsDue()
    {
        // An unstamped build has no version to compare against, and telling a developer
        // their working copy is out of date is noise. See CairnVersion.
        if (_current == "dev") return false;

        var state = UpdateState.Load(_statePath);
        return state.LastChecked is not { } last || _now() - last >= Interval;
    }

    /// <summary>
    /// Returns something only when there is a newer version this machine has not already
    /// been told about. Never throws: an update check is the least important thing the app
    /// does, and it runs while somebody is trying to play a game.
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
            // Not reachable, or not what we expected. Try again tomorrow rather than
            // recording a check that told us nothing.
            return null;
        }

        if (latest is null || string.IsNullOrWhiteSpace(latest.Version)) return null;

        var state = UpdateState.Load(_statePath);

        // Recorded before the decision, not after: the point of the interval is to stop
        // asking the server, and it did get asked.
        state.LastChecked = _now();
        state.Save(_statePath);

        if (!GameVersions.IsNewerVersionThan(latest.Version, _current)) return null;

        // Said once. Somebody who closed this dialog has been told, and telling them again
        // tomorrow teaches them to close it without looking.
        if (state.LastNotified == latest.Version) return null;

        state.LastNotified = latest.Version;
        state.Save(_statePath);

        var file = latest.Files?.FirstOrDefault(
            f => string.Equals(f.Platform, ThisPlatform, StringComparison.OrdinalIgnoreCase));

        return new UpdateAvailable(latest.Version, file);
    }
}

/// <summary>What the last check found, so the next one can stay quiet.</summary>
public sealed class UpdateState
{
    [JsonPropertyName("lastChecked")] public DateTimeOffset? LastChecked { get; set; }

    /// <summary>The version a popup has already named. Null until one has.</summary>
    [JsonPropertyName("lastNotified")] public string? LastNotified { get; set; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Never throws: unreadable bookkeeping costs one extra check.</summary>
    public static UpdateState Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(path), Json) ?? new UpdateState()
                : new UpdateState();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new UpdateState();
        }
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Staged and moved, as the caches and settings are: a half-written file reads
            // as corrupt, and this one is written from a background thread at startup.
            var staging = path + "." + Path.GetRandomFileName();
            File.WriteAllText(staging, JsonSerializer.Serialize(this, Json));
            File.Move(staging, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing this costs one extra check tomorrow.
        }
    }
}
