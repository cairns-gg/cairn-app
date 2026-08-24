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
    /// <param name="unreadable">
    /// Mods served as a real zip carrying a modinfo.json that will not parse — the case
    /// that matters, because the game's own parser is more forgiving than ours and such a
    /// mod loads in-game while its dependencies are invisible here.
    /// </param>
    /// <param name="notZip">Mods served as bytes that are not an archive at all.</param>
    /// <param name="stale">
    /// Mods whose releases are marked for the previous minor and nothing since — a mod that
    /// has not been rebuilt for the version the pack targets.
    /// </param>
    private sealed class Stub(Dictionary<string, string[]> world, string newest = "1.0.0",
        HashSet<string>? unreadable = null, HashSet<string>? notZip = null,
        HashSet<string>? stale = null)
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

                var marked = stale?.Contains(id) == true ? "\"1.21.6\"" : "\"1.22.5\"";

                var body = $$"""
                {"statuscode":"200","mod":{
                  "modid":1,"assetid":2,"name":"{{id}}","urlalias":"{{id}}","side":"client",
                  "releases":[
                    {"releaseid":1,"fileid":1,"modidstr":"{{id}}","modversion":"{{newest}}",
                     "filename":"{{id}}_{{newest}}.zip",
                     "mainfile":"https://moddbcdn.vintagestory.at/{{id}}_{{newest}}.zip",
                     "tags":[{{marked}}]},
                    {"releaseid":2,"fileid":2,"modidstr":"{{id}}","modversion":"1.0.0",
                     "filename":"{{id}}_1.0.0.zip",
                     "mainfile":"https://moddbcdn.vintagestory.at/{{id}}_1.0.0.zip",
                     "tags":[{{marked}}]}
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

            byte[] bytes;

            if (notZip?.Contains(modId) == true)
                bytes = Encoding.UTF8.GetBytes("<html>not an archive</html>");
            else if (unreadable?.Contains(modId) == true)
                bytes = Zip(modId, version, [], modInfo: "{ 'modid': not json at all, }");
            else
                bytes = Zip(modId, version, world.GetValueOrDefault(modId, []));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            });
        }

        /// <summary>A mod zip: nothing but the modinfo.json the game reads.</summary>
        private static byte[] Zip(string modId, string version, string[] dependencies,
            string? modInfo = null)
        {
            var deps = string.Join(", ", dependencies.Select(d => $"\"{d}\": \"1.0.0\""));

            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = zip.CreateEntry("modinfo.json");
                // Pinned, or the same URL does not serve the same bytes twice. A zip
                // entry stamps the current time, and DOS timestamps have two-second
                // granularity — so two builds of identical content are byte-identical
                // within a bucket and differ across one. A sync that re-downloads a locked
                // release then hashes it, finds a checksum that does not match the lock,
                // and correctly refuses the mod. That is the syncer being right about a
                // stub being wrong: a real CDN serves one file for one URL, and this has to
                // as well. It failed roughly once in a hundred runs, which is exactly often
                // enough to be dismissed as CI being CI.
                entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(modInfo ?? $$"""
                {"type":"content","modid":"{{modId}}","name":"{{modId}}","version":"{{version}}",
                 "dependencies":{ {{deps}} } }
                """);
            }

            return buffer.ToArray();
        }
    }

    private (PackSyncer Syncer, Stub Handler) Make(
        Dictionary<string, string[]> world, string newest = "1.0.0",
        HashSet<string>? unreadable = null, HashSet<string>? notZip = null,
        HashSet<string>? stale = null)
    {
        var handler = new Stub(world, newest, unreadable, notZip, stale);
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

    /// <summary>
    /// The silence that used to be here was the bug. Reading modinfo.json exists to stop
    /// the game disabling a mod for a dependency nobody was told about — so a modinfo.json
    /// this cannot read has to say so, or it reproduces exactly that failure while looking
    /// like a clean sync.
    /// </summary>
    [Fact]
    public async Task A_modinfo_that_will_not_parse_is_warned_about_rather_than_swallowed()
    {
        var (syncer, _) = Make(
            new() { ["carryon"] = ["carryonlib"], ["carryonlib"] = [] },
            unreadable: ["carryon"]);

        var report = await syncer.SyncAsync(Pack("carryon"), ModsDir, LockPath);

        var warning = report.Warnings.Single();
        Assert.Equal("carryon", warning.ModId);
        Assert.Contains("modinfo.json could not be read", warning.Detail);

        // Isolation is the other half. The mod installed, the lock is written, and the run
        // did not fail — one mod with an odd file must not cost the user their pack.
        Assert.False(report.Failed);
        Assert.Equal(["carryon"], report.Lock.Mods.Select(m => m.ModId).ToArray());
    }

    [Fact]
    public async Task A_download_that_is_not_an_archive_is_warned_about_too()
    {
        var (syncer, _) = Make(new() { ["carryon"] = [] }, notZip: ["carryon"]);

        var report = await syncer.SyncAsync(Pack("carryon"), ModsDir, LockPath);

        Assert.Contains("zip could not be opened", report.Warnings.Single().Detail);
        Assert.False(report.Failed);
    }

    [Fact]
    public async Task A_mod_that_declares_no_dependencies_is_not_warned_about()
    {
        // The whole point of separating "read nothing" from "there is nothing": a warning
        // on every ordinary mod would be noise nobody reads, which is how a real one gets
        // missed.
        var (syncer, _) = Make(new() { ["betterruins"] = [] });

        var report = await syncer.SyncAsync(Pack("betterruins"), ModsDir, LockPath);

        Assert.Empty(report.Warnings);
        Assert.False(report.Failed);
    }

    /// <summary>
    /// A mod entry that cannot be used fails on its own, and leaves the rest of the pack
    /// alone.
    ///
    /// It used to throw. A ModDB page with no mod id — a download listing, or Optimum,
    /// which is a modified client rather than a mod — could be added from the launcher's
    /// search, wrote an empty modid into the manifest, and from then on every sync of that
    /// pack died on "Pack manifest is invalid". One click, and the only way out was editing
    /// pack.json by hand.
    /// </summary>
    [Fact]
    public async Task An_entry_with_no_modid_fails_by_itself_and_the_pack_still_syncs()
    {
        var (syncer, _) = Make(new() { ["carryon"] = [] });

        var manifest = Pack("carryon");
        manifest.Mods.Add(new PackMod { ModId = "" });

        var report = await syncer.SyncAsync(manifest, ModsDir, LockPath);

        var failure = report.Steps.Single(s => s.Action == SyncAction.Failed);
        Assert.Equal("(no modid)", failure.ModId);
        Assert.Contains("no modid", failure.Detail);

        // The rest of the pack installed, and the lock was written.
        Assert.Equal(["carryon"], report.Lock.Mods.Select(m => m.ModId).ToArray());
        Assert.True(File.Exists(LockPath));
    }

    [Fact]
    public async Task A_pack_level_problem_still_stops_everything()
    {
        // The distinction being drawn: an unusable game version means nothing can be
        // installed at all, so it is not something to report per mod and carry on from.
        var (syncer, _) = Make(new() { ["carryon"] = [] });

        var manifest = Pack("carryon");
        manifest.GameVersion = ">=1.22.5";

        await Assert.ThrowsAsync<InvalidDataException>(
            () => syncer.SyncAsync(manifest, ModsDir, LockPath));
    }

    [Fact]
    public async Task A_mod_listed_twice_fails_once_rather_than_taking_the_pack_with_it()
    {
        var (syncer, _) = Make(new() { ["carryon"] = [] });

        var manifest = Pack("carryon");
        manifest.Mods.Add(new PackMod { ModId = "carryon" });

        var report = await syncer.SyncAsync(manifest, ModsDir, LockPath);

        Assert.Contains("more than once", report.Steps.Single(s => s.Action == SyncAction.Failed).Detail);
        Assert.Equal(["carryon"], report.Lock.Mods.Select(m => m.ModId).ToArray());
    }

    /// <summary>
    /// The Floral Zones case, which is what this rule was written for. A bridge mod marked
    /// for 1.22 requires region mods last marked for 1.21 — the mismatch being the entire
    /// purpose of a bridge — and Cairn refused every region, installed the bridge, and left
    /// a pack the game would disable on startup. Nothing on a dependency row could accept
    /// anything, so the only way out was adding seven mods by hand.
    /// </summary>
    [Fact]
    public async Task A_dependency_marked_for_nothing_like_this_game_version_is_installed_anyway()
    {
        var (syncer, _) = Make(
            new()
            {
                ["floralzones122bridge"] = ["floralzonesmediterraneanregion"],
                ["floralzonesmediterraneanregion"] = [],
            },
            stale: ["floralzonesmediterraneanregion"]);

        var report = await syncer.SyncAsync(Pack("floralzones122bridge"), ModsDir, LockPath);

        Assert.False(report.Failed);
        Assert.Equal(2, report.Lock.Mods.Count);

        // Recorded as what it is. The lock is "exactly what was installed", and an entry
        // marked for another version is how a follower's copy knows to say so too.
        var region = report.Lock.Mods.Single(m => m.ModId == "floralzonesmediterraneanregion");
        Assert.Equal(["1.21.6"], region.MarkedFor);
    }

    [Fact]
    public async Task And_says_which_mod_asked_for_it()
    {
        var (syncer, _) = Make(
            new()
            {
                ["floralzones122bridge"] = ["floralzonesmediterraneanregion"],
                ["floralzonesmediterraneanregion"] = [],
            },
            stale: ["floralzonesmediterraneanregion"]);

        var report = await syncer.SyncAsync(Pack("floralzones122bridge"), ModsDir, LockPath);

        // "the pack accepts it" is untrue of a mod nobody added, and leaves the reader with
        // no way to tell which of their mods is responsible for it.
        var warning = Assert.Single(report.Warnings, w => w.ModId == "floralzonesmediterraneanregion");
        Assert.Contains("1.21.6", warning.Detail);
        Assert.Contains("floralzones122bridge requires it", warning.Detail);
    }

    /// <summary>
    /// The edge of the rule. A dependency is vouched for by the mod that requires it; a mod
    /// the pack names is vouched for by whoever named it, and nobody has — so it fails as it
    /// always did, with the manifest entry and the launcher's Add anyway both able to say
    /// otherwise. See <see cref="UnmarkedModTests"/>.
    /// </summary>
    [Fact]
    public async Task A_mod_the_pack_names_itself_is_still_refused()
    {
        var (syncer, _) = Make(
            new() { ["oreveintracers"] = [] },
            stale: ["oreveintracers"]);

        var report = await syncer.SyncAsync(Pack("oreveintracers"), ModsDir, LockPath);

        Assert.True(report.Failed);
        Assert.Empty(report.Lock.Mods);
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
