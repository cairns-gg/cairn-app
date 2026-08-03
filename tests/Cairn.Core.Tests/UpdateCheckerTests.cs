using System.Net;
using System.Text;
using Cairn.Core;
using Cairn.Core.Updates;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Noticing that a newer Cairn exists, and — mostly — saying nothing.
///
/// The interesting cases are all restraint: an unstamped build, a version already
/// mentioned, a check made an hour ago, a manifest that will not parse. Getting any of
/// those wrong produces a popup somebody sees every single launch.
/// </summary>
public class UpdateCheckerTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "cairn-update-" + Guid.NewGuid().ToString("n")[..8]);

    private string StatePath => Path.Combine(_dir, "last-update-check");

    public UpdateCheckerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class Stub(Func<HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(reply());
        }
    }

    private static string Manifest(string version) => $$"""
        {
          "version": "{{version}}",
          "publishedAt": "2026-08-02T21:52:09Z",
          "files": [
            {"platform": "macos-arm64", "name": "cairn-macos-arm64.zip",
             "url": "https://download.cairns.gg/releases/{{version}}/cairn-macos-arm64.zip",
             "size": 48795601, "sha256": "aa"},
            {"platform": "windows-x64", "name": "cairn-windows-x64.zip",
             "url": "https://download.cairns.gg/releases/{{version}}/cairn-windows-x64.zip",
             "size": 44624948, "sha256": "bb"},
            {"platform": "linux-x64", "name": "cairn-linux-x64.tar.gz",
             "url": "https://download.cairns.gg/releases/{{version}}/cairn-linux-x64.tar.gz",
             "size": 42479765, "sha256": "cc"},
            {"platform": "macos-x64", "name": "cairn-macos-x64.zip",
             "url": "https://download.cairns.gg/releases/{{version}}/cairn-macos-x64.zip",
             "size": 51081143, "sha256": "dd"}
          ]
        }
        """;

    /// <param name="running">
    /// What the build calls itself. Supplied rather than read from the assembly because a
    /// test host is unstamped and reports "dev", which suppresses the whole feature.
    /// </param>
    private (UpdateChecker Checker, Stub Handler) Make(
        string body, HttpStatusCode code = HttpStatusCode.OK, DateTimeOffset? now = null,
        string running = "0.2.1")
    {
        var handler = new Stub(() => new HttpResponseMessage(code)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

        var clock = now ?? DateTimeOffset.UtcNow;
        return (new UpdateChecker(new HttpClient(handler), "https://cairns.test/latest.json",
                                  StatePath, () => clock, running), handler);
    }

    [Fact]
    public async Task A_newer_version_is_offered_with_the_build_for_this_machine()
    {
        var (checker, handler) = Make(Manifest("0.3.0"));

        var update = await checker.CheckAsync();

        Assert.NotNull(update);
        Assert.Equal("0.3.0", update!.Version);
        Assert.Equal(1, handler.Calls);

        // The offer is for the machine it is running on, not the first entry in the list.
        Assert.Equal(UpdateChecker.ThisPlatform, update.File!.Platform);
        Assert.Contains("0.3.0", update.DownloadUrl);
        Assert.StartsWith("Download for ", update.ButtonLabel);
    }

    [Fact]
    public async Task The_same_version_is_not_an_update()
    {
        var (checker, _) = Make(Manifest("0.2.1"));
        Assert.Null(await checker.CheckAsync());

        // The check still happened, so tomorrow is when it happens again.
        Assert.NotNull(UpdateChecker.LastChecked(StatePath));
    }

    [Fact]
    public async Task An_older_published_version_is_not_an_update()
    {
        // A rollback, or a manifest served from a stale cache. Neither is news.
        var (checker, _) = Make(Manifest("0.1.9"));
        Assert.Null(await checker.CheckAsync());
    }

    [Fact]
    public async Task A_declined_release_is_raised_again_the_next_day()
    {
        var now = DateTimeOffset.UtcNow;
        var (first, _) = Make(Manifest("0.3.0"), now: now);
        Assert.NotNull(await first.CheckAsync());

        // Nothing records which release was mentioned, so tomorrow it is mentioned again.
        // The trade for keeping the whole of the state to one timestamp: somebody who does
        // not want 0.3.0 is asked daily until they take it or it is superseded.
        var (tomorrow, _) = Make(Manifest("0.3.0"), now: now.AddDays(1).AddMinutes(1));
        Assert.NotNull(await tomorrow.CheckAsync());

        // But not twice in the same day.
        var (again, handler) = Make(Manifest("0.3.0"), now: now.AddDays(1).AddMinutes(2));
        Assert.Null(await again.CheckAsync());
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void An_unstamped_build_never_asks()
    {
        var (checker, _) = Make(Manifest("9.9.9"), running: "dev");

        // No network, no state file, no popup — decided before anything is spun up. This
        // is the guard that keeps a developer from being told their working copy is old.
        Assert.Equal("dev", CairnVersion.Current);
        Assert.False(checker.IsDue());
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public void The_platform_offered_is_one_the_manifest_actually_names()
    {
        // Whatever this test runs on, the key has to match a platform the release workflow
        // writes — the two are edited in different repositories and would drift silently.
        Assert.Contains(UpdateChecker.ThisPlatform,
            new[] { "windows-x64", "linux-x64", "macos-arm64", "macos-x64" });

        Assert.Contains($"\"platform\": \"{UpdateChecker.ThisPlatform}\"", Manifest("1.0.0"));
    }

    [Fact]
    public void A_missing_build_for_this_platform_still_offers_the_site()
    {
        // Better than silence: the release exists, and the download page has whatever was
        // built for it.
        var update = new UpdateAvailable("0.3.0", File: null);

        Assert.Equal("https://cairns.gg", update.DownloadUrl);
        Assert.Equal("Open cairns.gg", update.ButtonLabel);
    }

    [Fact]
    public void The_button_names_the_platform_it_would_fetch()
    {
        var file = new ReleaseFile("macos-arm64", "cairn.zip", "https://example.test/c.zip",
            48_795_601, "aa");

        Assert.Equal("Download for macOS (Apple silicon)", new UpdateAvailable("0.3.0", file).ButtonLabel);
        Assert.Equal("47 MB", file.SizeText);
    }

    [Fact]
    public void A_check_made_recently_is_not_repeated()
    {
        var now = DateTimeOffset.UtcNow;
        File.WriteAllText(StatePath, now.AddHours(-1).ToUnixTimeSeconds().ToString());

        var (checker, _) = Make(Manifest("9.9.9"), now: now);
        Assert.False(checker.IsDue());

        // And a day later it is.
        var (tomorrow, _) = Make(Manifest("9.9.9"), now: now.AddDays(1).AddMinutes(1));
        Assert.True(tomorrow.IsDue());
    }

    [Fact]
    public async Task The_file_is_one_epoch_timestamp_and_nothing_else()
    {
        var now = DateTimeOffset.UtcNow;
        var (checker, _) = Make(Manifest("0.3.0"), now: now);
        await checker.CheckAsync();

        // Readable with cat, and by anything that can parse an integer.
        Assert.Equal(now.ToUnixTimeSeconds().ToString(), File.ReadAllText(StatePath));
        Assert.Equal(now.ToUnixTimeSeconds(), UpdateChecker.LastChecked(StatePath)!.Value.ToUnixTimeSeconds());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a number")]
    [InlineData("{\"lastChecked\": 123}")]
    [InlineData("99999999999999999999")]
    public void An_unreadable_timestamp_reads_as_never_checked(string content)
    {
        // Including the JSON this used to be, so an older install is not read as having
        // checked at the epoch. Every one of these costs a single extra request.
        File.WriteAllText(StatePath, content);

        Assert.Null(UpdateChecker.LastChecked(StatePath));
    }

    [Fact]
    public async Task An_unreachable_server_records_nothing_and_asks_again_tomorrow()
    {
        var (checker, _) = Make("", HttpStatusCode.ServiceUnavailable);

        // Not a crash, and — because nothing was learned — not a check worth remembering.
        Assert.Null(await checker.CheckAsync());
        Assert.Null(UpdateChecker.LastChecked(StatePath));
    }

    [Fact]
    public async Task A_manifest_that_will_not_parse_is_survived()
    {
        var (checker, _) = Make("<html>not json at all</html>");

        Assert.Null(await checker.CheckAsync());
    }
}
