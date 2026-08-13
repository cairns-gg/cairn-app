using System.Net;
using System.Text;
using Cairn.Core.ModDb;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// A shared pack arrives with its author's lockfile, so every field in it started out
/// attacker-supplied. Two of them used to be obeyed: the download URL and the filename,
/// the latter combined straight into the directory handed to the game via --addModPath.
/// Mods are code.
///
/// The lock may still say WHAT to install. It no longer says where from, or where to.
/// PackStore.Import now clears the location fields on the way in as well — see
/// PackLock.ClearResolvedLocations — so these exercise the second line of that defence:
/// what PackSyncer does when a lock reaches it carrying them anyway, which is what a
/// hand-edited file or a future caller that forgets would produce.
/// </summary>
public class UntrustedLockTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-untrusted-" + Guid.NewGuid().ToString("n")[..8]);

    private string ModsDir => Path.Combine(_root, "pack", "Mods");
    private string LockPath => Path.Combine(_root, "pack", "pack.lock.json");

    public UntrustedLockTests() => Directory.CreateDirectory(ModsDir);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private const string Cdn = "https://moddbcdn.vintagestory.at";
    private const string Evil = "https://attacker.example";

    /// <summary>
    /// Serves ModDB's API, and counts what each host was asked for.
    /// </summary>
    /// <param name="extensions">
    /// Per-mod file extension, defaulting to zip. ModDB takes dll and cs uploads too, so a
    /// release filename can be any of the three, and a test that only ever sees zips cannot
    /// tell whether the sweep handles the others.
    /// </param>
    private sealed class Stub(Dictionary<string, string>? extensions = null) : HttpMessageHandler
    {
        public int Lookups { get; private set; }
        public List<string> Downloaded { get; } = [];

        public int DownloadsFrom(string prefix) =>
            Downloaded.Count(u => u.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        public string FileNameFor(string modId) =>
            $"{modId}_1.0.0.{(extensions?.GetValueOrDefault(modId) ?? "zip")}";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var url = r.RequestUri!.ToString();

            if (url.Contains("/api/mod/"))
            {
                Lookups++;
                var id = url[(url.LastIndexOf('/') + 1)..];
                var file = FileNameFor(id);
                var body = $$"""
                {"statuscode":"200","mod":{
                  "modid":1,"assetid":2,"name":"{{id}}","urlalias":"{{id}}","side":"client",
                  "releases":[
                    {"releaseid":1,"fileid":1,"modidstr":"{{id}}","modversion":"1.0.0",
                     "filename":"{{file}}",
                     "mainfile":"{{Cdn}}/{{file}}","tags":["1.22.5"]}
                  ]
                }
                }
                """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }

            Downloaded.Add(url);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("a mod zip")),
            });
        }
    }

    private (PackSyncer Syncer, Stub Handler) Make(Dictionary<string, string>? extensions = null)
    {
        var handler = new Stub(extensions);
        var http = new HttpClient(handler);
        return (new PackSyncer(new ModDbClient(http), http), handler);
    }

    private static PackManifest Pack() => new()
    {
        Id = "anego",
        GameVersion = "1.22.5",
        Mods = [new PackMod { ModId = "olla", Version = "1.0.0" }],
    };

    /// <summary>Writes a lock exactly as importing a shared pack would.</summary>
    private void GiveLock(string url, string fileName, string sha256 = "")
    {
        new PackLock
        {
            GameVersion = "1.22.5",
            Mods =
            [
                new LockedMod
                {
                    ModId = "olla", Version = "1.0.0", FileName = fileName,
                    Url = url, ReleaseId = 1, FileId = 1, Sha256 = sha256, Side = "client",
                },
            ],
        }.Save(LockPath);
    }

    [Fact]
    public async Task A_lock_pointing_at_another_host_is_installed_from_ModDB_instead()
    {
        GiveLock($"{Evil}/payload.zip", "olla_1.0.0.zip");

        var (syncer, handler) = Make();
        var report = await syncer.SyncAsync(Pack(), ModsDir, LockPath);

        // The hostile host must never be contacted at all.
        Assert.Equal(0, handler.DownloadsFrom(Evil));
        Assert.Equal(1, handler.DownloadsFrom(Cdn));

        // And the pack still installs, rather than being refused.
        Assert.False(report.Failed);
        Assert.Equal("1.0.0", report.Lock.Mods.Single().Version);
        Assert.True(File.Exists(Path.Combine(ModsDir, "olla_1.0.0.zip")));
    }

    [Fact]
    public async Task Rewriting_the_locked_url_costs_one_lookup_not_a_different_version()
    {
        GiveLock($"{Evil}/payload.zip", "olla_1.0.0.zip");

        var (syncer, handler) = Make();
        var report = await syncer.SyncAsync(Pack(), ModsDir, LockPath);

        Assert.Equal(1, handler.Lookups);
        Assert.Equal("1.0.0", report.Lock.Mods.Single().Version);

        // The URL recorded afterwards is ModDB's, so the hostile one does not persist.
        Assert.StartsWith(Cdn, report.Lock.Mods.Single().Url);
    }

    [Theory]
    [InlineData("../../../../evil.zip")]
    [InlineData("..\\..\\evil.zip")]
    [InlineData("/tmp/evil.zip")]
    [InlineData("sub/dir/evil.zip")]
    [InlineData("..")]
    public async Task A_lock_filename_cannot_escape_the_mods_directory(string fileName)
    {
        GiveLock($"{Cdn}/olla_1.0.0.zip", fileName);

        var (syncer, _) = Make();
        var report = await syncer.SyncAsync(Pack(), ModsDir, LockPath);

        // Refused rather than quietly rewritten, so it is visible in the log.
        Assert.True(report.Failed);
        Assert.Contains(report.Steps, s =>
            s.Action == SyncAction.Failed && s.Detail.Contains("plain file name"));

        // Nothing was written anywhere outside Mods/.
        var escaped = Path.GetFullPath(Path.Combine(ModsDir, fileName));
        Assert.False(File.Exists(escaped), $"wrote outside Mods/: {escaped}");
        Assert.Empty(Directory.GetFiles(ModsDir));
    }

    [Fact]
    public async Task A_mod_file_the_pack_no_longer_lists_is_removed_whatever_kind_it_is()
    {
        // ModDB takes dll and cs uploads as well as zip, so a release filename can be any
        // of the three — and the sweep used to look only for *.zip. Anything else Cairn
        // installed stayed in the mod path for ever: named by no lock, counted by nothing,
        // and still loaded by the game long after the mod was taken out of the pack.
        //
        // Installed through Cairn rather than written by hand, which is the whole point:
        // the sweep works from its own record of what it put there, so a test that fakes
        // the files proves nothing about the case it is named for — and would instead be
        // asserting that Cairn deletes files somebody else placed.
        var (syncer, stub) = Make(new Dictionary<string, string>
        {
            ["aaalib"] = "dll",
            ["snippet"] = "cs",
        });

        var full = new PackManifest
        {
            Id = "anego",
            GameVersion = "1.22.5",
            Mods =
            [
                new PackMod { ModId = "olla", Version = "1.0.0" },
                new PackMod { ModId = "aaalib", Version = "1.0.0" },
                new PackMod { ModId = "snippet", Version = "1.0.0" },
            ],
        };

        await syncer.SyncAsync(full, ModsDir, LockPath);

        var installed = new[] { "aaalib", "snippet" }.Select(stub.FileNameFor).ToArray();
        foreach (var name in installed)
            Assert.True(File.Exists(Path.Combine(ModsDir, name)), $"never installed: {name}");

        // Something Cairn did not put there stays put, whatever it is called.
        var mine = Path.Combine(ModsDir, "notes.txt");
        File.WriteAllText(mine, "mine");

        // Now the pack drops back to one mod: the other two are Cairn's to take away.
        var report = await syncer.SyncAsync(Pack(), ModsDir, LockPath);

        foreach (var name in installed)
            Assert.False(File.Exists(Path.Combine(ModsDir, name)), $"left behind: {name}");

        // And said so, rather than removing them quietly.
        var removed = report.Steps.Where(s => s.Action == SyncAction.Removed).ToList();
        Assert.Equal(2, removed.Count);
        Assert.All(removed, s => Assert.Equal("no longer in pack", s.Detail));

        Assert.True(File.Exists(mine));
        Assert.True(File.Exists(Path.Combine(ModsDir, "olla_1.0.0.zip")));
    }

    /// <summary>
    /// The other half of the same rule, and the defect the widened sweep introduced: a
    /// loose mod somebody placed by hand is not Cairn's to delete. Running a mod ModDB does
    /// not serve is exactly what a loose .dll or .cs in the mod path is for, sync runs on
    /// every Play, and nothing puts one back.
    /// </summary>
    [Fact]
    public async Task A_mod_file_placed_by_hand_survives_every_sync()
    {
        var (syncer, _) = Make();
        await syncer.SyncAsync(Pack(), ModsDir, LockPath);

        var mine = new[] { "handmade.dll", "tweak.cs", "sideloaded.zip", "notes.txt" };
        foreach (var name in mine)
            File.WriteAllText(Path.Combine(ModsDir, name), "mine");

        // Twice, because the failure mode was "it goes on the next launch", and a sweep
        // that consults the previous lock must not start counting them as its own.
        await syncer.SyncAsync(Pack(), ModsDir, LockPath);
        await syncer.SyncAsync(Pack(), ModsDir, LockPath);

        foreach (var name in mine)
            Assert.True(File.Exists(Path.Combine(ModsDir, name)), $"deleted somebody's file: {name}");

        Assert.True(File.Exists(Path.Combine(ModsDir, "olla_1.0.0.zip")));
    }

    [Fact]
    public async Task A_settled_pack_still_syncs_without_touching_ModDB()
    {
        // The regression this fix could plausibly cause: if every sync re-resolved to
        // avoid trusting the lock, launching would stop working offline.
        var (first, _) = Make();
        await first.SyncAsync(Pack(), ModsDir, LockPath);

        var (second, handler) = Make();
        await second.SyncAsync(Pack(), ModsDir, LockPath);

        Assert.Equal(0, handler.Lookups);
        Assert.Empty(handler.Downloaded);
    }

    [Fact]
    public void A_ModDB_url_is_recognised_and_anything_else_is_not()
    {
        Assert.True(ModDbUrls.IsKnownDownloadHost($"{Cdn}/olla_1.0.0.zip?dl=olla.zip"));
        Assert.True(ModDbUrls.IsKnownDownloadHost("https://mods.vintagestory.at/download?fileid=1"));

        Assert.False(ModDbUrls.IsKnownDownloadHost($"{Evil}/payload.zip"));
        Assert.False(ModDbUrls.IsKnownDownloadHost(null));
        Assert.False(ModDbUrls.IsKnownDownloadHost("not a url"));

        // http is not good enough even on the right host.
        Assert.False(ModDbUrls.IsKnownDownloadHost("http://moddbcdn.vintagestory.at/x.zip"));

        // Nor is a lookalike that merely ends with it.
        Assert.False(ModDbUrls.IsKnownDownloadHost("https://evil-moddbcdn.vintagestory.at.attacker.example/x.zip"));
    }
}
