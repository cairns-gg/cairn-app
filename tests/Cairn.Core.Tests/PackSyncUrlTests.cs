using System.IO.Compression;
using System.Net;
using System.Text;
using Cairn.Core.ModDb;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// A mod the manifest points at by address rather than by ModDB id.
///
/// The address says where and the lock says what: the first sync records the hash of the
/// file it fetched, every sync after that refuses a file whose bytes have moved, and
/// taking a new build is an update asked for like any other. These hold the syncer to that,
/// and to the one thing an address must never do — let a lock entry from a different kind
/// of install bind the wrong version or the wrong hash.
/// </summary>
public class PackSyncUrlTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-modurl-" + Guid.NewGuid().ToString("n")[..8]);

    private string ModsDir => Path.Combine(_root, "Mods");
    private string LockPath => Path.Combine(_root, "pack.lock.json");

    public PackSyncUrlTests() => Directory.CreateDirectory(ModsDir);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private const string Private = "https://files.example/anegotweaks.zip";

    /// <summary>ModDB's own download form, as the site's Download button hands it out.</summary>
    private const string OnModDb = "https://mods.vintagestory.at/download/118768/augur_0.1.1.zip";

    /// <summary>
    /// Serves whatever bytes each address is set to, and ModDB's API for one mod so the two
    /// kinds of entry can share a pack. Counts what was asked for, because "no request to
    /// ModDB" and "no download" are both things these tests assert.
    /// </summary>
    private sealed class Stub : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Requested { get; } = [];

        public int Lookups => Requested.Count(u => u.Contains("/api/mod/"));
        public int Downloads(string url) => Requested.Count(u => string.Equals(u, url, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Mods the API lists, beyond olla. Anything else asked for answers 404 — which is
        /// what ModDB says about a mod that has been uploaded and not yet published.
        /// </summary>
        public HashSet<string> Listed { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var url = r.RequestUri!.ToString();
            Requested.Add(url);

            if (url.Contains("/api/mod/"))
            {
                var id = url[(url.LastIndexOf('/') + 1)..];

                if (Listed.Contains(id))
                {
                    var listed = $$"""
                    {"statuscode":"200","mod":{
                      "modid":9,"assetid":10,"name":"Augur","urlalias":"augur","side":"universal",
                      "releases":[
                        {"releaseid":7,"fileid":118768,"modidstr":"{{id}}","modversion":"0.1.1",
                         "filename":"augur_0.1.1.zip",
                         "mainfile":"https://moddbcdn.vintagestory.at/augur_0.1.1.zip",
                         "tags":["1.22.5"]}
                      ]
                    }
                    }
                    """;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(listed, Encoding.UTF8, "application/json"),
                    });
                }

                if (!string.Equals(id, "olla", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"statuscode":"404"}""", Encoding.UTF8, "application/json"),
                    });

                var body = """
                {"statuscode":"200","mod":{
                  "modid":1,"assetid":2,"name":"Olla","urlalias":"olla","side":"client",
                  "releases":[
                    {"releaseid":1,"fileid":1,"modidstr":"olla","modversion":"1.0.0",
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

            if (url.StartsWith("https://moddbcdn.vintagestory.at/augur", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Zip("augur", "0.1.1")),
                });

            if (url.StartsWith("https://moddbcdn.vintagestory.at/", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Zip("olla", "1.0.0")),
                });

            return Task.FromResult(Files.TryGetValue(url, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    /// <summary>A genuine mod zip, deterministic so the same content hashes the same twice.</summary>
    internal static byte[] Zip(string modId, string version, string? side = null, string? extra = null)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("modinfo.json");
            entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var writer = new StreamWriter(entry.Open());
            var sideField = side is null ? "" : $",\"side\":\"{side}\"";
            writer.Write(
                $"{{\"type\":\"content\",\"modid\":\"{modId}\",\"name\":\"Anego Tweaks\","
                + $"\"version\":\"{version}\"{sideField}{extra}}}");
        }

        return buffer.ToArray();
    }

    private static PackManifest Pack(params PackMod[] mods) => new()
    {
        Id = "anego", GameVersion = "1.22.5", Mods = [.. mods],
    };

    private static PackMod FromUrl(string modId = "anegotweaks", string url = Private) =>
        new() { ModId = modId, Url = url };

    private (PackSyncer Syncer, Stub Handler) Make()
    {
        var handler = new Stub();
        var http = new HttpClient(handler);
        return (new PackSyncer(new ModDbClient(http), http), handler);
    }

    [Fact]
    public async Task Installs_from_the_address_and_records_what_the_zip_says()
    {
        var (syncer, stub) = Make();
        stub.Files[Private] = Zip("anegotweaks", "1.0.0", side: "Universal");

        var report = await syncer.SyncAsync(Pack(FromUrl()), ModsDir, LockPath);

        Assert.False(report.Failed);
        Assert.Equal(0, stub.Lookups);

        var locked = Assert.Single(report.Lock.Mods);
        Assert.Equal("anegotweaks", locked.ModId);
        Assert.Equal("1.0.0", locked.Version);
        Assert.Equal("anegotweaks.zip", locked.FileName);
        Assert.Equal(Private, locked.Url);
        Assert.True(locked.FromUrl);
        Assert.Equal("universal", locked.Side);
        Assert.Equal(64, locked.Sha256.Length);
        Assert.True(File.Exists(Path.Combine(ModsDir, "anegotweaks.zip")));

        var step = Assert.Single(report.Steps);
        Assert.Equal(SyncAction.Downloaded, step.Action);
        Assert.Contains("files.example", step.Detail);
    }

    [Fact]
    public async Task A_settled_pack_syncs_without_fetching_anything()
    {
        var (syncer, stub) = Make();
        stub.Files[Private] = Zip("anegotweaks", "1.0.0");
        var pack = Pack(FromUrl());

        await syncer.SyncAsync(pack, ModsDir, LockPath);
        stub.Requested.Clear();

        var report = await syncer.SyncAsync(pack, ModsDir, LockPath);

        Assert.Equal(SyncAction.Unchanged, Assert.Single(report.Steps).Action);
        Assert.Empty(stub.Requested);
    }

    [Fact]
    public async Task A_file_that_changed_at_its_address_is_refused_until_updated()
    {
        var (syncer, stub) = Make();
        stub.Files[Private] = Zip("anegotweaks", "1.0.0");
        var pack = Pack(FromUrl());

        var first = await syncer.SyncAsync(pack, ModsDir, LockPath);
        var original = first.Lock.Mods.Single().Sha256;

        // The author rebuilt. The pack on disk was deleted to force a fetch, the way a
        // fresh machine or a cleared Mods folder would.
        stub.Files[Private] = Zip("anegotweaks", "1.1.0");
        File.Delete(Path.Combine(ModsDir, "anegotweaks.zip"));

        var refused = await syncer.SyncAsync(pack, ModsDir, LockPath);

        Assert.True(refused.Failed);
        var step = Assert.Single(refused.Steps);
        Assert.Contains("no longer the one the lock describes", step.Detail);
        Assert.Contains("update anegotweaks", step.Detail);
        Assert.False(File.Exists(Path.Combine(ModsDir, "anegotweaks.zip")));

        // The refusal holds: the lock still says what was installed, so the next sync
        // refuses again rather than finding nothing to compare against.
        Assert.Equal(original, Assert.Single(refused.Lock.Mods).Sha256);
        Assert.True((await syncer.SyncAsync(pack, ModsDir, LockPath)).Failed);

        // Asked for, it moves — and the lock moves with it.
        var updated = await syncer.SyncAsync(pack, ModsDir, LockPath,
            allowUpdates: new HashSet<string> { "anegotweaks" });

        Assert.False(updated.Failed);
        var locked = Assert.Single(updated.Lock.Mods);
        Assert.Equal("1.1.0", locked.Version);
        Assert.NotEqual(original, locked.Sha256);
        Assert.Equal(SyncAction.Updated, Assert.Single(updated.Steps).Action);
    }

    [Fact]
    public async Task An_update_reports_the_move_when_the_old_file_is_still_installed()
    {
        var (syncer, stub) = Make();
        stub.Files[Private] = Zip("anegotweaks", "1.0.0");
        var pack = Pack(FromUrl());

        await syncer.SyncAsync(pack, ModsDir, LockPath);
        stub.Files[Private] = Zip("anegotweaks", "1.1.0");

        var report = await syncer.SyncAsync(pack, ModsDir, LockPath,
            allowUpdates: new HashSet<string> { "anegotweaks" });

        var step = Assert.Single(report.Steps);
        Assert.Equal(SyncAction.Updated, step.Action);
        Assert.Contains("1.0.0 -> 1.1.0", step.Detail);
    }

    [Fact]
    public async Task An_imported_lock_binds_the_fetch_to_the_authors_hash()
    {
        var (syncer, stub) = Make();
        stub.Files[Private] = Zip("anegotweaks", "1.0.0");
        var pack = Pack(FromUrl());

        // The author's lock, as it arrives: locations cleared, hash kept.
        var theirs = (await syncer.SyncAsync(pack, ModsDir, LockPath)).Lock;
        theirs.ClearResolvedLocations();
        Assert.True(theirs.Mods.Single().FromUrl);
        theirs.Save(LockPath);
        File.Delete(Path.Combine(ModsDir, "anegotweaks.zip"));

        // Same bytes at the address: reproduces, and the lock entry is whole again.
        var good = await syncer.SyncAsync(pack, ModsDir, LockPath);
        Assert.False(good.Failed);
        Assert.Equal(Private, good.Lock.Mods.Single().Url);
        Assert.Equal("anegotweaks.zip", good.Lock.Mods.Single().FileName);

        // Different bytes: the author's word about their file wins over the file.
        theirs.Save(LockPath);
        File.Delete(Path.Combine(ModsDir, "anegotweaks.zip"));
        stub.Files[Private] = Zip("anegotweaks", "1.0.0", extra: ",\"authors\":[\"someone else\"]");

        var bad = await syncer.SyncAsync(pack, ModsDir, LockPath);
        Assert.True(bad.Failed);
        Assert.Empty(Directory.EnumerateFiles(ModsDir));

        // And the lock still carries the author's word, so it keeps winning.
        Assert.Equal(theirs.Mods.Single().Sha256, Assert.Single(bad.Lock.Mods).Sha256);
    }

    [Fact]
    public async Task Moving_the_address_takes_the_new_file_without_an_update()
    {
        var (syncer, stub) = Make();
        stub.Files[Private] = Zip("anegotweaks", "1.0.0");
        const string moved = "https://files.example/anegotweaks_1.1.0.zip";
        stub.Files[moved] = Zip("anegotweaks", "1.1.0");

        await syncer.SyncAsync(Pack(FromUrl()), ModsDir, LockPath);

        var report = await syncer.SyncAsync(Pack(FromUrl(url: moved)), ModsDir, LockPath);

        Assert.False(report.Failed);
        var locked = Assert.Single(report.Lock.Mods);
        Assert.Equal("1.1.0", locked.Version);
        Assert.Equal("anegotweaks_1.1.0.zip", locked.FileName);

        // The old file was Cairn's and is gone; the new one is in its place.
        Assert.False(File.Exists(Path.Combine(ModsDir, "anegotweaks.zip")));
        Assert.True(File.Exists(Path.Combine(ModsDir, "anegotweaks_1.1.0.zip")));
        Assert.Contains(report.Steps, s => s.Action == SyncAction.Removed);
    }

    [Fact]
    public async Task Moving_a_mod_from_an_address_onto_ModDB_does_not_pin_it_to_the_private_version()
    {
        var (syncer, stub) = Make();
        stub.Files[Private] = Zip("olla", "0.9.0-private");

        await syncer.SyncAsync(Pack(FromUrl("olla")), ModsDir, LockPath);

        // Now asked for from ModDB, which has 1.0.0 and has never heard of 0.9.0-private.
        // A lock that bound its version would send the resolve looking for a release that
        // does not exist; one that bound its hash would refuse ModDB's file.
        var report = await syncer.SyncAsync(Pack(new PackMod { ModId = "olla" }), ModsDir, LockPath);

        Assert.False(report.Failed);
        var locked = Assert.Single(report.Lock.Mods);
        Assert.Equal("1.0.0", locked.Version);
        Assert.False(locked.FromUrl);
        Assert.False(File.Exists(Path.Combine(ModsDir, "olla.zip")));
    }

    [Fact]
    public async Task Moving_a_mod_from_ModDB_onto_an_address_is_not_held_to_ModDBs_hash()
    {
        var (syncer, stub) = Make();
        stub.Files[Private] = Zip("olla", "1.0.0", extra: ",\"authors\":[\"me\"]");

        await syncer.SyncAsync(Pack(new PackMod { ModId = "olla" }), ModsDir, LockPath);

        var report = await syncer.SyncAsync(Pack(FromUrl("olla")), ModsDir, LockPath);

        Assert.False(report.Failed);
        Assert.True(Assert.Single(report.Lock.Mods).FromUrl);
    }

    [Fact]
    public async Task Something_that_is_not_a_mod_is_refused_and_not_left_in_the_directory()
    {
        var (syncer, stub) = Make();
        stub.Files[Private] = Encoding.UTF8.GetBytes("<html>sign in to download</html>");

        var report = await syncer.SyncAsync(Pack(FromUrl()), ModsDir, LockPath);

        Assert.True(report.Failed);
        Assert.Contains("is not a mod", Assert.Single(report.Steps).Detail);
        Assert.Empty(Directory.EnumerateFiles(ModsDir));
        Assert.Empty(report.Lock.Mods);
    }

    [Fact]
    public async Task A_zip_declaring_a_different_modid_installs_with_a_warning()
    {
        var (syncer, stub) = Make();
        stub.Files[Private] = Zip("anegotweaksv2", "2.0.0");

        var report = await syncer.SyncAsync(Pack(FromUrl()), ModsDir, LockPath);

        Assert.False(report.Failed);
        Assert.Contains(report.Warnings, w => w.Detail.Contains("anegotweaksv2"));
    }

    [Fact]
    public async Task A_plain_http_address_is_refused_by_the_manifest_not_the_network()
    {
        var (syncer, stub) = Make();
        const string clear = "http://files.example/anegotweaks.zip";
        stub.Files[clear] = Zip("anegotweaks", "1.0.0");

        var report = await syncer.SyncAsync(Pack(FromUrl(url: clear)), ModsDir, LockPath);

        Assert.True(report.Failed);
        Assert.Contains("https", Assert.Single(report.Steps).Detail);
        Assert.Empty(stub.Requested);
    }

    [Fact]
    public async Task Dependencies_of_a_URL_mod_are_resolved_on_ModDB()
    {
        var (syncer, stub) = Make();
        stub.Files[Private] = Zip("anegotweaks", "1.0.0", extra: ",\"dependencies\":{\"olla\":\"1.0.0\",\"game\":\"1.22.0\"}");

        var report = await syncer.SyncAsync(Pack(FromUrl()), ModsDir, LockPath);

        Assert.False(report.Failed);
        Assert.Equal(1, stub.Lookups);
        var olla = report.Lock.Mods.Single(m => m.ModId == "olla");
        Assert.Equal(["anegotweaks"], olla.RequiredBy);
        Assert.False(olla.FromUrl);
    }

    [Fact]
    public async Task Checking_for_updates_fetches_the_file_and_reports_a_changed_one()
    {
        var (syncer, stub) = Make();
        stub.Files[Private] = Zip("anegotweaks", "1.0.0");
        var pack = Pack(FromUrl());

        await syncer.SyncAsync(pack, ModsDir, LockPath);

        Assert.Empty(await syncer.CheckUpdatesAsync(pack, LockPath));

        stub.Files[Private] = Zip("anegotweaks", "1.1.0");
        var moved = Assert.Single(await syncer.CheckUpdatesAsync(pack, LockPath));
        Assert.Equal("1.0.0", moved.From);
        Assert.Equal("1.1.0", moved.To);

        // Rebuilt without a bump: still an update, and said so rather than "1.0.0 -> 1.0.0".
        stub.Files[Private] = Zip("anegotweaks", "1.0.0", extra: ",\"authors\":[\"me\"]");
        var rebuilt = Assert.Single(await syncer.CheckUpdatesAsync(pack, LockPath));
        Assert.Contains("rebuilt", rebuilt.To);

        Assert.Equal(0, stub.Lookups);
    }

    [Fact]
    public async Task Checking_for_updates_says_nothing_about_a_host_it_cannot_reach()
    {
        var (syncer, stub) = Make();
        stub.Files[Private] = Zip("anegotweaks", "1.0.0");
        var pack = Pack(FromUrl());

        await syncer.SyncAsync(pack, ModsDir, LockPath);
        stub.Files.Remove(Private);

        Assert.Empty(await syncer.CheckUpdatesAsync(pack, LockPath));
    }

    // ---- ModDB's own download links ----

    [Fact]
    public async Task A_ModDB_link_to_an_unlisted_mod_becomes_an_address_and_records_the_file_id()
    {
        var (syncer, stub) = Make();
        stub.Files[OnModDb] = Zip("augur", "0.1.1");
        var http = new HttpClient(stub);

        var found = await ModUrl.InspectAsync(http, OnModDb, moddb: new ModDbClient(http));

        Assert.Equal(118768, found.FileId);
        Assert.Equal(ModDbListing.Unlisted, found.Listing);

        var entry = found.ToManifestEntry(OnModDb);
        Assert.Equal(OnModDb, entry.Url);
        Assert.Null(entry.Version);

        var report = await syncer.SyncAsync(Pack(entry), ModsDir, LockPath);

        Assert.False(report.Failed);
        var locked = Assert.Single(report.Lock.Mods);
        Assert.True(locked.FromUrl);
        Assert.Equal(118768, locked.FileId);
        Assert.Equal("augur_0.1.1.zip", locked.FileName);
    }

    [Fact]
    public async Task A_ModDB_link_to_a_listed_release_becomes_a_pinned_ModDB_entry()
    {
        var (_, stub) = Make();
        stub.Files[OnModDb] = Zip("augur", "0.1.1");
        stub.Listed.Add("augur");
        var http = new HttpClient(stub);

        var found = await ModUrl.InspectAsync(http, OnModDb, moddb: new ModDbClient(http));

        Assert.Equal(ModDbListing.Listed, found.Listing);
        Assert.Equal("0.1.1", found.ListedVersion);

        var entry = found.ToManifestEntry(OnModDb);
        Assert.Null(entry.Url);
        Assert.Equal("augur", entry.ModId);
        Assert.Equal("0.1.1", entry.Version);
    }

    [Fact]
    public async Task A_listed_mod_whose_file_is_not_a_release_stays_an_address()
    {
        var (_, stub) = Make();
        const string draft = "https://mods.vintagestory.at/download/999999/augur_0.2.0.zip";
        stub.Files[draft] = Zip("augur", "0.2.0");
        stub.Listed.Add("augur");
        var http = new HttpClient(stub);

        var found = await ModUrl.InspectAsync(http, draft, moddb: new ModDbClient(http));

        Assert.Equal(ModDbListing.Unlisted, found.Listing);
        Assert.Equal(draft, found.ToManifestEntry(draft).Url);
    }

    [Fact]
    public async Task Somebody_elses_host_is_never_asked_about_on_ModDB()
    {
        var (_, stub) = Make();
        stub.Files[Private] = Zip("anegotweaks", "1.0.0");
        var http = new HttpClient(stub);

        var found = await ModUrl.InspectAsync(http, Private, moddb: new ModDbClient(http));

        Assert.Equal(ModDbListing.NotModDb, found.Listing);
        Assert.Equal(0, found.FileId);
        Assert.Equal(0, stub.Lookups);
    }

    [Fact]
    public async Task Checking_for_updates_says_when_an_unlisted_mod_has_been_listed()
    {
        var (syncer, stub) = Make();
        stub.Files[OnModDb] = Zip("augur", "0.1.1");
        var pack = Pack(FromUrl("augur", OnModDb));

        await syncer.SyncAsync(pack, ModsDir, LockPath);

        // Still unlisted: the same bytes, and nothing to say.
        Assert.Empty(await syncer.CheckUpdatesAsync(pack, LockPath));

        stub.Listed.Add("augur");
        var listed = Assert.Single(await syncer.CheckUpdatesAsync(pack, LockPath));

        Assert.True(listed.NowListedOnModDb);
        Assert.Equal("0.1.1", listed.From);
        Assert.Contains("now listed on ModDB", listed.To);
        Assert.NotEqual(listed.From, listed.To);

        // Taking it is the front-end dropping the address; the sync then treats the
        // entry as any ModDB mod, and the old lock entry — a different kind — does not bind.
        pack.Mods.Single().Url = null;
        var report = await syncer.SyncAsync(pack, ModsDir, LockPath,
            allowUpdates: new HashSet<string> { "augur" });

        Assert.False(report.Failed);
        var locked = Assert.Single(report.Lock.Mods);
        Assert.False(locked.FromUrl);
        Assert.Equal(7, locked.ReleaseId);
        Assert.Equal("0.1.1", locked.Version);
    }
}
