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
public sealed record UpdateAvailable(string Version, ReleaseFile? File, string? Origin = null)
{
    /// <summary>
    /// Where the download button goes, or the site when the manifest offers nothing this
    /// machine should follow.
    ///
    /// Bounded to the origin the manifest itself was fetched from. The manifest names both
    /// the URL and the sha256 to check it against, and nothing reads that hash — so the
    /// document decides, on its own word, what a button labelled "Download for macOS
    /// (Apple silicon)" fetches. Whoever can rewrite it could point that anywhere; this at
    /// least stops them pointing it off the host they already had to compromise to rewrite
    /// it, which is what would let a build be served to some people and not others, or
    /// from somewhere that keeps no logs.
    ///
    /// Derived from the manifest rather than pinned to a constant, so it holds for a
    /// mirror or a test server too and cannot go stale the way a hardcoded host would.
    /// Falling back to the site rather than refusing: a release that exists is still worth
    /// telling somebody about, and cairns.gg is where they would have gone anyway.
    /// </summary>
    public string DownloadUrl => IsFromOrigin(File?.Url) ? File!.Url : "https://cairns.gg";

    private bool IsFromOrigin(string? url) =>
        Origin is { Length: > 0 }
        && Uri.TryCreate(url, UriKind.Absolute, out var target)
        && Uri.TryCreate(Origin, UriKind.Absolute, out var manifest)
        && target.Scheme == Uri.UriSchemeHttps
        && string.Equals(target.Host, manifest.Host, StringComparison.OrdinalIgnoreCase)
        && target.Port == manifest.Port;

    /// <summary>Names the platform, so the button says what pressing it fetches.</summary>
    public string ButtonLabel => File is null ? "Open cairns.gg" : $"Download for {File.Label}";
}

/// <summary>
/// The state behind the update check: when the server was last asked, and which release
/// this machine was last told about.
/// </summary>
/// <param name="NotifiedVersion">
/// Null when nothing has been offered yet, or when the file could not be read — both of
/// which mean the next newer release is worth mentioning.
/// </param>
public sealed record UpdateState(
    DateTimeOffset? LastChecked,
    string? NotifiedVersion,
    DateTimeOffset? NotifiedAt);

/// <summary>
/// Asks whether there is a newer Cairn, at most every two hours, and mentions any one
/// release at most every eight.
///
/// Everything needed already existed and nothing read it: the release workflow publishes
/// <c>releases/latest.json</c> and promotes it only when the macOS builds were notarised,
/// and cairns.gg serves its download page from the same file. What was missing was the
/// installed copy knowing — so somebody who downloaded once had no way to hear about a
/// release, which matters more here than in most apps because the ModDB listing and any
/// link a friend sent are equally frozen.
///
/// The state was one timestamp and is now three fields, which is worth explaining because
/// reducing it to the one was itself deliberate: a remembered version string was rejected
/// on the grounds that one going stale or unparseable is a popup that either never appears
/// again or appears forever. What makes it safe now is that it is never read alone. The
/// version only ever suppresses in company with <see cref="NotifyInterval"/>, so the worst
/// a wrong or stale version can do is delay one dialog by eight hours — and an unreadable
/// file reads as nothing notified, which shows the offer rather than swallowing it. Both
/// failure directions are bounded, which is what the single timestamp was protecting.
///
/// The two intervals answer different questions. <see cref="CheckInterval"/> is how often
/// the server is asked, and exists for the server's sake. <see cref="NotifyInterval"/> is
/// how often a person is interrupted about the same release, and exists for theirs — so
/// declining 0.3.1 buys eight hours of quiet about 0.3.1 specifically, while 0.3.2
/// appearing half an hour later is still raised at the very next check.
/// </summary>
public sealed class UpdateChecker(
    HttpClient http,
    string? manifestUrl = null,
    string? statePath = null,
    Func<DateTimeOffset>? clock = null,
    string? currentVersion = null)
{
    public const string DefaultManifest = "https://download.cairns.gg/releases/latest.json";

    /// <summary>
    /// How often the server is asked. Short enough that a release is noticed the same
    /// afternoon, and still twelve requests a day from a launcher left open all week.
    /// </summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(2);

    /// <summary>
    /// How often the same release is raised with the same person. Long enough that
    /// declining one is respected for the rest of a sitting, short enough that an update
    /// somebody meant to take later is not forgotten about entirely.
    ///
    /// Per version, not per check: a release nobody has been shown is never held back by
    /// this, however recently they were told about a different one.
    /// </summary>
    public static readonly TimeSpan NotifyInterval = TimeSpan.FromHours(8);

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

    /// <summary>The on-disk shape. Epoch seconds, so the file stays readable by eye.</summary>
    private sealed record StoredState(
        [property: JsonPropertyName("lastChecked")] long LastChecked,
        [property: JsonPropertyName("notifiedVersion")] string? NotifiedVersion,
        [property: JsonPropertyName("notifiedAt")] long NotifiedAt);

    /// <summary>
    /// What this machine remembers, or an empty state if it remembers nothing.
    ///
    /// Anything unreadable — a truncated write, a file somebody edited, an empty one —
    /// reads as never checked and nothing notified. That direction is chosen deliberately:
    /// it costs one extra request and at most one extra dialog, where the other direction
    /// silently swallows the offer.
    /// </summary>
    public static UpdateState Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return Empty;

            var text = File.ReadAllText(path).Trim();
            if (text.Length == 0) return Empty;

            // The bare Unix timestamp this file used to be. Read rather than discarded so
            // upgrading does not spend a request re-learning what it already knew — and
            // an old file legitimately says nothing has been notified.
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bare))
                return new UpdateState(FromEpoch(bare), null, null);

            var stored = JsonSerializer.Deserialize<StoredState>(text);
            if (stored is null) return Empty;

            return new UpdateState(
                FromEpoch(stored.LastChecked),
                string.IsNullOrWhiteSpace(stored.NotifiedVersion) ? null : stored.NotifiedVersion,
                FromEpoch(stored.NotifiedAt));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or JsonException or ArgumentOutOfRangeException)
        {
            return Empty;
        }

        static DateTimeOffset? FromEpoch(long epoch)
        {
            if (epoch <= 0) return null;

            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(epoch);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;    // a number too large to be a date is not a date
            }
        }
    }

    private static UpdateState Empty => new(null, null, null);

    /// <summary>When the server was last asked, or null if it never has been.</summary>
    public static DateTimeOffset? LastChecked(string path) => Load(path).LastChecked;

    private static void Save(string path, UpdateState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var stored = new StoredState(
                state.LastChecked?.ToUnixTimeSeconds() ?? 0,
                state.NotifiedVersion,
                state.NotifiedAt?.ToUnixTimeSeconds() ?? 0);

            // Staged and moved, as the caches and settings are: this is written from a
            // background timer, and a half-written file reads as garbage — which is
            // survivable here, and still worth not doing.
            var staging = path + "." + Path.GetRandomFileName();
            File.WriteAllText(staging, JsonSerializer.Serialize(stored));
            File.Move(staging, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing this costs one extra request, and one extra offer of the same release.
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

        return LastChecked(_statePath) is not { } last || _now() - last >= CheckInterval;
    }

    /// <summary>
    /// Whether this release is worth putting in front of somebody now.
    ///
    /// A version they have not been shown always is, however recently they were told about
    /// a different one — which is the whole point: declining 0.3.1 must not hide 0.3.2
    /// released half an hour later. Only being told the same thing again is rationed.
    /// </summary>
    private static bool ShouldNotify(UpdateState state, string version, DateTimeOffset now)
    {
        if (!string.Equals(state.NotifiedVersion, version, StringComparison.OrdinalIgnoreCase))
            return true;

        return state.NotifiedAt is not { } last || now - last >= NotifyInterval;
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

        var now = _now();
        var state = Load(_statePath);

        var notify = GameVersions.IsNewerVersionThan(latest.Version, _current)
                     && ShouldNotify(state, latest.Version, now);

        // One write, whatever was decided. The check is recorded either way — the interval
        // exists to stop asking the server, and it did get asked — and the notification is
        // recorded here rather than after the dialog closes, so a timer firing on top of an
        // open prompt finds nothing left to say.
        Save(_statePath, new UpdateState(
            LastChecked: now,
            NotifiedVersion: notify ? latest.Version : state.NotifiedVersion,
            NotifiedAt: notify ? now : state.NotifiedAt));

        if (!notify) return null;

        var file = latest.Files?.FirstOrDefault(
            f => string.Equals(f.Platform, ThisPlatform, StringComparison.OrdinalIgnoreCase));

        // The manifest's own address travels with the offer, so the button cannot be sent
        // somewhere the manifest was not. See UpdateAvailable.DownloadUrl.
        return new UpdateAvailable(latest.Version, file, _manifest);
    }
}
