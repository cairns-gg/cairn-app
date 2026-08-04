using System.Net;
using System.Text;
using Cairn.Core.ModDb;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// ModDB keeps the row for a release whose file has gone, and serves it as
/// <c>"fileid": null</c> with an empty <c>mainfile</c>. Jaunt 3.0.0-rc.1 is one, which
/// mattered because Equus requires jaunt: modelling fileid as a plain int made that one
/// row poison the whole mod, and the JsonException escaped every catch in the syncer.
/// Sync died after downloading equus and genelib but before writing the lock, so the pack
/// was left with zips nothing accounted for and re-downloaded them on every retry.
///
/// Two separate faults, and both are needed: reading the entry at all, and not then
/// choosing the release that has no file.
/// </summary>
public class ModDbBrokenReleaseTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-broken-" + Guid.NewGuid().ToString("n")[..8]);

    private string ModsDir => Path.Combine(_root, "Mods");
    private string LockPath => Path.Combine(_root, "pack.lock.json");

    public ModDbBrokenReleaseTests() => Directory.CreateDirectory(ModsDir);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// Jaunt as the live API serves it, trimmed to three releases. The fileless one is
    /// listed first and marked for the exact game version, so a resolve that does not
    /// exclude it will pick it.
    /// </summary>
    private const string Jaunt = """
    {"statuscode":"200","mod":{
      "modid":4254,"assetid":25128,"name":"Jaunt","urlalias":"jaunt","side":"both",
      "releases":[
        {"releaseid":36202,"fileid":null,"modidstr":"jaunt","modversion":"3.0.0-rc.1",
         "filename":"","mainfile":"","tags":["1.22.5"]},
        {"releaseid":49279,"fileid":107504,"modidstr":"jaunt","modversion":"3.1.0",
         "filename":"jaunt_3.1.0.zip",
         "mainfile":"https://moddbcdn.vintagestory.at/jaunt_3.1.0.zip","tags":["1.22.5"]},
        {"releaseid":37168,"fileid":82299,"modidstr":"jaunt","modversion":"3.0.0-rc.3",
         "filename":"jaunt_3.0.0-rc.3.zip",
         "mainfile":"https://moddbcdn.vintagestory.at/jaunt_3.0.0-rc.3.zip","tags":["1.22.5"]}
      ]}}
    """;

    private sealed class Stub(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private static ModDbClient Client(string body) => new(new HttpClient(new Stub(body)));

    [Fact]
    public async Task A_release_with_a_null_fileid_does_not_make_the_mod_unreadable()
    {
        var mod = await Client(Jaunt).GetModAsync("jaunt");

        Assert.Equal(3, mod.Releases.Count);
        Assert.Null(mod.Releases[0].FileId);
        Assert.Equal(107504, mod.Releases[1].FileId);
    }

    [Fact]
    public async Task A_release_with_no_file_never_wins_a_resolve()
    {
        var release = await Client(Jaunt).ResolveAsync("jaunt", "1.22.5");

        // 3.0.0-rc.1 is marked for 1.22.5 exactly and would out-rank nothing else, but it
        // cannot be downloaded — the newest release that actually has a file wins.
        Assert.NotNull(release);
        Assert.Equal("3.1.0", release.ModVersion);
    }

    [Fact]
    public async Task A_release_with_no_file_is_not_offered_as_a_choice()
    {
        var releases = await Client(Jaunt).ListCompatibleReleasesAsync("jaunt", "1.22.5");

        Assert.Equal(["3.1.0", "3.0.0-rc.3"], releases.Select(r => r.ModVersion).ToArray());
    }

    [Fact]
    public async Task Pinning_to_a_release_with_no_file_is_refused_rather_than_attempted()
    {
        var e = await Assert.ThrowsAsync<ModDbException>(
            () => Client(Jaunt).ResolveAsync("jaunt", "1.22.5", "3.0.0-rc.1"));

        // It exists, so saying "no such release" would send the user looking for a typo.
        Assert.Contains("not marked for game", e.Message);
    }

    /// <summary>
    /// Cairn's own ModDB entry, which is a download link rather than a mod and so has no
    /// modid to put on its release. Found by tools/moddb-audit.cs, sixteen mods into a
    /// sample of eight thousand.
    /// </summary>
    private const string NoModIdStr = """
    {"statuscode":"200","mod":{
      "modid":10742,"assetid":62619,"name":"Cairn — Modpack Manager","urlalias":null,"side":"both",
      "releases":[
        {"releaseid":49999,"fileid":107999,"modidstr":null,"modversion":"1.0.0",
         "filename":"cairn-download.zip",
         "mainfile":"https://moddbcdn.vintagestory.at/cairn-download.zip","tags":["1.22.5"]}
      ]}}
    """;

    [Fact]
    public async Task A_release_with_no_modidstr_falls_back_to_the_mod_name()
    {
        var release = await Client(NoModIdStr).ResolveAsync("cairn", "1.22.5");

        // The point of the test is the guard at the call site, not the null itself: the
        // property is nullable, so the compiler no longer promises this cannot happen, and
        // tightening IsNullOrEmpty to a length check would put the null straight into a
        // ResolvedRelease and on into the lockfile.
        Assert.NotNull(release);
        Assert.Equal("Cairn — Modpack Manager", release.ModId);
    }

    [Fact]
    public async Task A_body_that_cannot_be_parsed_is_a_ModDbException()
    {
        // Callers all handle ModDbException by failing the one mod they asked about; a raw
        // JsonException escaped every one of them.
        var e = await Assert.ThrowsAsync<ModDbException>(
            () => Client("""{"statuscode":"200","mod":{"modid":"not a number"}}""").GetModAsync("jaunt"));

        Assert.Contains("could not read", e.Message);
    }

    [Fact]
    public async Task An_unreadable_entry_fails_one_mod_and_still_writes_the_lock()
    {
        // Two mods, and ModDB's answer for the second one is unreadable whatever it is
        // asked. The first must still end up installed and locked.
        var handler = new Broken(good: "carryon");
        var http = new HttpClient(handler);
        var syncer = new PackSyncer(new ModDbClient(http), http);

        var manifest = new PackManifest
        {
            Id = "anego",
            GameVersion = "1.22.5",
            Mods = [new PackMod { ModId = "carryon" }, new PackMod { ModId = "jaunt" }],
        };

        var report = await syncer.SyncAsync(manifest, ModsDir, LockPath);

        Assert.True(report.Failed);
        Assert.Equal(["jaunt"], report.Steps
            .Where(s => s.Action == SyncAction.Failed).Select(s => s.ModId).ToArray());

        // The whole point: the run finished, so the good mod is locked and the lockfile
        // exists. Without it the next sync starts from nothing and repeats the download.
        Assert.Equal(["carryon"], report.Lock.Mods.Select(m => m.ModId).ToArray());
        Assert.True(File.Exists(LockPath));
    }

    /// <summary>Serves one usable mod; every other id gets a body that will not parse.</summary>
    private sealed class Broken(string good) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var url = r.RequestUri!.ToString();

            if (!url.Contains("/api/mod/"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([0x50, 0x4B, 0x05, 0x06, .. new byte[18]]),
                });

            var id = url[(url.LastIndexOf('/') + 1)..];

            var body = id == good
                ? $$$"""
                  {"statuscode":"200","mod":{"modid":1,"assetid":2,"name":"{{{id}}}","side":"client",
                    "releases":[{"releaseid":1,"fileid":1,"modidstr":"{{{id}}}","modversion":"1.0.0",
                      "filename":"{{{id}}}_1.0.0.zip",
                      "mainfile":"https://moddbcdn.vintagestory.at/{{{id}}}_1.0.0.zip",
                      "tags":["1.22.5"]}]}}
                  """
                : """{"statuscode":"200","mod":{"modid":1,"releases":[{"releaseid":"nope"}]}}""";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
