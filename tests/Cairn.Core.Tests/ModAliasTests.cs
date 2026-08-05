using System.IO.Compression;
using System.Net;
using System.Text;
using Cairn.Core.ModDb;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// ModDB answers to more than one id per mod. Asking for "hpspinningwheel" returns
/// Immersive Fibercraft, whose every release declares "spinningwheel" — and asking for
/// "spinningwheel" returns exactly the same mod.
///
/// Recording the declared id in the lock broke the one relationship the lock exists to
/// support. The manifest said hpspinningwheel and the lock said spinningwheel, so nothing
/// could match them: sharing refused a pack that had just synced, and — worse, because
/// nobody would report it — every sync re-downloaded the mod, on every launch, forever.
/// </summary>
public class ModAliasTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-alias-" + Guid.NewGuid().ToString("n")[..8]);

    private string ModsDir => Path.Combine(_root, "Mods");
    private string LockPath => Path.Combine(_root, "pack.lock.json");

    public ModAliasTests() => Directory.CreateDirectory(ModsDir);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private const string Alias = "hpspinningwheel";
    private const string Declared = "spinningwheel";

    /// <summary>
    /// Answers to any id with the same mod, whose releases declare <see cref="Declared"/>.
    /// That is what the live API does for this mod.
    /// </summary>
    private sealed class Stub(string[]? dependsOn = null) : HttpMessageHandler
    {
        public int Downloads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var url = r.RequestUri!.ToString();

            if (url.Contains("/api/mod/"))
            {
                var id = url[(url.LastIndexOf('/') + 1)..];

                // "othermod" is a separate mod that requires the declared id.
                var declares = id == "othermod" ? "othermod" : Declared;
                var version = id == "othermod" ? "1.0.0" : "1.2.12";

                var body = $$$"""
                {"statuscode":"200","mod":{
                  "modid":5814,"assetid":1,"name":"Immersive Fibercraft","side":"both",
                  "releases":[{"releaseid":1,"fileid":1,"modidstr":"{{{declares}}}",
                    "modversion":"{{{version}}}","filename":"{{{declares}}}_{{{version}}}.zip",
                    "mainfile":"https://moddbcdn.vintagestory.at/{{{declares}}}_{{{version}}}.zip",
                    "tags":["1.22.3"]}]}}
                """;

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }

            Downloads++;

            var file = url[(url.LastIndexOf('/') + 1)..];
            var modId = file[..file.IndexOf('_')];
            var deps = modId == "othermod" && dependsOn is not null
                ? string.Join(", ", dependsOn.Select(d => $"\"{d}\": \"1.0.0\""))
                : "";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Zip(modId, deps)),
            });
        }

        private static byte[] Zip(string modId, string deps)
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = zip.CreateEntry("modinfo.json");
                using var writer = new StreamWriter(entry.Open());
                writer.Write($$"""
                {"type":"content","modid":"{{modId}}","dependencies":{ {{deps}} } }
                """);
            }

            return buffer.ToArray();
        }
    }

    private static PackManifest Pack(params string[] mods) => new()
    {
        Id = "spin",
        GameVersion = "1.22.3",
        Mods = [.. mods.Select(m => new PackMod { ModId = m })],
    };

    [Fact]
    public async Task Resolving_by_an_alias_keeps_the_id_that_was_asked_for()
    {
        var client = new ModDbClient(new HttpClient(new Stub()));

        var release = await client.ResolveAsync(Alias, "1.22.3");

        Assert.NotNull(release);
        Assert.Equal(Alias, release.ModId);

        // The declared id is kept, but only so the same mod can be recognised arriving
        // again as somebody else's dependency. Nothing is keyed on it.
        Assert.Equal(Declared, release.DeclaredModId);
    }

    [Fact]
    public async Task A_pack_naming_an_alias_locks_under_that_alias()
    {
        var http = new HttpClient(new Stub());
        var report = await new PackSyncer(new ModDbClient(http), http)
            .SyncAsync(Pack(Alias), ModsDir, LockPath);

        // The lock has to be comparable with the manifest, or nothing that reads both can
        // work: not sharing, not updates, not the next sync.
        Assert.Equal([Alias], report.Lock.Mods.Select(m => m.ModId).ToArray());
        Assert.False(report.Failed);
    }

    [Fact]
    public async Task A_second_sync_of_an_aliased_mod_downloads_nothing()
    {
        var handler = new Stub();
        var http = new HttpClient(handler);
        var syncer = new PackSyncer(new ModDbClient(http), http);

        await syncer.SyncAsync(Pack(Alias), ModsDir, LockPath);
        Assert.Equal(1, handler.Downloads);

        var again = await syncer.SyncAsync(Pack(Alias), ModsDir, LockPath);

        // The symptom nobody would have reported: the lock was keyed by an id the lookup
        // never used, so every launch fetched the mod again.
        Assert.Equal(1, handler.Downloads);
        Assert.All(again.Steps, s => Assert.Equal(SyncAction.Unchanged, s.Action));
    }

    [Fact]
    public async Task A_pack_whose_mod_came_from_an_alias_can_be_published()
    {
        var http = new HttpClient(new Stub());
        var manifest = Pack(Alias);

        var report = await new PackSyncer(new ModDbClient(http), http)
            .SyncAsync(manifest, ModsDir, LockPath);

        var plan = await PublishPlan.PrepareAsync(
            manifest, report.Lock, syncFailures: report.Steps);

        // The reported bug: "1 mod is not installed. Sync the pack first", on a pack that
        // had just been synced successfully in front of the person reading it.
        Assert.True(plan.CanPublish);
        Assert.Null(plan.LockProblem);
    }

    [Fact]
    public async Task A_dependency_naming_the_declared_id_does_not_install_a_second_copy()
    {
        // The pack asks for the alias; another mod requires the id the releases declare.
        // They are one mod, and installing it twice would put two lock entries on one file.
        var http = new HttpClient(new Stub([Declared]));

        var report = await new PackSyncer(new ModDbClient(http), http)
            .SyncAsync(Pack(Alias, "othermod"), ModsDir, LockPath);

        Assert.Equal([Alias, "othermod"],
            report.Lock.Mods.Select(m => m.ModId).Order().ToArray());

        Assert.Single(report.Lock.Mods.Select(m => m.FileName),
            f => f.StartsWith(Declared, StringComparison.Ordinal));
    }
}
