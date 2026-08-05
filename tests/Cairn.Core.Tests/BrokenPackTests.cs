using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Cairn.Core;
using Cairn.Core.ModDb;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// One pack broken in every way at once, carried the whole way through: a real sync over
/// real archives, and then the diagnostics report somebody would paste into an issue.
///
/// The pieces are each covered elsewhere — the warnings in PackSyncDependencyTests, the
/// report in DiagnosticsTests, the refusal in PublishPlanTests — all with inputs handed
/// straight to the thing under test. What none of them covers is the composition, which is
/// the only part a user ever sees: whether a pack with four different problems still
/// syncs, still locks, still describes itself, and still says which mod is missing.
///
/// A hand-built copy of this pack lives in the dev home for poking at by eye. This is the
/// same pack, so the one that runs on every commit and the one somebody clicks through
/// cannot drift apart.
/// </summary>
public class BrokenPackTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-broken-pack-" + Guid.NewGuid().ToString("n")[..8]);

    private string ModsDir => Path.Combine(_root, "Mods");
    private string LockPath => Path.Combine(_root, "pack.lock.json");

    public BrokenPackTests() => Directory.CreateDirectory(ModsDir);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// Single-quoted keys and an unquoted value: JSON the game's own parser accepts and
    /// System.Text.Json does not. The archive is perfectly good, which is the entire point
    /// — a corrupt zip fails somewhere the game explains, and this does not.
    /// </summary>
    private const string GameAcceptsWeDoNot =
        "{ 'modid': badjson, 'name': 'Bad JSON', dependencies: { 'somelib': '1.0.0' } }";

    /// <summary>An HTML error page saved under a .zip name, as a bad mirror produces.</summary>
    private static readonly byte[] NotAnArchive =
        Encoding.UTF8.GetBytes("<!DOCTYPE html>\n<html><body>404 Not Found</body></html>\n");

    private sealed class Stub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var url = r.RequestUri!.ToString();

            if (url.Contains("/api/mod/"))
            {
                var id = url[(url.LastIndexOf('/') + 1)..];

                if (id != "goodmod")
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

                var body = """
                {"statuscode":"200","mod":{
                  "modid":1,"assetid":2,"name":"Good Mod","urlalias":"goodmod","side":"both",
                  "releases":[{"releaseid":1,"fileid":1,"modidstr":"goodmod","modversion":"2.0.0",
                    "filename":"goodmod_2.0.0.zip",
                    "mainfile":"https://moddbcdn.vintagestory.at/goodmod_2.0.0.zip",
                    "tags":["1.22.3"]}]}}
                """;

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Zip("""{"modid":"goodmod","version":"2.0.0"}""")),
            });
        }
    }

    private static byte[] Zip(string modInfo)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("modinfo.json");
            using var writer = new StreamWriter(entry.Open());
            writer.Write(modInfo);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Plants the two unreadable mods as already installed, with genuine checksums so sync
    /// keeps them rather than re-fetching or sweeping them away. Without the lock entries
    /// the stray-zip sweep would delete them and there would be nothing left to fail on.
    /// </summary>
    private void PlantBroken()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["badjson_1.0.0.zip"] = Zip(GameAcceptsWeDoNot),
            ["notazip_1.0.0.zip"] = NotAnArchive,
        };

        var locked = new PackLock { GameVersion = "1.22.3" };

        foreach (var (name, bytes) in files)
        {
            File.WriteAllBytes(Path.Combine(ModsDir, name), bytes);

            locked.Mods.Add(new LockedMod
            {
                ModId = name[..name.IndexOf('_')],
                Version = "1.0.0",
                FileName = name,
                Url = $"https://moddbcdn.vintagestory.at/{name}",
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                Side = "both",
            });
        }

        locked.Save(LockPath);
    }

    private static PackManifest Manifest() => new()
    {
        Id = "broken",
        Name = "Broken On Purpose",
        GameVersion = "1.22.3",
        Mods =
        [
            new PackMod { ModId = "goodmod" },       // installs cleanly
            new PackMod { ModId = "badjson" },       // installed, modinfo unreadable
            new PackMod { ModId = "notazip" },       // installed, archive unreadable
            new PackMod { ModId = "missingmod" },    // not on ModDB at all
        ],
    };

    [Fact]
    public async Task Four_broken_mods_still_leave_a_pack_that_synced_and_locked()
    {
        PlantBroken();

        var http = new HttpClient(new Stub());
        var report = await new PackSyncer(new ModDbClient(http), http)
            .SyncAsync(Manifest(), ModsDir, LockPath);

        // Exactly one mod could not be installed, and it is the one that does not exist.
        var failed = report.Steps.Where(s => s.Action == SyncAction.Failed).ToList();
        Assert.Equal(["missingmod"], failed.Select(s => s.ModId).ToArray());

        // Both unreadable archives are named, and neither is fatal.
        var warned = report.Warnings.ToDictionary(s => s.ModId, s => s.Detail);
        Assert.Contains("modinfo.json could not be read", warned["badjson"]);
        Assert.Contains("zip could not be opened", warned["notazip"]);

        // The isolation that matters: the good mod arrived, the broken ones were kept
        // rather than swept, and the lock was written despite all of it.
        Assert.Equal(["badjson", "goodmod", "notazip"],
            report.Lock.Mods.Select(m => m.ModId).Order().ToArray());
        Assert.True(File.Exists(LockPath));
    }

    [Fact]
    public async Task The_report_somebody_would_paste_names_the_mod_that_is_missing()
    {
        PlantBroken();

        var http = new HttpClient(new Stub());
        var log = new List<string>();
        var manifest = Manifest();

        var report = await new PackSyncer(new ModDbClient(http), http).SyncAsync(
            manifest, ModsDir, LockPath,
            progress: new Progress<SyncStep>(s => log.Add($"{s.Action} {s.ModId} {s.Detail}")));

        var text = Diagnostics.Report(manifest, report.Lock, log, modsDir: ModsDir);

        // The one line that explains why this pack will not launch or publish.
        Assert.Contains("NOT INSTALLED: missingmod", text);

        // And enough context to act on it without a round trip asking.
        Assert.Contains("Pack 'broken' — Broken On Purpose", text);
        Assert.Contains("1.22.3", text);
        Assert.Contains("goodmod", text);

        // Every mod described from its own zip, so the two that cannot be read say why
        // right where somebody is already looking rather than only in the sync log.
        Assert.Contains("sha256 matches the lock", text);
        Assert.Contains("modinfo.json could not be read", text);
        Assert.Contains("zip could not be opened", text);

        // Still no name of a real person anywhere in it.
        Assert.DoesNotContain(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), text);
    }

    [Fact]
    public async Task A_pack_this_broken_is_refused_publication_with_the_reason_why()
    {
        PlantBroken();

        var http = new HttpClient(new Stub());
        var manifest = Manifest();

        var report = await new PackSyncer(new ModDbClient(http), http)
            .SyncAsync(manifest, ModsDir, LockPath);

        var plan = await PublishPlan.PrepareAsync(
            manifest, PackLock.Load(LockPath), syncFailures: report.Steps);

        Assert.False(plan.CanPublish);

        // Names the mod and what the sync actually said about it, rather than telling the
        // author to go and sync a pack that has just been synced on their behalf.
        Assert.Contains("missingmod", plan.LockProblem);
        Assert.Contains("could not be installed", plan.LockProblem);
        Assert.DoesNotContain("Sync the pack first", plan.LockProblem);
    }
}
