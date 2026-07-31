using System.IO.Compression;
using System.Net;
using System.Text;
using Cairn.Core.ModDb;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// A mod can require another mod, and ModDB does not say so — the declaration lives in
/// modinfo.json inside the zip. So the set a pack installs is not known until things have
/// been downloaded, and sync has to keep going until it stops finding new ones.
///
/// Getting this wrong is what makes the game disable a mod on startup for a missing
/// dependency the user was never told about.
/// </summary>
public class PackSyncDependencyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-deps-" + Guid.NewGuid().ToString("n")[..8]);

    private string ModsDir => Path.Combine(_root, "Mods");
    private string LockPath => Path.Combine(_root, "pack.lock.json");

    public PackSyncDependencyTests() => Directory.CreateDirectory(ModsDir);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// Serves a small world of mods. Each download is a real zip carrying a real
    /// modinfo.json, because reading that file is the thing under test.
    /// </summary>
    private sealed class Stub(Dictionary<string, string[]> world, string newest = "1.0.0")
        : HttpMessageHandler
    {
        public List<string> Looked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var url = r.RequestUri!.ToString();

            if (url.Contains("/api/mod/"))
            {
                var id = url[(url.LastIndexOf('/') + 1)..];
                Looked.Add(id);

                if (!world.ContainsKey(id))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

                var body = $$"""
                {"statuscode":"200","mod":{
                  "modid":1,"assetid":2,"name":"{{id}}","urlalias":"{{id}}","side":"client",
                  "releases":[
                    {"releaseid":1,"fileid":1,"modidstr":"{{id}}","modversion":"{{newest}}",
                     "filename":"{{id}}_{{newest}}.zip",
                     "mainfile":"https://moddbcdn.vintagestory.at/{{id}}_{{newest}}.zip",
                     "tags":["1.22.5"]},
                    {"releaseid":2,"fileid":2,"modidstr":"{{id}}","modversion":"1.0.0",
                     "filename":"{{id}}_1.0.0.zip",
                     "mainfile":"https://moddbcdn.vintagestory.at/{{id}}_1.0.0.zip",
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

            var file = url[(url.LastIndexOf('/') + 1)..];
            var modId = file[..file.IndexOf('_')];
            var version = file[(file.IndexOf('_') + 1)..^".zip".Length];

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(
                    Zip(modId, version, world.GetValueOrDefault(modId, []))),
            });
        }

        /// <summary>A mod zip: nothing but the modinfo.json the game reads.</summary>
        private static byte[] Zip(string modId, string version, string[] dependencies)
        {
            var deps = string.Join(", ", dependencies.Select(d => $"\"{d}\": \"1.0.0\""));

            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = zip.CreateEntry("modinfo.json");
                using var writer = new StreamWriter(entry.Open());
                writer.Write($$"""
                {"type":"content","modid":"{{modId}}","name":"{{modId}}","version":"{{version}}",
                 "dependencies":{ {{deps}} } }
                """);
            }

            return buffer.ToArray();
        }
    }

    private (PackSyncer Syncer, Stub Handler) Make(
        Dictionary<string, string[]> world, string newest = "1.0.0")
    {
        var handler = new Stub(world, newest);
        var http = new HttpClient(handler);
        return (new PackSyncer(new ModDbClient(http), http), handler);
    }

    private static PackManifest Pack(params string[] mods) => new()
    {
        Id = "anego",
        GameVersion = "1.22.5",
        Mods = [.. mods.Select(m => new PackMod { ModId = m })],
    };

    [Fact]
    public async Task A_dependency_declared_in_modinfo_is_installed_too()
    {
        var (syncer, _) = Make(new()
        {
            ["carryon"] = ["carryonlib"],
            ["carryonlib"] = [],
        });

        var report = await syncer.SyncAsync(Pack("carryon"), ModsDir, LockPath);

        Assert.False(report.Failed);
        Assert.Equal(
            new[] { "carryon", "carryonlib" },
            report.Lock.Mods.Select(m => m.ModId).Order().ToArray());

        // Recorded, because a mod nobody asked for needs to be able to explain itself.
        var lib = report.Lock.Mods.Single(m => m.ModId == "carryonlib");
        Assert.Equal(["carryon"], lib.RequiredBy);
    }

    [Fact]
    public async Task Dependencies_of_dependencies_are_followed()
    {
        var (syncer, _) = Make(new()
        {
            ["expandedfoods"] = ["aculinaryartillery"],
            ["aculinaryartillery"] = ["somelib"],
            ["somelib"] = [],
        });

        var report = await syncer.SyncAsync(Pack("expandedfoods"), ModsDir, LockPath);

        Assert.False(report.Failed);
        Assert.Equal(3, report.Lock.Mods.Count);
        Assert.Contains(report.Lock.Mods, m => m.ModId == "somelib");
    }

    [Fact]
    public async Task The_games_own_domains_are_not_looked_up()
    {
        var (syncer, handler) = Make(new()
        {
            ["betterruins"] = ["game", "survival", "creative"],
        });

        var report = await syncer.SyncAsync(Pack("betterruins"), ModsDir, LockPath);

        // These ship with the game. Asking ModDB for them finds nothing, and a pack that
        // failed because a mod depends on "game" would be absurd.
        Assert.False(report.Failed);
        Assert.Single(report.Lock.Mods);
        Assert.Equal(["betterruins"], handler.Looked);
    }

    [Fact]
    public async Task Two_mods_that_require_each_other_terminate()
    {
        var (syncer, _) = Make(new()
        {
            ["alpha"] = ["beta"],
            ["beta"] = ["alpha"],
        });

        var report = await syncer.SyncAsync(Pack("alpha"), ModsDir, LockPath);

        Assert.False(report.Failed);
        Assert.Equal(2, report.Lock.Mods.Count);
    }

    [Fact]
    public async Task A_mod_the_pack_names_itself_is_not_marked_as_required_by()
    {
        var (syncer, _) = Make(new()
        {
            ["carryon"] = ["carryonlib"],
            ["carryonlib"] = [],
        });

        var report = await syncer.SyncAsync(Pack("carryon", "carryonlib"), ModsDir, LockPath);

        // It is in the pack because the pack asked for it, whatever else also wants it.
        Assert.Null(report.Lock.Mods.Single(m => m.ModId == "carryonlib").RequiredBy);
    }

    [Fact]
    public async Task A_dependency_is_removed_once_nothing_requires_it()
    {
        var world = new Dictionary<string, string[]>
        {
            ["carryon"] = ["carryonlib"],
            ["carryonlib"] = [],
        };

        var (first, _) = Make(world);
        await first.SyncAsync(Pack("carryon"), ModsDir, LockPath);
        Assert.Equal(2, Directory.GetFiles(ModsDir, "*.zip").Length);

        // carryon is dropped from the pack, so nothing reaches carryonlib any more.
        var (second, _) = Make(world);
        var report = await second.SyncAsync(Pack(), ModsDir, LockPath);

        Assert.Empty(report.Lock.Mods);
        Assert.Empty(Directory.GetFiles(ModsDir, "*.zip"));
    }

    [Fact]
    public async Task A_dependency_moves_when_the_mod_that_requires_it_is_updated()
    {
        var world = new Dictionary<string, string[]>
        {
            ["carryon"] = ["carryonlib"],
            ["carryonlib"] = [],
        };

        var (first, _) = Make(world);
        await first.SyncAsync(Pack("carryon"), ModsDir, LockPath);

        // Both ship a new release, and the user asks to update carryon.
        var (second, _) = Make(world, newest: "1.1.0");
        var report = await second.SyncAsync(
            Pack("carryon"), ModsDir, LockPath, allowUpdates: new HashSet<string> { "carryon" });

        // Updating carryon while carryonlib stays at 1.0.0 is how the game ends up
        // disabling the mod: the newer mod is usually why the newer library is needed.
        Assert.Equal("1.1.0", report.Lock.Mods.Single(m => m.ModId == "carryon").Version);
        Assert.Equal("1.1.0", report.Lock.Mods.Single(m => m.ModId == "carryonlib").Version);
    }

    [Fact]
    public async Task A_dependency_stays_put_when_nothing_asks_its_dependent_to_move()
    {
        var world = new Dictionary<string, string[]>
        {
            ["carryon"] = ["carryonlib"],
            ["carryonlib"] = [],
        };

        var (first, _) = Make(world);
        await first.SyncAsync(Pack("carryon"), ModsDir, LockPath);

        // A plain sync — which is what every Play does — must not move anything.
        var (second, _) = Make(world, newest: "1.1.0");
        var report = await second.SyncAsync(Pack("carryon"), ModsDir, LockPath);

        Assert.All(report.Lock.Mods, m => Assert.Equal("1.0.0", m.Version));
    }

    [Fact]
    public async Task An_update_carries_down_a_chain_of_dependencies()
    {
        var world = new Dictionary<string, string[]>
        {
            ["expandedfoods"] = ["aculinaryartillery"],
            ["aculinaryartillery"] = ["somelib"],
            ["somelib"] = [],
        };

        var (first, _) = Make(world);
        await first.SyncAsync(Pack("expandedfoods"), ModsDir, LockPath);

        var (second, _) = Make(world, newest: "1.1.0");
        var report = await second.SyncAsync(
            Pack("expandedfoods"), ModsDir, LockPath,
            allowUpdates: new HashSet<string> { "expandedfoods" });

        Assert.All(report.Lock.Mods, m => Assert.Equal("1.1.0", m.Version));
    }

    [Fact]
    public async Task A_dependency_ModDB_does_not_have_says_who_wanted_it()
    {
        var (syncer, _) = Make(new()
        {
            ["carryon"] = ["carryonlib"],
            // carryonlib is deliberately absent from ModDB.
        });

        var report = await syncer.SyncAsync(Pack("carryon"), ModsDir, LockPath);

        Assert.True(report.Failed);

        // "no release marked for game 1.22.5" is a puzzle for a mod the user never added.
        var failure = report.Steps.Single(s => s.Action == SyncAction.Failed);
        Assert.Equal("carryonlib", failure.ModId);
        Assert.Contains("required by carryon", failure.Detail);
    }
}
