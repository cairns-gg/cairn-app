using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Cairn.Core.ModDb;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// A mod ModDB stops listing while its files go on being served — xskillsfork, locked by a
/// moderator, and the xlibfork it depends on, locked with it.
///
/// Two ends to it. A copy that already has the files keeps running on them, dependencies
/// included, rather than refusing to start. And the author's copy, the one place a download
/// address for the mod is trusted, moves such mods onto that address when it publishes, so a
/// copy that has never had them can install them without asking ModDB anything.
/// </summary>
public class LockedModTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-locked-" + Guid.NewGuid().ToString("n")[..8]);

    public LockedModTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>What each mod requires, as its modinfo.json says.</summary>
    private static readonly Dictionary<string, string[]> World = new()
    {
        ["xskillsfork"] = ["xlibfork", "vsimgui"],
        ["xlibfork"] = [],
        ["vsimgui"] = [],
    };

    private static readonly string[] Ids = ["xskillsfork", "xlibfork", "vsimgui"];

    private static int FileIdOf(string modId) => Array.IndexOf(Ids, modId) + 100;

    private static string FileNameOf(string modId) => $"{modId}_1.0.0.zip";

    /// <summary>
    /// ModDB, with a switch for which mods it still lists. Downloads answer whether or not the
    /// mod is listed — which is the whole situation — at both the CDN address the API names and
    /// the site's own /download/ link.
    /// </summary>
    private sealed class Stub : HttpMessageHandler
    {
        public HashSet<string> Unlisted { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Unreachable { get; set; }
        public int Lookups { get; private set; }
        public int Downloads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var url = r.RequestUri!.ToString();

            if (url.Contains("/api/mod/"))
            {
                Lookups++;
                if (Unreachable) throw new HttpRequestException("no route to host");

                var id = url[(url.LastIndexOf('/') + 1)..];

                // What ModDB actually sends for a mod it will not show: HTTP 200, and the
                // status in the body.
                var body = Unlisted.Contains(id) || !World.ContainsKey(id)
                    ? """{"statuscode":"404"}"""
                    : $$"""
                      {"statuscode":"200","mod":{
                        "modid":1,"assetid":2,"name":"{{id}}","urlalias":"{{id}}","side":"both",
                        "releases":[
                          {"releaseid":{{FileIdOf(id)}},"fileid":{{FileIdOf(id)}},"modidstr":"{{id}}",
                           "modversion":"1.0.0","filename":"{{FileNameOf(id)}}",
                           "mainfile":"https://moddbcdn.vintagestory.at/{{FileNameOf(id)}}","tags":["1.22.5"]}
                        ]
                      } }
                      """;

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }

            Downloads++;
            var file = Uri.UnescapeDataString(url[(url.LastIndexOf('/') + 1)..]);
            var modId = file[..file.IndexOf('_')];

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Zip(modId)),
            });
        }
    }

    /// <summary>A mod zip carrying the modinfo.json its dependencies are read from.</summary>
    private static byte[] Zip(string modId)
    {
        var deps = string.Join(", ", World[modId].Select(d => $"\"{d}\": \"1.0.0\""));

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("modinfo.json");

            // Pinned, so one address serves one file: see PackSyncDependencyTests.Stub.Zip.
            entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var writer = new StreamWriter(entry.Open());
            writer.Write($$"""
                {"type":"code","modid":"{{modId}}","name":"{{modId}}","version":"1.0.0",
                 "side":"universal","dependencies":{ {{deps}} } }
                """);
        }

        return buffer.ToArray();
    }

    private static string ShaOf(string modId) => Convert.ToHexStringLower(SHA256.HashData(Zip(modId)));

    private static (PackSyncer Syncer, ModDbClient ModDb, Stub Handler) Make()
    {
        var handler = new Stub();
        var http = new HttpClient(handler);
        var moddb = new ModDbClient(http);
        return (new PackSyncer(moddb, http), moddb, handler);
    }

    private static PackManifest Pack() => new()
    {
        Id = "best",
        GameVersion = "1.22.5",
        Mods = [new PackMod { ModId = "xskillsfork" }],
    };

    private Task<SyncReport> Sync(PackStore store, PackSyncer syncer, string id = "best") =>
        syncer.SyncAsync(store.Load(id), store.ModsDir(id), store.LockPath(id));

    /// <summary>The author's pack, installed while everything was listed.</summary>
    private async Task<PackStore> Authored()
    {
        var store = new PackStore(Path.Combine(_root, "author"));
        store.Save(Pack());

        var (syncer, _, _) = Make();
        Assert.False((await Sync(store, syncer)).Failed);
        Assert.Equal(3, store.LoadLock("best")!.Mods.Count);
        return store;
    }

    /// <summary>
    /// The server's shape: the zips are on disk, and the lock came from somebody else, so it
    /// names versions and hashes and nothing about where the files are.
    /// </summary>
    private PackStore Followed(PackLock authors, bool withFiles)
    {
        var store = new PackStore(Path.Combine(_root, "follower"));
        store.Save(Pack());

        authors.ClearResolvedLocations();
        authors.Save(store.LockPath("best"));

        if (withFiles)
            foreach (var id in Ids)
                File.WriteAllBytes(Path.Combine(store.ModsDir("best"), $"{id} as it was named.zip"), Zip(id));

        return store;
    }

    [Fact]
    public async Task A_copy_that_has_the_files_keeps_running_on_them()
    {
        var author = await Authored();
        var follower = Followed(author.LoadLock("best")!, withFiles: true);

        var (syncer, _, handler) = Make();
        handler.Unlisted.UnionWith(["xskillsfork", "xlibfork"]);

        var report = await Sync(follower, syncer);

        // Warned, not Failed: this is what cairn-server and Play both refuse to start over.
        Assert.False(report.Failed, string.Join("\n", report.Steps.Select(s => $"{s.ModId}: {s.Detail}")));
        Assert.Contains(report.Steps, s => s is { Action: SyncAction.Warned, ModId: "xskillsfork" });
        Assert.Contains(report.Steps, s => s is { Action: SyncAction.Warned, ModId: "xlibfork" });

        // The library locked alongside the mod is the one that used to be swept: nothing
        // read the dependencies of a mod whose resolve had failed.
        var locked = follower.LoadLock("best")!.Mods;
        Assert.Equal(Ids.Order(), locked.Select(m => m.ModId).Order());
        Assert.All(Ids, id => Assert.True(File.Exists(
            Path.Combine(follower.ModsDir("best"), $"{id} as it was named.zip"))));

        // Named in the lock under the name the file has here, so the next sync neither
        // sweeps it nor has to go looking for it.
        Assert.Equal("xskillsfork as it was named.zip",
            locked.Single(m => m.ModId == "xskillsfork").FileName);

        var (next, _, still) = Make();
        still.Unlisted.UnionWith(["xskillsfork", "xlibfork"]);
        Assert.False((await Sync(follower, next)).Failed);
        Assert.Equal(3, follower.LoadLock("best")!.Mods.Count);
        Assert.Equal(0, still.Downloads);
    }

    /// <summary>
    /// A file that is not the one the lock names is not a copy of it, whatever it is called.
    /// </summary>
    [Fact]
    public async Task A_file_with_other_bytes_is_not_taken_for_the_mod()
    {
        var author = await Authored();
        var follower = Followed(author.LoadLock("best")!, withFiles: false);
        File.WriteAllBytes(Path.Combine(follower.ModsDir("best"), FileNameOf("xskillsfork")), Zip("vsimgui"));

        var (syncer, _, handler) = Make();
        handler.Unlisted.Add("xskillsfork");

        var report = await Sync(follower, syncer);

        Assert.Contains(report.Steps, s => s is { Action: SyncAction.Failed, ModId: "xskillsfork" });
    }

    /// <summary>
    /// Nothing on this machine can fix a copy that never had the mod, so the message says who
    /// can, rather than a 404 that reads as Cairn's own fault.
    /// </summary>
    [Fact]
    public async Task A_copy_with_nothing_to_fall_back_on_is_told_who_can_fix_it()
    {
        var author = await Authored();
        var follower = Followed(author.LoadLock("best")!, withFiles: false);

        var (syncer, _, handler) = Make();
        handler.Unlisted.UnionWith(["xskillsfork", "xlibfork"]);

        var report = await Sync(follower, syncer);

        var failed = Assert.Single(report.Steps, s => s.Action == SyncAction.Failed);
        Assert.Equal("xskillsfork", failed.ModId);
        Assert.Contains("publish it again", failed.Detail);
    }

    [Fact]
    public async Task Publishing_moves_an_unlisted_mod_and_its_dependency_onto_download_links()
    {
        var store = await Authored();

        var (_, moddb, handler) = Make();
        handler.Unlisted.UnionWith(["xskillsfork", "xlibfork"]);

        var manifest = store.Load("best");
        var moved = await UnlistedMods.ReaddressAsync(store, manifest, moddb);

        Assert.Equal(["xlibfork", "xskillsfork"], moved.Select(m => m.ModId).Order());
        Assert.True(moved.Single(m => m.ModId == "xlibfork").WasDependency);
        Assert.False(moved.Single(m => m.ModId == "xskillsfork").WasDependency);

        // Saved, and the same as what the caller holds.
        var saved = store.Load("best");
        Assert.Empty(saved.Validate());
        Assert.Equal(
            $"https://mods.vintagestory.at/download/{FileIdOf("xskillsfork")}/{FileNameOf("xskillsfork")}",
            saved.Mods.Single(m => m.ModId == "xskillsfork").Url);
        Assert.Equal(
            $"https://mods.vintagestory.at/download/{FileIdOf("xlibfork")}/{FileNameOf("xlibfork")}",
            saved.Mods.Single(m => m.ModId == "xlibfork").Url);
        Assert.Equal(saved.Mods.Count, manifest.Mods.Count);

        // The mod ModDB still lists is left on ModDB.
        Assert.DoesNotContain(saved.Mods, m => m.ModId == "vsimgui");

        var locked = store.LoadLock("best")!.Mods;
        Assert.True(locked.Single(m => m.ModId == "xskillsfork").FromUrl);
        Assert.Equal(ShaOf("xskillsfork"), locked.Single(m => m.ModId == "xskillsfork").Sha256);
        Assert.False(locked.Single(m => m.ModId == "vsimgui").FromUrl);

        // And the author's own next sync finds everything in place: nothing asked, nothing fetched.
        var (after, _, quiet) = Make();
        quiet.Unlisted.UnionWith(["xskillsfork", "xlibfork"]);
        var report = await Sync(store, after);

        Assert.False(report.Failed, string.Join("\n", report.Steps.Select(s => $"{s.ModId}: {s.Detail}")));
        Assert.All(report.Steps, s => Assert.Equal(SyncAction.Unchanged, s.Action));
        Assert.Equal(0, quiet.Lookups);
        Assert.Equal(0, quiet.Downloads);
    }

    /// <summary>The point of all of it: a copy that never had the mods, from the republished pack.</summary>
    [Fact]
    public async Task A_new_copy_of_the_republished_pack_installs_without_ModDB()
    {
        var author = await Authored();
        var (_, moddb, handler) = Make();
        handler.Unlisted.UnionWith(["xskillsfork", "xlibfork"]);
        await UnlistedMods.ReaddressAsync(author, author.Load("best"), moddb);

        var follower = new PackStore(Path.Combine(_root, "follower"));
        follower.Save(author.Load("best"));
        var theirs = author.LoadLock("best")!;
        theirs.ClearResolvedLocations();
        theirs.Save(follower.LockPath("best"));

        var (syncer, _, fresh) = Make();
        fresh.Unlisted.UnionWith(["xskillsfork", "xlibfork"]);
        var report = await Sync(follower, syncer);

        Assert.False(report.Failed, string.Join("\n", report.Steps.Select(s => $"{s.ModId}: {s.Detail}")));

        // Held to the author's bytes, not merely to whatever the address served.
        var locked = follower.LoadLock("best")!.Mods;
        Assert.All(Ids, id => Assert.Equal(ShaOf(id), locked.Single(m => m.ModId == id).Sha256));
    }

    [Fact]
    public async Task An_unreachable_ModDB_moves_nothing()
    {
        var store = await Authored();
        var before = File.ReadAllText(store.ManifestPath("best"));

        var (_, moddb, handler) = Make();
        handler.Unreachable = true;

        Assert.Empty(await UnlistedMods.ReaddressAsync(store, store.Load("best"), moddb));
        Assert.Equal(before, File.ReadAllText(store.ManifestPath("best")));
    }

    [Fact]
    public async Task A_plan_names_what_publishing_moved()
    {
        var store = await Authored();
        var (_, moddb, handler) = Make();
        handler.Unlisted.Add("xskillsfork");

        var manifest = store.Load("best");
        var moved = await UnlistedMods.ReaddressAsync(store, manifest, moddb);
        var plan = await PublishPlan.PrepareAsync(manifest, store.LoadLock("best"), moddb, readdressed: moved);

        Assert.True(plan.CanPublish);
        Assert.False(plan.AnythingUnresolvable);
        Assert.True(plan.AnythingReaddressed);
        Assert.Contains("xskillsfork", plan.ReaddressedWarning());
    }
}
