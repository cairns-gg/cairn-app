using System.IO.Compression;
using System.Net;
using System.Text;
using Cairn.Core.ModDb;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// A mod that has not caught up with the game, installed anyway because somebody said they
/// ran it.
///
/// The case is ordinary: a small mod stops being updated, the game moves a minor, and it
/// still works — but ModDB says nothing about the new version, so a resolve refuses it and
/// the pack reports "no release marked for game 1.22.6". True, and no help to somebody who
/// has tested it. These cover the escape hatch and, more importantly, its edges: that it is
/// never taken without being asked for, that it says so every time, and that it stops
/// applying when the pack moves to a game version nobody accepted anything for.
/// </summary>
public class UnmarkedModTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-unmarked-" + Guid.NewGuid().ToString("n")[..8]);

    private string ModsDir => Path.Combine(_root, "Mods");
    private string LockPath => Path.Combine(_root, "pack.lock.json");

    public UnmarkedModTests() => Directory.CreateDirectory(ModsDir);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ---- the rule ----

    [Theory]
    [InlineData("1.22.6", "1.22.6")]   // the version it was accepted for
    [InlineData("1.22.6", "1.22.7")]   // a patch bump: the game treats these as the same
    [InlineData("1.22.0", "1.22.11")]
    public void An_acceptance_covers_the_release_series_it_was_made_in(string accepted, string now)
        => Assert.True(new PackMod { ModId = "m", AcceptedFor = accepted }.AcceptsUnmarkedFor(now));

    [Theory]
    [InlineData("1.22.6", "1.23.0")]   // a minor bump: nobody has tested this
    [InlineData("1.22.6", "2.0.0")]
    [InlineData("1.21.4", "1.22.6")]
    public void And_stops_at_the_edge_of_it(string accepted, string now)
        => Assert.False(new PackMod { ModId = "m", AcceptedFor = accepted }.AcceptsUnmarkedFor(now));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-version")]
    public void A_mod_with_no_usable_acceptance_accepts_nothing(string? accepted)
        => Assert.False(new PackMod { ModId = "m", AcceptedFor = accepted }.AcceptsUnmarkedFor("1.22.6"));

    // ---- the sync ----

    /// <summary>
    /// Serves one mod whose only release is marked for 1.21.4, and its zip — or, with
    /// <paramref name="installable"/> off, that release with its file gone, the way ModDB
    /// keeps the row for a deleted upload.
    /// </summary>
    private sealed class Stub(bool installable = true) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            if (r.RequestUri!.ToString().Contains("/api/mod/"))
            {
                var body = """
                {"statuscode":"200","mod":{
                  "modid":1,"assetid":2,"name":"Ore Vein Tracers","urlalias":"oreveintracers",
                  "side":"both",
                  "releases":[{"releaseid":1,"fileid":1,"modidstr":"oreveintracers",
                    "modversion":"1.2.3","filename":"oreveintracers_1.2.3.zip",
                    "mainfile":"MAINFILE",
                    "tags":["1.21.4"]}]}}
                """.Replace("MAINFILE", installable
                    ? "https://moddbcdn.vintagestory.at/oreveintracers_1.2.3.zip"
                    : "");

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Zip()),
            });
        }

        private static byte[] Zip()
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(zip.CreateEntry("modinfo.json").Open());
                writer.Write("""{"modid":"oreveintracers","version":"1.2.3"}""");
            }

            return buffer.ToArray();
        }
    }

    private async Task<SyncReport> SyncAsync(
        string gameVersion, string? acceptedFor, string? version = null, bool installable = true)
    {
        var http = new HttpClient(new Stub(installable));
        var manifest = new PackManifest
        {
            Id = "p",
            GameVersion = gameVersion,
            Mods = [new PackMod { ModId = "oreveintracers", Version = version, AcceptedFor = acceptedFor }],
        };

        return await new PackSyncer(new ModDbClient(http), http)
            .SyncAsync(manifest, ModsDir, LockPath);
    }

    [Fact]
    public async Task Without_an_acceptance_it_is_refused_as_it_always_was()
    {
        var report = await SyncAsync("1.22.6", acceptedFor: null);

        Assert.True(report.Failed);
        Assert.Contains(report.Steps, s =>
            s.Action == SyncAction.Failed && s.Detail.Contains("not 1.22.6"));
        Assert.Empty(report.Lock.Mods);
    }

    [Fact]
    public async Task With_one_it_installs_and_says_what_it_installed()
    {
        var report = await SyncAsync("1.22.6", acceptedFor: "1.22.6");

        Assert.False(report.Failed);
        Assert.Single(report.Lock.Mods);
        Assert.Equal("1.2.3", report.Lock.Mods[0].Version);

        // Named versions, not "not marked for yours": how far behind the mod is decides
        // whether you believe it still works.
        var warning = Assert.Single(report.Warnings, w => w.ModId == "oreveintracers");
        Assert.Contains("1.21.4", warning.Detail);
        Assert.Contains("may misbehave", warning.Detail);
    }

    [Fact]
    public async Task The_warning_comes_back_on_every_sync_not_just_the_first()
    {
        await SyncAsync("1.22.6", acceptedFor: "1.22.6");

        // Second run: the lock applies now, so nothing is resolved or downloaded — and the
        // pack is still leaning on an untested combination, which is the thing worth
        // saying to somebody looking at it a month later.
        var again = await SyncAsync("1.22.6", acceptedFor: "1.22.6");

        Assert.False(again.Failed);
        Assert.Contains(again.Warnings, w => w.ModId == "oreveintracers");
    }

    [Fact]
    public async Task An_acceptance_from_another_series_fails_and_explains_itself()
    {
        // The pack has been retargeted to 1.23 since somebody accepted this for 1.22.
        var report = await SyncAsync("1.23.0", acceptedFor: "1.22.6");

        Assert.True(report.Failed);

        var failure = Assert.Single(report.Steps, s => s.Action == SyncAction.Failed);
        Assert.Contains("no release marked for game 1.23.0", failure.Detail);
        Assert.Contains("accepted for game 1.22.6", failure.Detail);
    }

    // ---- the way out, named by the failure ----
    //
    // Refusal used to be a dead end. Somebody keeping pack.json by hand was told "1.2.3 exists
    // but is not marked for game 1.22.7", found nothing in it about acceptedFor, and dropped
    // a mod they had run and knew worked.

    [Theory]
    [InlineData(null)]       // follows the newest release
    [InlineData("1.2.3")]    // pinned, which used to fail with a different message
    public async Task Without_an_acceptance_the_failure_says_how_to_give_one(string? version)
    {
        var report = await SyncAsync("1.22.7", acceptedFor: null, version: version);

        Assert.True(report.Failed);

        var failure = Assert.Single(report.Steps, s => s.Action == SyncAction.Failed);
        Assert.Equal("oreveintracers", failure.ModId);
        Assert.Contains("1.2.3 is marked for 1.21.4, not 1.22.7", failure.Detail);
        Assert.Contains("\"acceptedFor\": \"1.22.7\"", failure.Detail);

        // The step already names the mod; the old pinned message named it again.
        Assert.DoesNotContain("oreveintracers", failure.Detail);
    }

    [Fact]
    public async Task A_pinned_release_installs_once_accepted()
    {
        var report = await SyncAsync("1.22.7", acceptedFor: "1.22.7", version: "1.2.3");

        Assert.False(report.Failed);
        Assert.Equal("1.2.3", Assert.Single(report.Lock.Mods).Version);
        Assert.Contains(report.Warnings, w => w.ModId == "oreveintracers");
    }

    [Fact]
    public async Task Nothing_to_accept_means_no_offer_to_accept_it()
    {
        // The only release has no file. Accepting it would install nothing, so saying how
        // to would send somebody to edit pack.json for no result.
        var report = await SyncAsync("1.22.7", acceptedFor: null, installable: false);

        var failure = Assert.Single(report.Steps, s => s.Action == SyncAction.Failed);
        Assert.Contains("no release marked for game 1.22.7", failure.Detail);
        Assert.DoesNotContain("acceptedFor", failure.Detail);
    }

    [Fact]
    public async Task A_stale_acceptance_keeps_its_own_explanation()
    {
        var report = await SyncAsync("1.23.0", acceptedFor: "1.22.6");

        var failure = Assert.Single(report.Steps, s => s.Action == SyncAction.Failed);
        Assert.Contains("accepted for game 1.22.6", failure.Detail);
        Assert.DoesNotContain("\"acceptedFor\"", failure.Detail);
    }
}
