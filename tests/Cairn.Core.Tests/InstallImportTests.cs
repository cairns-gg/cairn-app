using System.IO.Compression;
using System.Net;
using System.Text;
using Cairn.Core.ModDb;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Turning the mods somebody already has into a pack.
///
/// The half that matters is what "import the versions I am running" means once it meets a
/// pack: the manifest names the mods and pins nothing, and the exact releases go in the
/// lockfile. Sync installs from the lock, so the first launch reproduces the folder that was
/// imported; updating is still the button it always was. Pinning instead would reproduce it
/// too, and then never move again.
///
/// The other half is what is left behind. A mod ModDB will not serve cannot be in a pack —
/// a pack is a list of things anyone can fetch — so it is skipped and named, one line per
/// mod, rather than dropped quietly into a count that does not add up.
/// </summary>
public class InstallImportTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-import-" + Guid.NewGuid().ToString("n")[..8]);

    private string InstallMods => Path.Combine(_root, "VintagestoryData", "Mods");

    public InstallImportTests() => Directory.CreateDirectory(InstallMods);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ---- a folder of mods, as the game leaves it ----

    /// <summary>A mod zip with a modinfo.json in it, as ModDB serves them.</summary>
    private string WriteMod(string fileName, string? modId, string? version, string? name = null)
    {
        var path = Path.Combine(InstallMods, fileName);

        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);

        if (modId is not null)
        {
            using var writer = new StreamWriter(zip.CreateEntry("modinfo.json").Open());
            writer.Write($$"""
                {"modid":"{{modId}}","name":"{{name ?? modId}}","version":"{{version}}"}
                """);
        }
        else
        {
            zip.CreateEntry("assets/nothing.json");
        }

        return path;
    }

    /// <summary>
    /// Serves a small ModDB: each mod's releases as given, and a zip for any download.
    ///
    /// A mod nobody registered comes back as a 200 carrying no mod, which is how ModDB
    /// itself reports one that does not exist — the distinction that lets an import tell
    /// "there is no such mod" from "ModDB could not be reached".
    /// </summary>
    private sealed class Moddb(Dictionary<string, (string Version, string[] Tags)[]> mods)
        : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var url = r.RequestUri!.ToString();
            Requests++;

            if (!url.Contains("/api/mod/"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Zip()),
                });

            var id = url[(url.LastIndexOf('/') + 1)..];

            if (!mods.TryGetValue(id, out var releases))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"statuscode":"404"}""", Encoding.UTF8, "application/json"),
                });

            var listed = releases.Select((x, i) => $$"""
                {"releaseid":{{i + 1}},"fileid":{{i + 1}},"modidstr":"{{id}}",
                 "modversion":"{{x.Version}}","filename":"{{id}}_{{x.Version}}.zip",
                 "mainfile":"https://moddbcdn.vintagestory.at/{{id}}_{{x.Version}}.zip",
                 "tags":[{{string.Join(",", x.Tags.Select(t => $"\"{t}\""))}}]}
                """);

            var body = $"{{\"statuscode\":\"200\",\"mod\":{{\"modid\":1,\"assetid\":2,"
                       + $"\"name\":\"{id}\",\"urlalias\":\"{id}\",\"side\":\"both\","
                       + $"\"releases\":[{string.Join(",", listed)}]}}}}";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }

        private static byte[] Zip()
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(zip.CreateEntry("modinfo.json").Open());
                writer.Write("""{"modid":"olla","version":"1.2.0"}""");
            }

            return buffer.ToArray();
        }
    }

    private static Moddb Serving(params (string Id, string Version, string[] Tags)[] releases)
        => new(releases
            .GroupBy(r => r.Id)
            .ToDictionary(g => g.Key, g => g.Select(r => (r.Version, r.Tags)).ToArray()));

    /// <param name="playedOn">
    /// What the folder was being played on. Defaults to the version the pack targets, which
    /// is the ordinary case: importing the install you have into a pack for the game you
    /// have.
    /// </param>
    private async Task<IReadOnlyList<ImportCandidate>> PlanAsync(
        Moddb moddb, string gameVersion = "1.22.6", IReadOnlySet<string>? disabled = null,
        string? playedOn = "same")
    {
        var http = new HttpClient(moddb);
        var scan = InstalledMods.Scan(InstallMods);

        return await new InstallImport(new ModDbClient(http))
            .PlanAsync(scan, gameVersion, disabled, playedOn == "same" ? gameVersion : playedOn);
    }

    // ---- reading the folder ----

    [Fact]
    public void A_scan_reads_what_each_zip_calls_itself()
    {
        WriteMod("olla_1.2.0.zip", "olla", "1.2.0", "Olla");

        var mod = Assert.Single(InstalledMods.Scan(InstallMods).Mods);

        Assert.Equal("olla", mod.ModId);
        Assert.Equal("1.2.0", mod.Version);
        Assert.Equal("Olla 1.2.0", mod.Describe);
        Assert.Null(mod.Problem);
    }

    [Fact]
    public void Anything_that_is_not_a_mod_zip_is_listed_rather_than_passed_over()
    {
        WriteMod("olla_1.2.0.zip", "olla", "1.2.0");
        Directory.CreateDirectory(Path.Combine(InstallMods, "UnpackedMod"));
        File.WriteAllText(Path.Combine(InstallMods, "Creating_Mods.txt"), "the game's own note");

        var scan = InstalledMods.Scan(InstallMods);

        // "It found 11 of my 14 mods" has to be able to say which three and why.
        Assert.Equal(["UnpackedMod"], scan.Ignored);
        Assert.Single(scan.Mods);
    }

    [Fact]
    public void A_folder_that_is_not_there_scans_to_nothing()
    {
        var scan = InstalledMods.Scan(Path.Combine(_root, "nowhere"));

        Assert.Empty(scan.Mods);
        Assert.Empty(scan.Ignored);
    }

    // ---- what each mod becomes ----

    [Fact]
    public async Task The_version_you_have_is_the_version_that_is_imported()
    {
        WriteMod("olla_1.2.0.zip", "olla", "1.2.0");

        // A newer one exists. It is not what is being run, so it is not what is imported.
        var plan = await PlanAsync(Serving(
            ("olla", "1.2.0", ["1.22.6"]),
            ("olla", "1.3.0", ["1.22.6"])));

        var olla = Assert.Single(plan);
        Assert.Equal(ImportVerdict.Ready, olla.Verdict);
        Assert.Equal("1.2.0", olla.Release!.ModVersion);
    }

    [Fact]
    public async Task A_version_this_game_cannot_have_moves_to_one_it_can()
    {
        WriteMod("olla_0.9.0.zip", "olla", "0.9.0");

        // Played on 1.21.4 and imported into a pack for 1.22.6 — someone moving to a newer
        // game. Running it there says nothing about running it here, so the pack takes the
        // release the new game actually has.
        var plan = await PlanAsync(
            Serving(("olla", "0.9.0", ["1.21.4"]), ("olla", "1.3.0", ["1.22.6"])),
            playedOn: "1.21.4");

        var olla = Assert.Single(plan);
        Assert.Equal(ImportVerdict.Newest, olla.Verdict);

        // Nothing to reproduce, so nothing is locked — the next sync resolves it.
        Assert.Null(olla.Release);
        Assert.Contains("will install 1.3.0", olla.Note);
    }

    [Fact]
    public async Task A_release_marked_for_nothing_like_this_game_is_imported_as_accepted()
    {
        // The mod stopped being updated, the game moved on, and they are still running it.
        // That is testimony, and the manifest has a place to record it.
        WriteMod("oldmod_1.0.0.zip", "oldmod", "1.0.0");

        var plan = await PlanAsync(Serving(("oldmod", "1.0.0", ["1.21.4"])));

        var mod = Assert.Single(plan);
        Assert.Equal(ImportVerdict.Accepted, mod.Verdict);
        Assert.Contains("because you are running it", mod.Note);
    }

    [Fact]
    public async Task Nobody_testifies_for_a_game_they_were_not_playing()
    {
        // The same mod and the same folder, imported without saying what it was played on.
        // An acceptance is a sentence somebody said; it is not inferred from a zip.
        WriteMod("oldmod_1.0.0.zip", "oldmod", "1.0.0");

        var plan = await PlanAsync(Serving(("oldmod", "1.0.0", ["1.21.4"])), playedOn: null);

        Assert.Equal(ImportVerdict.Incompatible, Assert.Single(plan).Verdict);
    }

    [Fact]
    public async Task A_mod_ModDB_has_never_heard_of_is_skipped_and_named()
    {
        WriteMod("olla_1.2.0.zip", "olla", "1.2.0");
        WriteMod("secretbuild_0.1.0.zip", "secretbuild", "0.1.0");

        var plan = await PlanAsync(Serving(("olla", "1.2.0", ["1.22.6"])));

        var skipped = Assert.Single(plan, c => c.ModId == "secretbuild");
        Assert.Equal(ImportVerdict.Unknown, skipped.Verdict);
        Assert.False(skipped.Included);

        // And it does not take the rest of the folder with it.
        Assert.True(Assert.Single(plan, c => c.ModId == "olla").Included);
    }

    [Fact]
    public async Task A_zip_that_will_not_say_what_it_is_never_reaches_ModDB()
    {
        WriteMod("mystery.zip", modId: null, version: null);

        var moddb = Serving();
        var plan = await PlanAsync(moddb);

        Assert.Equal(ImportVerdict.Unreadable, Assert.Single(plan).Verdict);
        Assert.Equal(0, moddb.Requests);
    }

    [Fact]
    public async Task A_mod_switched_off_in_the_game_is_left_off()
    {
        WriteMod("olla_1.2.0.zip", "olla", "1.2.0");

        var plan = await PlanAsync(
            Serving(("olla", "1.2.0", ["1.22.6"])),
            disabled: new HashSet<string> { "olla" });

        Assert.Equal(ImportVerdict.Disabled, Assert.Single(plan).Verdict);
    }

    [Fact]
    public async Task An_old_copy_left_beside_the_one_in_use_is_imported_once()
    {
        WriteMod("olla_1.2.0.zip", "olla", "1.2.0");
        WriteMod("olla_1.3.0_old.zip", "olla", "1.3.0");

        var plan = await PlanAsync(Serving(
            ("olla", "1.2.0", ["1.22.6"]),
            ("olla", "1.3.0", ["1.22.6"])));

        Assert.Single(plan, c => c.Included);
        Assert.Single(plan, c => c.Verdict == ImportVerdict.Duplicate);
    }

    [Fact]
    public async Task A_mod_with_nothing_published_for_this_game_is_skipped()
    {
        WriteMod("olla_1.2.0.zip", "olla", "1.2.0");

        var plan = await PlanAsync(Serving(("olla", "1.2.0", ["1.19.8"])), gameVersion: "1.23.0");

        var olla = Assert.Single(plan);
        Assert.Equal(ImportVerdict.Accepted, olla.Verdict);   // running it is still testimony

        // ...but a version they are not running gets no such benefit.
        WriteMod("other_9.9.9.zip", "other", "9.9.9");

        var second = await PlanAsync(Serving(("other", "1.0.0", ["1.19.8"])), gameVersion: "1.23.0");
        Assert.Equal(ImportVerdict.Incompatible, Assert.Single(second, c => c.ModId == "other").Verdict);
    }

    /// <summary>
    /// Each mod is reported as it is settled, so a caller can list the folder immediately
    /// and fill the verdicts in behind it. Reading the zips is instant; it is the lookups
    /// that take a moment, and holding somebody's own mods back for them made it look as
    /// though finding them were the slow part.
    /// </summary>
    [Fact]
    public async Task Each_mod_is_reported_as_it_is_settled()
    {
        WriteMod("olla_1.2.0.zip", "olla", "1.2.0");
        WriteMod("carryon_2.1.4.zip", "carryon", "2.1.4");
        WriteMod("mystery.zip", modId: null, version: null);

        var told = new List<string>();

        var http = new HttpClient(Serving(
            ("olla", "1.2.0", ["1.22.6"]), ("carryon", "2.1.4", ["1.22.6"])));

        var plan = await new InstallImport(new ModDbClient(http)).PlanAsync(
            InstalledMods.Scan(InstallMods), "1.22.6", null, "1.22.6",
            new Progress<ImportCandidate>(c => told.Add(c.Mod.FileName)));

        // Every one of them, once, and none left to the end.
        Assert.Equal(plan.Count, told.Count);
        Assert.Equal(
            plan.Select(c => c.Mod.FileName).Order(),
            told.Order());
    }

    // ---- what gets written ----

    [Fact]
    public async Task The_pack_names_the_mods_and_pins_none_of_them()
    {
        WriteMod("olla_1.2.0.zip", "olla", "1.2.0");
        WriteMod("oldmod_1.0.0.zip", "oldmod", "1.0.0");

        var plan = await PlanAsync(Serving(
            ("olla", "1.2.0", ["1.22.6"]),
            ("oldmod", "1.0.0", ["1.21.4"])));

        var store = new PackStore(Path.Combine(_root, "packs"));
        var manifest = InstallImport.CreatePack(store, "mine", "1.22.6", "My mods", plan);

        // Unpinned: a pin means "stay here", and nobody said that by importing.
        Assert.All(manifest.Mods, m => Assert.Null(m.Version));
        Assert.Equal(["oldmod", "olla"], manifest.Mods.Select(m => m.ModId).Order());

        // The one being run unmarked carries the acceptance that makes it installable.
        Assert.Equal("1.22.6", Assert.Single(manifest.Mods, m => m.ModId == "oldmod").AcceptedFor);
        Assert.Null(Assert.Single(manifest.Mods, m => m.ModId == "olla").AcceptedFor);
    }

    [Fact]
    public async Task The_versions_being_run_are_written_to_the_lock()
    {
        WriteMod("olla_1.2.0.zip", "olla", "1.2.0");

        var plan = await PlanAsync(Serving(
            ("olla", "1.2.0", ["1.22.6"]),
            ("olla", "1.3.0", ["1.22.6"])));

        var store = new PackStore(Path.Combine(_root, "packs"));
        InstallImport.CreatePack(store, "mine", "1.22.6", "My mods", plan);

        var locked = Assert.Single(PackLock.Load(store.LockPath("mine"))!.Mods);
        Assert.Equal("1.2.0", locked.Version);

        // Nothing has been downloaded, so there is no hash yet. The syncer already knows
        // this state: it verifies against a locked hash when there is one, and records the
        // one it computed when there is not.
        Assert.Equal("", locked.Sha256);
    }

    /// <summary>
    /// The claim the whole design rests on, made end to end: after importing, a sync
    /// installs the version that was in the folder rather than the newest one — without
    /// anything being pinned.
    /// </summary>
    [Fact]
    public async Task Syncing_an_imported_pack_installs_what_was_installed()
    {
        WriteMod("olla_1.2.0.zip", "olla", "1.2.0");

        var moddb = Serving(("olla", "1.2.0", ["1.22.6"]), ("olla", "1.3.0", ["1.22.6"]));
        var plan = await PlanAsync(moddb);

        var store = new PackStore(Path.Combine(_root, "packs"));
        var manifest = InstallImport.CreatePack(store, "mine", "1.22.6", "My mods", plan);

        var http = new HttpClient(moddb);
        var report = await new PackSyncer(new ModDbClient(http), http)
            .SyncAsync(manifest, store.ModsDir("mine"), store.LockPath("mine"));

        Assert.False(report.Failed);
        Assert.Equal("1.2.0", Assert.Single(report.Lock.Mods).Version);

        // And the hash the lock was missing is there now.
        Assert.NotEqual("", Assert.Single(report.Lock.Mods).Sha256);
    }

    [Fact]
    public async Task An_imported_pack_can_still_be_updated()
    {
        WriteMod("olla_1.2.0.zip", "olla", "1.2.0");

        var moddb = Serving(("olla", "1.2.0", ["1.22.6"]), ("olla", "1.3.0", ["1.22.6"]));
        var plan = await PlanAsync(moddb);

        var store = new PackStore(Path.Combine(_root, "packs"));
        var manifest = InstallImport.CreatePack(store, "mine", "1.22.6", "My mods", plan);

        var http = new HttpClient(moddb);
        var syncer = new PackSyncer(new ModDbClient(http), http);
        await syncer.SyncAsync(manifest, store.ModsDir("mine"), store.LockPath("mine"));

        // Nothing is pinned, so the update check has something to offer — which is the
        // difference between importing versions and pinning them.
        var updates = await syncer.CheckUpdatesAsync(manifest, store.LockPath("mine"));

        Assert.Equal("olla 1.2.0 -> 1.3.0", Assert.Single(updates).Describe());
    }
}
