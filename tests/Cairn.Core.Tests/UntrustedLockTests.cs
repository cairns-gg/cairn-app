using System.Net;
using System.Text;
using Cairn.Core.ModDb;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// A shared pack arrives with its author's lockfile, and PackStore.Import writes it
/// verbatim — so every field in it is attacker-supplied. Two of them used to be obeyed:
/// the download URL and the filename, the latter combined straight into the directory
/// handed to the game via --addModPath. Mods are code.
///
/// The lock may still say WHAT to install. It no longer says where from, or where to.
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

    /// <summary>Serves ModDB's API, and counts what each host was asked for.</summary>
    private sealed class Stub : HttpMessageHandler
    {
        public int Lookups { get; private set; }
        public List<string> Downloaded { get; } = [];

        public int DownloadsFrom(string prefix) =>
            Downloaded.Count(u => u.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var url = r.RequestUri!.ToString();

            if (url.Contains("/api/mod/"))
            {
                Lookups++;
                var body = $$"""
                {"statuscode":"200","mod":{
                  "modid":1,"assetid":2,"name":"Olla","urlalias":"olla","side":"client",
                  "releases":[
                    {"releaseid":1,"fileid":1,"modidstr":"olla","modversion":"1.0.0",
                     "filename":"olla_1.0.0.zip",
                     "mainfile":"{{Cdn}}/olla_1.0.0.zip","tags":["1.22.5"]}
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

    private (PackSyncer Syncer, Stub Handler) Make()
    {
        var handler = new Stub();
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
