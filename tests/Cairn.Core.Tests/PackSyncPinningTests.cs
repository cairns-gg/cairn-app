using System.IO.Compression;
using System.Net;
using System.Text;
using Cairn.Core.ModDb;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Syncing installs what the lockfile already says.
///
/// It used to re-resolve every unpinned mod against ModDB on every sync — and sync runs
/// on every Play — so launching could silently move a pack's mods. Mods break saves, so
/// "it worked yesterday" needs to stay explicable. Updating is now something you ask for.
/// </summary>
public class PackSyncPinningTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-sync-" + Guid.NewGuid().ToString("n")[..8]);

    private string ModsDir => Path.Combine(_root, "Mods");
    private string LockPath => Path.Combine(_root, "pack.lock.json");

    public PackSyncPinningTests() => Directory.CreateDirectory(ModsDir);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// Serves one mod whose newest release is configurable, and counts lookups.
    ///
    /// Download URLs use ModDB's real CDN host because the syncer will not follow a
    /// locked URL pointing anywhere else — see UntrustedLockTests.
    /// </summary>
    private sealed class Stub(string newest) : HttpMessageHandler
    {
        public int Lookups { get; private set; }
        public int Downloads { get; private set; }

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
                    {"releaseid":1,"fileid":1,"modidstr":"olla","modversion":"{{newest}}",
                     "filename":"olla_{{newest}}.zip",
                     "mainfile":"https://moddbcdn.vintagestory.at/olla_{{newest}}.zip",
                     "tags":["1.22.5"]},
                    {"releaseid":2,"fileid":2,"modidstr":"olla","modversion":"1.0.0",
                     "filename":"olla_1.0.0.zip",
                     "mainfile":"https://moddbcdn.vintagestory.at/olla_1.0.0.zip",
                     "tags":["1.22.5"]}
                  ]
                }
                }
                """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }

            Downloads++;

            // A real archive, because sync reads modinfo.json out of every mod it installs
            // and now says so when it cannot. Placeholder bytes made every download warn
            // that its zip would not open — true, and nothing to do with pinning.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                // Content differs per version, so the recorded checksums differ too.
                Content = new ByteArrayContent(Zip($"zip for {url}")),
            });
        }

        /// <summary>A minimal but genuine mod zip, its comment carrying the unique bytes.</summary>
        private static byte[] Zip(string marker)
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = zip.CreateEntry("modinfo.json");
                using var writer = new StreamWriter(entry.Open());
                writer.Write($$"""{"type":"content","modid":"olla","name":"{{marker}}"}""");
            }

            return buffer.ToArray();
        }
    }

    private (PackSyncer Syncer, Stub Handler) Make(string newest = "1.0.0")
    {
        var handler = new Stub(newest);
        var http = new HttpClient(handler);
        return (new PackSyncer(new ModDbClient(http), http), handler);
    }

    private static PackManifest Pack(string? pin = null) => new()
    {
        Id = "anego",
        GameVersion = "1.22.5",
        Mods = [new PackMod { ModId = "olla", Version = pin }],
    };

    [Fact]
    public async Task A_first_sync_resolves_and_installs_the_newest()
    {
        var (syncer, handler) = Make("1.2.0");

        var report = await syncer.SyncAsync(Pack(), ModsDir, LockPath);

        Assert.False(report.Failed);
        Assert.Equal("1.2.0", report.Lock.Mods.Single().Version);
        Assert.Equal(1, handler.Lookups);
    }

    [Fact]
    public async Task Syncing_again_after_a_new_release_keeps_what_is_installed()
    {
        var (first, _) = Make("1.2.0");
        await first.SyncAsync(Pack(), ModsDir, LockPath);

        // The mod ships 1.3.0 in the meantime, and the pack is launched again.
        var (second, handler) = Make("1.3.0");
        var report = await second.SyncAsync(Pack(), ModsDir, LockPath);

        // The whole point: launching does not move the mods under a save.
        Assert.Equal("1.2.0", report.Lock.Mods.Single().Version);
        Assert.All(report.Steps, s => Assert.Equal(SyncAction.Unchanged, s.Action));
    }

    [Fact]
    public async Task A_settled_pack_syncs_without_touching_ModDB_at_all()
    {
        var (first, _) = Make("1.2.0");
        await first.SyncAsync(Pack(), ModsDir, LockPath);

        var (second, handler) = Make("1.3.0");
        await second.SyncAsync(Pack(), ModsDir, LockPath);

        // Everything needed is in the lock, so a synced pack launches offline.
        Assert.Equal(0, handler.Lookups);
        Assert.Equal(0, handler.Downloads);
    }

    [Fact]
    public async Task An_update_happens_only_when_it_is_asked_for()
    {
        var (first, _) = Make("1.2.0");
        await first.SyncAsync(Pack(), ModsDir, LockPath);

        var (second, _) = Make("1.3.0");
        var report = await second.SyncAsync(Pack(), ModsDir, LockPath,
            allowUpdates: new HashSet<string>(["olla"], StringComparer.OrdinalIgnoreCase));

        Assert.Equal("1.3.0", report.Lock.Mods.Single().Version);
        Assert.Contains(report.Steps, s => s.Action == SyncAction.Updated && s.Detail == "1.2.0 -> 1.3.0");
    }

    [Fact]
    public async Task Checking_for_updates_reports_without_changing_anything()
    {
        var (first, _) = Make("1.2.0");
        await first.SyncAsync(Pack(), ModsDir, LockPath);

        var (second, _) = Make("1.3.0");
        var updates = await second.CheckUpdatesAsync(Pack(), LockPath);

        Assert.Equal("olla 1.2.0 -> 1.3.0", updates.Single().Describe());

        // Reporting is not applying.
        Assert.Equal("1.2.0", PackLock.Load(LockPath)!.Mods.Single().Version);
    }

    [Fact]
    public async Task Nothing_to_report_when_the_installed_version_is_the_newest()
    {
        var (syncer, _) = Make("1.2.0");
        await syncer.SyncAsync(Pack(), ModsDir, LockPath);

        Assert.Empty(await syncer.CheckUpdatesAsync(Pack(), LockPath));
    }

    [Fact]
    public async Task A_pinned_mod_is_never_offered_an_update()
    {
        var (first, _) = Make("1.2.0");
        await first.SyncAsync(Pack(pin: "1.0.0"), ModsDir, LockPath);
        Assert.Equal("1.0.0", PackLock.Load(LockPath)!.Mods.Single().Version);

        var (second, _) = Make("1.3.0");

        // A pin says stay put; nagging about it would defeat the purpose.
        Assert.Empty(await second.CheckUpdatesAsync(Pack(pin: "1.0.0"), LockPath));

        // And even an explicit update leaves a pinned mod where it is.
        var report = await second.SyncAsync(Pack(pin: "1.0.0"), ModsDir, LockPath,
            allowUpdates: new HashSet<string>(["olla"], StringComparer.OrdinalIgnoreCase));
        Assert.Equal("1.0.0", report.Lock.Mods.Single().Version);
    }

    [Fact]
    public async Task Changing_a_pin_moves_the_mod_without_asking_for_an_update()
    {
        var (first, _) = Make("1.2.0");
        await first.SyncAsync(Pack(), ModsDir, LockPath);

        // Pinning to an older release is an instruction, not an update.
        var (second, _) = Make("1.2.0");
        var report = await second.SyncAsync(Pack(pin: "1.0.0"), ModsDir, LockPath);

        Assert.Equal("1.0.0", report.Lock.Mods.Single().Version);
    }

    [Fact]
    public async Task Retargeting_the_pack_at_another_game_version_re_resolves()
    {
        var (first, _) = Make("1.2.0");
        await first.SyncAsync(Pack(), ModsDir, LockPath);

        var moved = Pack();
        moved.GameVersion = "1.21.5";

        var (second, handler) = Make("1.2.0");
        await second.SyncAsync(moved, ModsDir, LockPath);

        // The locked release was chosen for a different game version, so it cannot stand.
        Assert.True(handler.Lookups > 0);
    }

    [Fact]
    public async Task A_deleted_zip_is_restored_from_the_lock_not_re_resolved()
    {
        var (first, _) = Make("1.2.0");
        await first.SyncAsync(Pack(), ModsDir, LockPath);

        foreach (var zip in Directory.EnumerateFiles(ModsDir, "*.zip")) File.Delete(zip);

        var (second, handler) = Make("1.3.0");
        var report = await second.SyncAsync(Pack(), ModsDir, LockPath);

        Assert.Equal("1.2.0", report.Lock.Mods.Single().Version);
        Assert.Equal(1, handler.Downloads);
        Assert.Equal(0, handler.Lookups);
    }
}
