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
        // The real manifest address, not a stand-in on another host. The files it names
        // live on this host too, and an offer is only followed when they agree — a fixture
        // that served the manifest from somewhere else was modelling a combination that
        // does not occur and would now, rightly, be refused.
        // Signature checking off for these: they are about when somebody is interrupted
        // and where a button points, and the stub answers every request with the manifest
        // — including the one for the signature. The signed path has its own fixtures
        // below, which is where arming it belongs.
        return (new UpdateChecker(new HttpClient(handler), UpdateChecker.DefaultManifest,
                                  StatePath, () => clock, running, publicKey: ""), handler);
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
    public async Task A_declined_release_is_not_raised_again_for_eight_hours()
    {
        var now = DateTimeOffset.UtcNow;
        var (first, _) = Make(Manifest("0.3.0"), now: now);
        Assert.NotNull(await first.CheckAsync());

        // Walked two hours at a time, because that is how it actually runs: each check
        // moves LastChecked, so the next one is due two hours after the last, not two
        // hours after the offer. Asserting at 7h59m instead would have been asserting
        // about a check that could not have happened.
        for (var t = UpdateChecker.CheckInterval;
             t < UpdateChecker.NotifyInterval;
             t += UpdateChecker.CheckInterval)
        {
            var (soon, handler) = Make(Manifest("0.3.0"), now: now.Add(t));

            // The server is asked — that interval is up — and 0.3.0 is still not repeated.
            Assert.Null(await soon.CheckAsync());
            Assert.Equal(1, handler.Calls);
        }

        // And raised again once eight hours are up: an update somebody meant to take later
        // should not be forgotten about entirely.
        var (later, _) = Make(Manifest("0.3.0"), now: now.Add(UpdateChecker.NotifyInterval));
        Assert.NotNull(await later.CheckAsync());
    }

    [Fact]
    public async Task A_release_newer_than_the_declined_one_is_raised_at_the_very_next_check()
    {
        var now = DateTimeOffset.UtcNow;

        var (first, _) = Make(Manifest("0.3.1"), now: now);
        Assert.Equal("0.3.1", (await first.CheckAsync())!.Version);

        // 0.3.2 ships half an hour later. The eight hours of quiet were bought for 0.3.1,
        // not for silence in general — rationing by version rather than by check is the
        // whole point, and holding this back would be sitting on the release that fixes
        // whatever made them decline the last one.
        var (next, _) = Make(Manifest("0.3.2"), now: now.Add(UpdateChecker.CheckInterval));
        Assert.Equal("0.3.2", (await next.CheckAsync())!.Version);

        // And 0.3.2 now gets the same eight hours of its own.
        var (again, _) = Make(Manifest("0.3.2"),
            now: now.Add(UpdateChecker.CheckInterval).Add(UpdateChecker.CheckInterval));
        Assert.Null(await again.CheckAsync());
    }

    [Fact]
    public async Task The_server_is_not_asked_more_than_once_every_two_hours()
    {
        var now = DateTimeOffset.UtcNow;
        var (first, _) = Make(Manifest("0.3.0"), now: now);
        Assert.NotNull(await first.CheckAsync());

        // A tick an hour later reads the file and stops there — no request at all.
        var (soon, handler) = Make(Manifest("0.3.0"), now: now.AddHours(1));
        Assert.False(soon.IsDue());
        Assert.Null(await soon.CheckAsync());
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

        // And two hours on it is.
        var (later, _) = Make(Manifest("9.9.9"),
            now: now.Add(UpdateChecker.CheckInterval).AddMinutes(1));
        Assert.True(later.IsDue());
    }

    [Fact]
    public async Task The_file_records_the_check_and_what_was_offered()
    {
        var now = DateTimeOffset.UtcNow;
        var (checker, _) = Make(Manifest("0.3.0"), now: now);
        await checker.CheckAsync();

        var state = UpdateChecker.Load(StatePath);

        Assert.Equal(now.ToUnixTimeSeconds(), state.LastChecked!.Value.ToUnixTimeSeconds());
        Assert.Equal("0.3.0", state.NotifiedVersion);
        Assert.Equal(now.ToUnixTimeSeconds(), state.NotifiedAt!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task A_check_that_found_nothing_new_records_no_notification()
    {
        var now = DateTimeOffset.UtcNow;
        var (checker, _) = Make(Manifest("0.2.1"), now: now);
        Assert.Null(await checker.CheckAsync());

        // The check happened and is remembered; nothing was said, so nothing is rationed.
        var state = UpdateChecker.Load(StatePath);
        Assert.NotNull(state.LastChecked);
        Assert.Null(state.NotifiedVersion);
    }

    [Fact]
    public void The_bare_timestamp_this_file_used_to_be_is_still_read()
    {
        // An install upgrading from the one-number format should not spend a request
        // re-learning what it already knew — and it has genuinely notified nothing.
        var when = DateTimeOffset.UtcNow.AddMinutes(-5);
        File.WriteAllText(StatePath, when.ToUnixTimeSeconds().ToString());

        var state = UpdateChecker.Load(StatePath);

        Assert.Equal(when.ToUnixTimeSeconds(), state.LastChecked!.Value.ToUnixTimeSeconds());
        Assert.Null(state.NotifiedVersion);
        Assert.Null(state.NotifiedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a number")]
    [InlineData("{\"lastChecked\": ")]
    [InlineData("99999999999999999999")]
    public void An_unreadable_file_reads_as_never_checked_and_nothing_notified(string content)
    {
        // Both directions matter. Reading as never checked costs one extra request;
        // reading as nothing notified shows an offer that might be a repeat. The opposite
        // defaults would silently swallow the offer instead, which is the failure nobody
        // would ever report.
        File.WriteAllText(StatePath, content);

        var state = UpdateChecker.Load(StatePath);

        Assert.Null(state.LastChecked);
        Assert.Null(state.NotifiedVersion);
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

    /// <summary>
    /// The manifest names both the URL and the sha256 to check it against, and nothing
    /// reads that hash — so the document decides, on its own word alone, what a button
    /// labelled "Download for Windows" fetches. Whoever can rewrite it cannot also send
    /// that button off the host they had to compromise to rewrite it.
    /// </summary>
    [Fact]
    public async Task A_build_hosted_somewhere_else_is_not_what_the_button_fetches()
    {
        var elsewhere = Manifest("0.3.0").Replace(
            "https://download.cairns.gg/releases/", "https://attacker.example/releases/");

        var (checker, _) = Make(elsewhere);
        var update = await checker.CheckAsync();

        Assert.NotNull(update);
        Assert.Equal("0.3.0", update!.Version);

        // Still offered — a release that exists is worth mentioning — but pointed at the
        // site rather than at whatever the manifest asked for.
        Assert.Equal("https://cairns.gg", update.DownloadUrl);
        Assert.DoesNotContain("attacker.example", update.DownloadUrl);
    }

    [Fact]
    public async Task A_build_offered_over_plain_http_is_not_followed_either()
    {
        var downgraded = Manifest("0.3.0").Replace("https://download.cairns.gg", "http://download.cairns.gg");

        var (checker, _) = Make(downgraded);
        var update = await checker.CheckAsync();

        Assert.Equal("https://cairns.gg", update!.DownloadUrl);
    }

    // ---- the signed manifest ----

    private const string SignedKey = "RWRAbJ1gHdDEh9xDOLFum0islHiQrxMrXefIFoeDUB2GgqUNY4bHmPXr";

    /// <summary>Byte for byte what was signed. Reformatting it invalidates the signature.</summary>
    private const string SignedBody =
        """{"version":"0.3.0","files":[{"platform":"linux-x64","name":"c.tar.gz","url":"https://download.cairns.gg/releases/0.3.0/c.tar.gz","size":1,"sha256":"aa"}]}""";

    private const string SignedSig = """
        untrusted comment: signature from minisign secret key
        RURAbJ1gHdDEh+0ZEdWrBFaVFMthcKIXEfvcS1DRxddOtn11ayjxmA0+zUGD8e3rNh6by6nRhTWXvx3JIlkdFdzDjAyeytEqRgw=
        trusted comment: cairn 0.3.0
        lP+lAY50nL781p6251LPY4cUpdJCuEG5Nfj52CWQ7lxWVtp7yUF9BlA9f5QU5YK/NsLX2sIZUJhvP8e15Nl7Cg==
        """;

    /// <summary>Answers the manifest and its signature separately, or 404s the signature.</summary>
    private sealed class Signed(string body, string? signature) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var wantsSignature = r.RequestUri!.ToString().EndsWith(".minisig", StringComparison.Ordinal);

            if (wantsSignature && signature is null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(wantsSignature ? signature! : body, Encoding.UTF8),
            });
        }
    }

    private UpdateChecker Armed(string body, string? signature, string key = SignedKey) =>
        new(new HttpClient(new Signed(body, signature)), UpdateChecker.DefaultManifest,
            StatePath, () => DateTimeOffset.UtcNow, "0.2.1", key);

    [Fact]
    public async Task A_signed_manifest_is_acted_on()
    {
        var update = await Armed(SignedBody, SignedSig).CheckAsync();

        Assert.NotNull(update);
        Assert.Equal("0.3.0", update!.Version);
    }

    /// <summary>
    /// An unsigned manifest is refused as firmly as a wrongly-signed one. Treating absence
    /// as the lesser failure would let anybody who can rewrite the manifest turn the check
    /// off by deleting a file, which is the same attack with less work.
    /// </summary>
    [Fact]
    public async Task An_unsigned_manifest_is_refused_once_a_key_is_configured()
        => Assert.Null(await Armed(SignedBody, signature: null).CheckAsync());

    [Fact]
    public async Task A_manifest_edited_after_signing_is_refused()
    {
        var edited = SignedBody.Replace("\"0.3.0\"", "\"9.9.9\"");

        Assert.Null(await Armed(edited, SignedSig).CheckAsync());
    }

    /// <summary>
    /// The version comes out of the same document as everything else, so a manifest that
    /// does not verify is not a source for it either — announcing a release named by a
    /// forged document is doing the attacker's typing.
    /// </summary>
    [Fact]
    public async Task A_refused_manifest_offers_nothing_at_all()
    {
        var forged = SignedBody.Replace("\"0.3.0\"", "\"9.9.9\"");
        var update = await Armed(forged, SignedSig).CheckAsync();

        Assert.Null(update);
    }

    [Fact]
    public async Task A_manifest_signed_by_another_key_is_refused()
        => Assert.Null(await Armed(SignedBody, SignedSig,
            key: "RWQDiHgg9aatPFKkqUvPYNvMyNAevHIYjOOTWaN65OATfn8zQawEfQCZ").CheckAsync());

    /// <summary>
    /// And with no key compiled in, nothing changes — which is the state this ships in
    /// until a key exists, and the reason SR-011 is not closed by this alone.
    /// </summary>
    [Fact]
    public async Task With_no_key_configured_an_unsigned_manifest_is_still_accepted()
    {
        var checker = new UpdateChecker(
            new HttpClient(new Signed(SignedBody, signature: null)), UpdateChecker.DefaultManifest,
            StatePath, () => DateTimeOffset.UtcNow, "0.2.1", publicKey: "");

        Assert.NotNull(await checker.CheckAsync());
    }

    /// <summary>
    /// The key Cairn actually ships with, checked for being a key at all. A signing scheme
    /// is only as good as the constant it is pinned to, and that constant is pasted by
    /// hand: a truncated or mistyped one would not fail loudly, it would quietly refuse
    /// every release forever, which looks exactly like nobody having published one.
    /// </summary>
    [Fact]
    public void The_shipped_public_key_is_a_usable_minisign_key()
    {
        Assert.NotEqual("", UpdateChecker.ManifestPublicKey);

        var raw = Convert.FromBase64String(UpdateChecker.ManifestPublicKey);

        Assert.Equal(42, raw.Length);                                  // 2 + 8 + 32
        Assert.Equal("Ed", Encoding.ASCII.GetString(raw, 0, 2));       // Ed25519

        // And it is the key it is meant to be, rather than merely a well-formed one.
        Assert.Equal("08F023A1BE926CB4", Convert.ToHexString(raw[2..10].Reverse().ToArray()));
    }
}
