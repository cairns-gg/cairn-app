using System.Text.Json.Nodes;
using Cairn.Core.Launch;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// The authoring half: working out what an author changed, so the Mod config tab can offer
/// it as a tick instead of asking them to remember which key they edited.
/// </summary>
public class ModConfigSurveyTests : IDisposable
{
    private readonly string _data = Path.Combine(
        Path.GetTempPath(), "cairn-survey-" + Guid.NewGuid().ToString("n")[..8]);

    public ModConfigSurveyTests() => Directory.CreateDirectory(_data);

    public void Dispose()
    {
        if (Directory.Exists(_data)) Directory.Delete(_data, recursive: true);
    }

    private void WriteConfig(string name, string json)
    {
        var path = Path.Combine(_data, "ModConfig", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private static Dictionary<string, JsonObject> Declare(string file, string json) =>
        new() { [file] = (JsonNode.Parse(json) as JsonObject)! };

    private IReadOnlyList<ModConfigSetting> Read(
        Dictionary<string, JsonObject>? declared = null, bool all = false) =>
        ModConfigSurvey.Read(_data, declared, all);

    /// <summary>
    /// The whole point of the baseline: the mod wrote these once, the author changed one,
    /// and the tab shows the one rather than all of them.
    /// </summary>
    [Fact]
    public void Only_what_the_author_changed_since_the_mod_wrote_it()
    {
        WriteConfig("terrainslabs.json", """{ "enableSlabs": true, "compatibleMods": [] }""");
        ModConfigFiles.Capture(_data);

        WriteConfig("terrainslabs.json", """{ "enableSlabs": true, "compatibleMods": ["footprints"] }""");

        var changed = Assert.Single(Read());
        Assert.Equal("compatibleMods", changed.Key);
        Assert.True(changed.IsChanged);
        Assert.False(changed.IsCarried);
        Assert.Equal("[footprints]", changed.CurrentText);
        Assert.Equal("[]", changed.BaselineText);
    }

    [Fact]
    public void The_baseline_is_the_first_thing_seen_and_never_moves()
    {
        WriteConfig("x.json", """{ "v": 1 }""");
        ModConfigFiles.Capture(_data);

        WriteConfig("x.json", """{ "v": 2 }""");
        ModConfigFiles.Capture(_data);
        WriteConfig("x.json", """{ "v": 3 }""");
        ModConfigFiles.Capture(_data);

        // Still 1. A baseline that followed the file would say nothing had ever changed.
        Assert.Equal("1", Assert.Single(Read()).BaselineText);
    }

    [Fact]
    public void A_value_the_pack_already_carries_is_shown_as_carried()
    {
        WriteConfig("terrainslabs.json", """{ "compatibleMods": ["footprints"] }""");
        ModConfigFiles.Capture(_data);

        var carried = Assert.Single(Read(Declare("terrainslabs.json", """{ "compatibleMods": ["footprints"] }""")));

        Assert.True(carried.IsCarried);

        // Carried but not changed: it matches the file, which is what a pack that has been
        // launched looks like. Shown because the pack names it, not because it differs.
        Assert.False(carried.IsChanged);
    }

    /// <summary>
    /// The way out of the one thing the baseline cannot see. A value the author changed
    /// during the very first session was in the file before anything observed it, so it
    /// never reads as changed — and they still need to find it.
    /// </summary>
    [Fact]
    public void Everything_can_be_listed_for_a_change_the_baseline_never_saw()
    {
        WriteConfig("x.json", """{ "a": 1, "b": 2, "c": 3 }""");
        ModConfigFiles.Capture(_data);

        Assert.Empty(Read());
        Assert.Equal(3, Read(all: true).Count);
    }

    [Fact]
    public void A_file_first_seen_before_any_baseline_existed_claims_no_changes()
    {
        // No Capture: an older pack, whose files predate this feature.
        WriteConfig("x.json", """{ "a": 1 }""");

        var setting = Assert.Single(Read(all: true));
        Assert.False(setting.HasBaseline);
        Assert.False(setting.IsChanged);
        Assert.Equal("—", setting.BaselineText);
    }

    [Fact]
    public void Nested_settings_keep_their_path()
    {
        WriteConfig("BedSpawn.json", """{ "Rooms": { "Enabled": false } }""");
        ModConfigFiles.Capture(_data);
        WriteConfig("BedSpawn.json", """{ "Rooms": { "Enabled": true } }""");

        var setting = Assert.Single(Read());
        Assert.Equal(["Rooms", "Enabled"], setting.Path);
        Assert.Equal("Rooms.Enabled", setting.Key);
    }

    [Fact]
    public void A_mod_keeping_its_config_in_a_folder_is_surveyed_too()
    {
        WriteConfig(Path.Combine("XLeveling", "mining.json"), """{ "enabled": false }""");
        ModConfigFiles.Capture(_data);
        WriteConfig(Path.Combine("XLeveling", "mining.json"), """{ "enabled": true }""");

        Assert.Equal("XLeveling/mining.json", Assert.Single(Read()).File);
    }

    /// <summary>
    /// A row that could not be written is a row that must not be offered. Ticking one would
    /// promise an edit that Apply then refuses, which is a worse answer than not showing it.
    /// </summary>
    [Fact]
    public void Files_a_tick_could_not_reach_are_not_offered()
    {
        WriteConfig("imgui.ini", "[Window][Debug]\nPos=60,60\n");
        WriteConfig("commented.json", "{ // documented\n \"v\": 1 }");
        WriteConfig("markers.json", """[ { "Category": "Flora" } ]""");

        // Real structure in a YAML file, which is not the flat shape ConfigLib generates and
        // not something a line edit can safely touch.
        WriteConfig("nested.yaml", "settings:\n  enabled: true\n");

        Assert.Empty(Read(all: true));

        // But ConfigLib's own flat files are reachable, and are most of the YAML there is.
        WriteConfig("seafarer.yaml", "version: 1\ndrying-enable-wind: true\n");
        Assert.Equal(["drying-enable-wind", "version"], Read(all: true).Select(s => s.Key));
    }

    [Fact]
    public void Cairns_own_bookkeeping_is_never_offered_as_a_mod_setting()
    {
        WriteConfig("x.json", """{ "v": 1 }""");
        WriteConfig(ModConfigFiles.RecordName, """{ "x.json": { "v": 1 } }""");
        WriteConfig(ModConfigFiles.BaselineName, """{ "x.json": { "v": 1 } }""");

        Assert.All(Read(all: true), s => Assert.Equal("x.json", s.File));
    }

    // ---- ticking rows back into the manifest ----

    [Fact]
    public void Ticked_rows_become_what_the_manifest_carries()
    {
        WriteConfig("terrainslabs.json", """{ "compatibleMods": ["footprints"], "enableSlabs": true }""");
        WriteConfig(Path.Combine("XLeveling", "mining.json"), """{ "enabled": true }""");

        var all = Read(all: true);
        var carried = ModConfigSurvey.ToManifest(
            all.Where(s => s.Key is "compatibleMods" or "enabled"))!;

        Assert.Equal(2, carried.Count);
        Assert.Equal("footprints", carried["terrainslabs.json"]["compatibleMods"]![0]!.GetValue<string>());
        Assert.True(carried["XLeveling/mining.json"]["enabled"]!.GetValue<bool>());

        // And nothing that was not ticked.
        Assert.False(carried["terrainslabs.json"].ContainsKey("enableSlabs"));
    }

    [Fact]
    public void A_nested_ticked_row_rebuilds_its_sections()
    {
        WriteConfig("BedSpawn.json", """{ "Rooms": { "Enabled": true, "Other": 1 } }""");

        var carried = ModConfigSurvey.ToManifest(Read(all: true).Where(s => s.Key == "Rooms.Enabled"))!;

        Assert.True(carried["BedSpawn.json"]["Rooms"]!["Enabled"]!.GetValue<bool>());
        Assert.False(((JsonObject)carried["BedSpawn.json"]["Rooms"]!).ContainsKey("Other"));
    }

    /// <summary>
    /// Null rather than an empty object, so a pack that carries none reads as unchanged
    /// against what was published rather than as a revision that changed nothing.
    /// </summary>
    [Fact]
    public void Nothing_ticked_is_null_and_not_an_empty_object()
    {
        Assert.Null(ModConfigSurvey.ToManifest([]));
    }

    /// <summary>
    /// A mod renaming a setting leaves the pack carrying a key no file has. It gets a row of
    /// its own so it can be unticked deliberately — without which, ticking anything else
    /// would erase it from the shared document silently.
    /// </summary>
    [Fact]
    public void A_carried_key_the_file_no_longer_has_still_gets_a_row()
    {
        WriteConfig("x.json", """{ "newName": 1 }""");

        var declared = Declare("x.json", """{ "oldName": 1 }""");
        var orphan = Assert.Single(Read(declared), s => s.Key == "oldName");

        Assert.True(orphan.IsCarried);
        Assert.Equal("—", orphan.CurrentText);

        // Ticking it through keeps it; it is only lost by being unticked.
        var kept = ModConfigSurvey.ToManifest(Read(declared).Where(s => s.IsCarried))!;
        Assert.True(kept["x.json"].ContainsKey("oldName"));
    }

    // ---- the launch keeps the baseline up to date ----

    /// <summary>
    /// The first launch of a pack is the one where the config files do not exist when it
    /// starts, so the way in cannot see them. The way out is the first moment anything can.
    /// </summary>
    [Fact]
    public void A_first_launch_records_the_baseline_on_the_way_out()
    {
        var root = Path.Combine(_data, "home");
        var store = new PackStore(Path.Combine(root, "packs"));
        store.Create("anego", "1.22.5", "Anego");

        var data = new PackData(store, Path.Combine(root, "session.json"), Path.Combine(root, "shared"));
        data.BeforeLaunch("anego");

        // The mod, during the session.
        var config = Path.Combine(store.DataDir("anego"), "ModConfig", "x.json");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, """{ "v": 1 }""");

        data.AfterExit("anego");

        Assert.True(File.Exists(Path.Combine(store.DataDir("anego"), ModConfigFiles.BaselineName)));

        File.WriteAllText(config, """{ "v": 2 }""");

        var changed = Assert.Single(ModConfigSurvey.Read(store.DataDir("anego"), null));
        Assert.True(changed.IsChanged);
        Assert.Equal("1", changed.BaselineText);
    }

    /// <summary>
    /// The pack's own values are written before the mod first runs, so they end up in the
    /// baseline. That is correct rather than a leak: they are the keys the manifest already
    /// names, and the tab shows them as carried rather than as something the author changed.
    /// </summary>
    [Fact]
    public void A_packs_own_value_does_not_read_as_the_authors_change()
    {
        var root = Path.Combine(_data, "home");
        var store = new PackStore(Path.Combine(root, "packs"));
        var manifest = store.Create("anego", "1.22.5", "Anego");
        manifest.ModConfig = Declare("x.json", """{ "v": 1 }""");
        manifest.Save(store.ManifestPath("anego"));

        var data = new PackData(store, Path.Combine(root, "session.json"), Path.Combine(root, "shared"));
        data.BeforeLaunch("anego");
        data.AfterExit("anego");

        var setting = Assert.Single(ModConfigSurvey.Read(store.DataDir("anego"), manifest.ModConfig));

        Assert.True(setting.IsCarried);
        Assert.False(setting.IsChanged);
    }
}
