using System.Net;
using System.Security.Cryptography;
using System.Text;
using Cairn.Core.ModDb;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// A mod that ModDB has stopped listing, on a copy that already has it.
///
/// This is the case that put a dedicated server into a restart loop for a week. The zip
/// was on disk with the hash the lock named, the download link still answered, and the
/// sync could not install it: an updated lock arrives with its locations cleared — see
/// PackLock.ClearResolvedLocations — so the mod was resolved by id, ModDB said 404, and
/// the failed entry was dropped from the lock. Every later attempt then started from
/// less than the one before.
///
/// Two rules come out of it. A resolve that fails leaves the lock entry as it was, so
/// the hash and the file survive to be used. And an update whose entry names the same
/// bytes this copy already has keeps where this copy got them, so the next sync has no
/// reason to ask ModDB at all.
/// </summary>
public class UnlistedModTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-unlisted-" + Guid.NewGuid().ToString("n")[..8]);

    private string ModsDir => Path.Combine(_root, "pack", "Mods");
    private string LockPath => Path.Combine(_root, "pack", "pack.lock.json");

    public UnlistedModTests() => Directory.CreateDirectory(ModsDir);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static readonly byte[] ZipBytes = Encoding.UTF8.GetBytes("a mod zip");
    private static string ZipSha => Convert.ToHexStringLower(SHA256.HashData(ZipBytes));

    /// <summary>ModDB as it was while the mod was listed, and as it is once it is not.</summary>
    private sealed class Stub : HttpMessageHandler
    {
        public bool Listed { get; set; } = true;
        public int Lookups { get; private set; }
        public int Downloads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var url = r.RequestUri!.ToString();

            if (url.Contains("/api/mod/"))
            {
                Lookups++;

                // What ModDB actually sends for a mod it does not have: HTTP 200, and the
                // status in the body.
                var body = Listed
                    ? """
                      {"statuscode":"200","mod":{
                        "modid":1,"assetid":2,"name":"augur","urlalias":"augur","side":"both",
                        "releases":[
                          {"releaseid":1,"fileid":1,"modidstr":"augur","modversion":"0.3.0",
                           "filename":"augur_0.3.0.zip",
                           "mainfile":"https://moddbcdn.vintagestory.at/augur_0.3.0.zip","tags":["1.22.5"]}
                        ]
                      }}
                      """
                    : """{"statuscode":"404"}""";

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }

            Downloads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(ZipBytes),
            });
        }
    }

    private static (PackSyncer Syncer, Stub Handler) Make()
    {
        var handler = new Stub();
        var http = new HttpClient(handler);
        return (new PackSyncer(new ModDbClient(http), http), handler);
    }

    private static PackManifest Pack(string gameVersion = "1.22.5") => new()
    {
        Id = "anego",
        GameVersion = gameVersion,
        Mods = [new PackMod { ModId = "augur" }],
    };

    /// <summary>
    /// The entry as an update leaves it: the author's version and hash, no location — and
    /// the zip already here from the last time it was installed.
    /// </summary>
    private void PlantImported(string lockGameVersion = "1.22.5")
    {
        File.WriteAllBytes(Path.Combine(ModsDir, "augur_0.3.0.zip"), ZipBytes);

        new PackLock
        {
            GameVersion = lockGameVersion,
            Mods = [new LockedMod { ModId = "augur", Version = "0.3.0", Sha256 = ZipSha, Side = "both" }],
        }.Save(LockPath);
    }

    [Fact]
    public async Task A_resolve_that_fails_keeps_the_lock_entry_and_the_file()
    {
        PlantImported();
        var (syncer, handler) = Make();
        handler.Listed = false;

        var report = await syncer.SyncAsync(Pack(), ModsDir, LockPath);

        Assert.True(report.Failed);

        var entry = Assert.Single(PackLock.Load(LockPath)!.Mods);
        Assert.Equal("augur", entry.ModId);
        Assert.Equal(ZipSha, entry.Sha256);
        Assert.True(File.Exists(Path.Combine(ModsDir, "augur_0.3.0.zip")));

        // And the attempt after that starts from the same place, not from less.
        await syncer.SyncAsync(Pack(), ModsDir, LockPath);
        Assert.Single(PackLock.Load(LockPath)!.Mods);
    }

    /// <summary>
    /// Keeping the entry is what makes the file worth keeping: a lock that names a zip is a
    /// lock the sweep leaves alone. Pinned here on the settled shape, where the filename is
    /// known, because that is the shape in which the sweep can find the file to delete.
    /// </summary>
    [Fact]
    public async Task A_resolve_that_fails_does_not_hand_the_zip_to_the_sweep()
    {
        var (first, _) = Make();
        await first.SyncAsync(Pack(), ModsDir, LockPath);

        // Retargeting a pin is what sends a settled entry back to ModDB.
        var moved = Pack();
        moved.Mods[0].Version = "0.4.0";

        var (second, handler) = Make();
        handler.Listed = false;
        var report = await second.SyncAsync(moved, ModsDir, LockPath);

        Assert.True(report.Failed);
        Assert.True(File.Exists(Path.Combine(ModsDir, "augur_0.3.0.zip")));
        Assert.Equal("0.3.0", Assert.Single(PackLock.Load(LockPath)!.Mods).Version);
    }

    [Fact]
    public async Task An_entry_for_another_game_version_is_not_carried()
    {
        PlantImported(lockGameVersion: "1.21.0");
        var (syncer, handler) = Make();
        handler.Listed = false;

        await syncer.SyncAsync(Pack("1.22.5"), ModsDir, LockPath);

        Assert.Empty(PackLock.Load(LockPath)!.Mods);
    }

    private static PackBundle Bundle(PackManifest pack, PackLock locked, int revision) => new()
    {
        Pack = pack,
        Lock = locked,
        CanonicalUrl = "https://cairns.gg/dizzyd/anego",
        Revision = revision,
    };

    private static PackLock AuthorsLock(string sha256) => new()
    {
        GameVersion = "1.22.5",
        Mods =
        [
            new LockedMod
            {
                ModId = "augur", Version = "0.3.0", FileName = "augur_0.3.0.zip",
                Url = "https://moddbcdn.vintagestory.at/augur_0.3.0.zip", Sha256 = sha256, Side = "both",
            },
        ],
    };

    /// <summary>
    /// The whole story, end to end: followed while listed, updated after unlisting, and
    /// still starting. The update's entry names the bytes this copy has, so the copy keeps
    /// the address it resolved for itself, and the sync that follows never asks ModDB.
    /// </summary>
    [Fact]
    public async Task An_update_that_names_the_same_bytes_keeps_where_this_copy_got_them()
    {
        var store = new PackStore(_root);
        store.Import(Bundle(Pack(), AuthorsLock(ZipSha), revision: 1),
            sourceUrl: "https://cairns.gg/dizzyd/anego");

        var (listed, _) = Make();
        var first = await listed.SyncAsync(store.Load("anego"), store.ModsDir("anego"), store.LockPath("anego"));
        Assert.False(first.Failed);

        // The mod is unlisted; the author publishes a revision that changes nothing about it.
        var plan = PackUpdatePlan.Between(
            store.Load("anego"), Pack(), store.LoadUpstream("anego"), state: store.LoadLocalState("anego"));
        store.ApplyUpdate("anego", plan, Bundle(Pack(), AuthorsLock(ZipSha), revision: 2));

        var kept = Assert.Single(store.LoadLock("anego")!.Mods);
        Assert.Equal("https://moddbcdn.vintagestory.at/augur_0.3.0.zip", kept.Url);
        Assert.Equal("augur_0.3.0.zip", kept.FileName);

        var (unlisted, handler) = Make();
        handler.Listed = false;
        var report = await unlisted.SyncAsync(store.Load("anego"), store.ModsDir("anego"), store.LockPath("anego"));

        Assert.False(report.Failed);
        Assert.Equal(0, handler.Lookups);
        Assert.Equal(0, handler.Downloads);
    }

    /// <summary>
    /// The safeguard the rule above rests on. An author's entry that names other bytes —
    /// a rebuilt release, or a document somebody rewrote — gets the fresh resolve it
    /// always did, and nothing this copy knew is attached to it.
    /// </summary>
    [Fact]
    public async Task An_update_that_names_other_bytes_is_resolved_afresh()
    {
        var store = new PackStore(_root);
        store.Import(Bundle(Pack(), AuthorsLock(ZipSha), revision: 1),
            sourceUrl: "https://cairns.gg/dizzyd/anego");

        var (listed, _) = Make();
        await listed.SyncAsync(store.Load("anego"), store.ModsDir("anego"), store.LockPath("anego"));

        var plan = PackUpdatePlan.Between(
            store.Load("anego"), Pack(), store.LoadUpstream("anego"), state: store.LoadLocalState("anego"));
        store.ApplyUpdate("anego", plan, Bundle(Pack(), AuthorsLock(new string('f', 64)), revision: 2));

        var entry = Assert.Single(store.LoadLock("anego")!.Mods);
        Assert.Equal("", entry.Url);
        Assert.Equal("", entry.FileName);
    }
}
