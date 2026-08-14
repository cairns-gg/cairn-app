using System.Text.Json;
using System.Text.Json.Nodes;
using Cairn.Core.Launch;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Applying the config values a pack carries to the mods' own files.
///
/// The case throughout is the real one: Terrain Slabs needs Footprints named in a list
/// before the two behave together, the author works that out once, and everybody who
/// installs the pack should get the answer without being told to edit a file.
/// </summary>
public class ModConfigTests : IDisposable
{
    private readonly string _data = Path.Combine(
        Path.GetTempPath(), "cairn-modconfig-" + Guid.NewGuid().ToString("n")[..8]);

    public ModConfigTests() => Directory.CreateDirectory(_data);

    public void Dispose()
    {
        if (Directory.Exists(_data)) Directory.Delete(_data, recursive: true);
    }

    private string Config(string name) => Path.Combine(_data, "ModConfig", name);

    private void WriteConfig(string name, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Config(name))!);
        File.WriteAllText(Config(name), json);
    }

    private JsonObject ReadConfig(string name) =>
        (JsonNode.Parse(File.ReadAllText(Config(name))) as JsonObject)!;

    private static Dictionary<string, JsonObject> Declare(string file, string json) =>
        new() { [file] = (JsonNode.Parse(json) as JsonObject)! };

    private IReadOnlyList<ModConfigChange> Apply(Dictionary<string, JsonObject>? declared) =>
        ModConfigFiles.Apply(_data, declared);

    // ---- the file the mod has not written yet ----

    /// <summary>
    /// A follower's first launch: the mod has never run, so there is no file. Cairn writes
    /// one holding only what the pack asserts, and the mod fills in the rest — its own
    /// defaults for a missing key, on both of the game's config paths.
    /// </summary>
    [Fact]
    public void A_value_lands_in_a_file_that_does_not_exist_yet()
    {
        var changes = Apply(Declare("terrainslabs.json", """{ "compatibleMods": ["footprints"] }"""));

        Assert.Equal(ModConfigOutcome.Applied, Assert.Single(changes).Outcome);
        Assert.Equal("compatibleMods", changes[0].Key);

        Assert.Equal("footprints", ReadConfig("terrainslabs.json")["compatibleMods"]![0]!.GetValue<string>());
    }

    /// <summary>
    /// The case that decides the whole design. By the time anybody has pressed Play once,
    /// the mod has rewritten its config in full and every key is present at its default —
    /// so the rule ClientHotkeys uses, fill only what is absent, would do nothing at all.
    /// The pack's first word about a key wins, because a player cannot have overridden a
    /// pack that had not yet said anything.
    /// </summary>
    [Fact]
    public void A_value_sitting_at_the_mods_default_is_replaced()
    {
        WriteConfig("terrainslabs.json", """{ "enableSlabs": true, "compatibleMods": [] }""");

        var changes = Apply(Declare("terrainslabs.json", """{ "compatibleMods": ["footprints"] }"""));

        Assert.Equal(ModConfigOutcome.Applied, Assert.Single(changes).Outcome);
        Assert.Equal("footprints", ReadConfig("terrainslabs.json")["compatibleMods"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Everything_else_in_the_file_survives()
    {
        WriteConfig("BedSpawn.json", """
        { "RequireSneaking": false, "Rooms": { "Enabled": false, "BedsThatDontRequireRooms": [] },
          "Cooldown": { "Enabled": false, "CooldownDays": 0.0 } }
        """);

        Apply(Declare("BedSpawn.json", """{ "Rooms": { "Enabled": true } }"""));

        var root = ReadConfig("BedSpawn.json");
        Assert.True(root["Rooms"]!["Enabled"]!.GetValue<bool>());

        // The sibling inside the section it reached into, and the sections it did not.
        Assert.Empty(root["Rooms"]!["BedsThatDontRequireRooms"]!.AsArray());
        Assert.False(root["RequireSneaking"]!.GetValue<bool>());
        Assert.False(root["Cooldown"]!["Enabled"]!.GetValue<bool>());
    }

    /// <summary>
    /// Objects recurse, everything else is a leaf. There is no answer to whether a declared
    /// list appends, replaces or de-duplicates that is right for every mod, so the manifest
    /// means what it appears to say.
    /// </summary>
    [Fact]
    public void An_array_is_replaced_whole_rather_than_merged()
    {
        WriteConfig("x.json", """{ "list": ["a", "b"] }""");

        Apply(Declare("x.json", """{ "list": ["c"] }"""));

        Assert.Equal(["c"], ReadConfig("x.json")["list"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    // ---- what the player has changed is theirs ----

    /// <summary>
    /// The pack's value arrives, the player moves it, and it is theirs from then on —
    /// including against a later pack update that changes the same key again.
    /// </summary>
    [Fact]
    public void A_value_changed_after_Cairn_wrote_it_is_left_alone_and_reported()
    {
        Apply(Declare("statushud.json", """{ "showClock": true }"""));

        // The player, in game.
        WriteConfig("statushud.json", """{ "showClock": false }""");

        var changes = Apply(Declare("statushud.json", """{ "showClock": true }"""));

        var kept = Assert.Single(changes);
        Assert.Equal(ModConfigOutcome.Kept, kept.Outcome);
        Assert.Equal("statushud.json", kept.File);
        Assert.False(ReadConfig("statushud.json")["showClock"]!.GetValue<bool>());
    }

    [Fact]
    public void And_stays_theirs_when_the_pack_moves_the_value_again()
    {
        Apply(Declare("statushud.json", """{ "scale": 1.0 }"""));
        WriteConfig("statushud.json", """{ "scale": 2.0 }""");

        Apply(Declare("statushud.json", """{ "scale": 1.5 }"""));
        Apply(Declare("statushud.json", """{ "scale": 3.0 }"""));

        Assert.Equal(2.0, ReadConfig("statushud.json")["scale"]!.GetValue<double>());
    }

    /// <summary>
    /// A value the pack declines to overwrite is still recorded as the pack's word. Were
    /// only writes recorded, it would read as a first word on the next launch and be taken
    /// away from the player again — every launch, forever.
    /// </summary>
    [Fact]
    public void A_kept_value_is_not_taken_on_the_next_launch()
    {
        Apply(Declare("x.json", """{ "v": 1 }"""));
        WriteConfig("x.json", """{ "v": 2 }""");

        Assert.Equal(ModConfigOutcome.Kept, Assert.Single(Apply(Declare("x.json", """{ "v": 1 }"""))).Outcome);
        Assert.Equal(ModConfigOutcome.Kept, Assert.Single(Apply(Declare("x.json", """{ "v": 1 }"""))).Outcome);

        Assert.Equal(2, ReadConfig("x.json")["v"]!.GetValue<int>());
    }

    /// <summary>
    /// A second launch that changes nothing says nothing. A line per launch is noise, and
    /// noise is what trains people to skip the line that matters.
    /// </summary>
    [Fact]
    public void A_second_launch_reports_nothing_further()
    {
        var declared = Declare("x.json", """{ "v": 1 }""");

        Assert.Single(Apply(declared));
        Assert.Empty(Apply(declared));
    }

    // ---- shapes the file can be in ----

    /// <summary>
    /// The game reads these keys case-insensitively — JsonObject's own indexer does, and
    /// Newtonsoft matches properties the same way. Writing the manifest's spelling beside
    /// the file's would leave two keys and let the mod pick between them, which is a setting
    /// that silently does nothing.
    /// </summary>
    [Fact]
    public void A_key_that_differs_only_in_case_is_the_same_key()
    {
        WriteConfig("x.json", """{ "EnableSlabs": false }""");

        Apply(Declare("x.json", """{ "enableSlabs": true }"""));

        var root = ReadConfig("x.json");
        Assert.Single(root);
        Assert.True(root["EnableSlabs"]!.GetValue<bool>());
    }

    [Fact]
    public void A_mod_keeping_its_config_in_a_folder_is_reachable()
    {
        WriteConfig(Path.Combine("XLeveling", "mining.json"), """{ "enabled": false }""");

        Apply(Declare("XLeveling/mining.json", """{ "enabled": true }"""));

        Assert.True(ReadConfig(Path.Combine("XLeveling", "mining.json"))["enabled"]!.GetValue<bool>());
    }

    /// <summary>
    /// Two of the hundred-odd config files in a real pack use comments to document their own
    /// settings. Rewriting one through a JSON writer would delete that documentation without
    /// asking, so it is read, refused, and said out loud.
    /// </summary>
    [Fact]
    public void A_file_with_comments_in_it_is_left_exactly_as_it_was()
    {
        var original = """
        {
          // === Vanilla More Molds : Configuration File ===
          "enableMolds": false
        }
        """;
        WriteConfig("vanillamoremolds.json", original);

        var refused = Assert.Single(Apply(Declare("vanillamoremolds.json", """{ "enableMolds": true }""")));

        Assert.Equal(ModConfigOutcome.Refused, refused.Outcome);
        Assert.Contains("comments", refused.Detail);
        Assert.Equal(original, File.ReadAllText(Config("vanillamoremolds.json")));
    }

    [Fact]
    public void A_file_whose_top_level_is_a_list_is_refused()
    {
        WriteConfig("markers.json", """[ { "Category": "Flora" } ]""");

        var refused = Assert.Single(Apply(Declare("markers.json", """{ "Category": "Fauna" }""")));

        Assert.Equal(ModConfigOutcome.Refused, refused.Outcome);
        Assert.Contains("list", refused.Detail);
    }

    [Fact]
    public void A_section_where_the_file_holds_a_single_value_is_left_alone()
    {
        WriteConfig("x.json", """{ "rooms": 3 }""");

        var kept = Assert.Single(Apply(Declare("x.json", """{ "rooms": { "enabled": true } }""")));

        Assert.Equal(ModConfigOutcome.Kept, kept.Outcome);
        Assert.Equal(3, ReadConfig("x.json")["rooms"]!.GetValue<int>());
    }

    // ---- the record ----

    [Fact]
    public void Nothing_is_written_when_the_pack_declares_nothing()
    {
        Assert.Empty(Apply(null));
        Assert.Empty(Apply([]));

        Assert.False(File.Exists(Path.Combine(_data, ModConfigFiles.RecordName)));
        Assert.False(Directory.Exists(Path.Combine(_data, "ModConfig")));
    }

    /// <summary>
    /// A value the pack has stopped setting is said once and then forgotten. The mod's own
    /// default for it is not knowable from outside the game — it lives in a field
    /// initialiser in the mod's assembly — so there is nothing to put back.
    /// </summary>
    [Fact]
    public void A_value_the_pack_stops_setting_is_reported_once_and_left_as_it_is()
    {
        Apply(Declare("x.json", """{ "v": 1 }"""));

        var changes = Apply(Declare("x.json", """{ "other": 2 }"""));

        var released = Assert.Single(changes, c => c.Outcome == ModConfigOutcome.Released);
        Assert.Equal("v", released.Key);
        Assert.Equal("other", Assert.Single(changes, c => c.Outcome == ModConfigOutcome.Applied).Key);

        Assert.Empty(Apply(Declare("x.json", """{ "other": 2 }""")));
        Assert.Equal(1, ReadConfig("x.json")["v"]!.GetValue<int>());
    }

    [Fact]
    public void A_file_dropped_from_the_manifest_is_reported_once()
    {
        Apply(Declare("x.json", """{ "v": 1 }"""));

        var released = Assert.Single(Apply([]));
        Assert.Equal(ModConfigOutcome.Released, released.Outcome);

        Assert.Empty(Apply([]));
        Assert.False(File.Exists(Path.Combine(_data, ModConfigFiles.RecordName)));
    }

    /// <summary>
    /// The record lives in the data path so it dies with it. Kept beside the manifest it
    /// would survive Delete data, and the next launch would read the mod's freshly written
    /// defaults as a player's deliberate edits — refusing to apply the pack to them forever.
    /// </summary>
    [Fact]
    public void Deleting_the_data_path_takes_the_record_with_it()
    {
        Apply(Declare("x.json", """{ "v": 1 }"""));
        WriteConfig("x.json", """{ "v": 2 }""");
        Assert.Equal(ModConfigOutcome.Kept, Assert.Single(Apply(Declare("x.json", """{ "v": 1 }"""))).Outcome);

        Directory.Delete(_data, recursive: true);
        Directory.CreateDirectory(_data);

        // A fresh data path, and the mod has written its defaults into it again.
        WriteConfig("x.json", """{ "v": 2 }""");
        Assert.Equal(ModConfigOutcome.Applied, Assert.Single(Apply(Declare("x.json", """{ "v": 1 }"""))).Outcome);
    }

    // ---- the launch ----

    [Fact]
    public void A_launch_applies_the_packs_mod_config()
    {
        var root = Path.Combine(_data, "home");
        var store = new PackStore(Path.Combine(root, "packs"));
        var manifest = store.Create("anego", "1.22.5", "Anego");
        manifest.ModConfig = Declare("terrainslabs.json", """{ "compatibleMods": ["footprints"] }""");
        manifest.Save(store.ManifestPath("anego"));

        var data = new PackData(store, Path.Combine(root, "session.json"), Path.Combine(root, "shared"));

        var changes = new List<ModConfigChange>();
        data.BeforeLaunch("anego", config: changes);

        Assert.Equal(ModConfigOutcome.Applied, Assert.Single(changes).Outcome);

        var written = JsonNode.Parse(File.ReadAllText(
            Path.Combine(store.DataDir("anego"), "ModConfig", "terrainslabs.json")))!;

        Assert.Equal("footprints", written["compatibleMods"]![0]!.GetValue<string>());
    }

    /// <summary>
    /// The values arrive whether or not the caller wanted to print about them — the same
    /// trap the hotkey work fell into, where the reporting collection was what switched the
    /// feature on and a front end that never printed the line silently did not have it.
    /// </summary>
    [Fact]
    public void A_launch_that_reports_nothing_still_applies_them()
    {
        var root = Path.Combine(_data, "home");
        var store = new PackStore(Path.Combine(root, "packs"));
        var manifest = store.Create("anego", "1.22.5", "Anego");
        manifest.ModConfig = Declare("x.json", """{ "v": 1 }""");
        manifest.Save(store.ManifestPath("anego"));

        new PackData(store, Path.Combine(root, "session.json"), Path.Combine(root, "shared"))
            .BeforeLaunch("anego");

        Assert.True(File.Exists(Path.Combine(store.DataDir("anego"), "ModConfig", "x.json")));
    }

    // ---- the manifest ----

    [Fact]
    public void The_mod_config_survives_a_manifest_round_trip()
    {
        var path = Path.Combine(_data, "pack.json");

        new PackManifest
        {
            Id = "anego",
            GameVersion = "1.22.5",
            ModConfig = Declare("terrainslabs.json", """{ "compatibleMods": ["footprints"] }"""),
        }.Save(path);

        var loaded = PackManifest.Load(path);

        Assert.Equal("footprints",
            loaded.ModConfig!["terrainslabs.json"]["compatibleMods"]![0]!.GetValue<string>());
    }

    /// <summary>
    /// A manifest arrives from somebody else and its keys are joined onto a path on the
    /// machine that imported it. Refused at validation — which is to say at import — rather
    /// than on the machine of whoever eventually presses Play.
    /// </summary>
    [Theory]
    [InlineData("../../etc/passwd", "outside")]
    [InlineData("a/../../b.json", "outside")]
    [InlineData("XLeveling\\mining.json", "backslashes")]
    [InlineData("/etc/passwd", "absolute")]
    [InlineData("imgui.ini", ".json and .yaml")]
    [InlineData("", "no file name")]
    public void A_path_that_could_write_outside_ModConfig_is_refused(string file, string expected)
    {
        var manifest = new PackManifest
        {
            Id = "anego",
            GameVersion = "1.22.5",
            ModConfig = new Dictionary<string, JsonObject> { [file] = new() { ["v"] = 1 } },
        };

        Assert.Contains(expected, Assert.Single(manifest.ModConfigProblems()));

        // And refused again at the point of use, so the check is not only a message.
        Assert.Equal(ModConfigOutcome.Refused,
            Assert.Single(Apply(new Dictionary<string, JsonObject> { [file] = new() { ["v"] = 1 } })).Outcome);
    }

    /// <summary>
    /// And refused where it actually arrives from a stranger. A bundle is fetched from a URL
    /// and its manifest is written straight to disk, so this is the path that matters: the
    /// check has to be part of what makes a bundle valid, not something a front end
    /// remembers to call.
    /// </summary>
    [Fact]
    public void A_shared_pack_that_writes_outside_ModConfig_is_refused_at_import()
    {
        var json = PackBundle.Serialize(new PackManifest
        {
            Id = "anego",
            GameVersion = "1.22.5",
            ModConfig = new Dictionary<string, JsonObject>
            {
                ["../../../.bashrc"] = new() { ["v"] = 1 },
            },
        });

        var refused = Assert.Throws<InvalidDataException>(() => PackBundle.Parse(json));
        Assert.Contains("outside", refused.Message);
    }

    /// <summary>
    /// pack.json is a shared document — published, fetched by everyone who imports the pack,
    /// and meant to be read by eye. In a real pack the largest config file is a 149KB ore
    /// table, and a manifest that swallowed one whole would have stopped being a manifest.
    /// </summary>
    [Fact]
    public void A_pack_carrying_a_whole_ore_table_is_refused()
    {
        var big = new JsonObject();
        for (var i = 0; i < 4000; i++) big[$"ore{i}"] = "a string long enough to count";

        var manifest = new PackManifest
        {
            Id = "anego",
            GameVersion = "1.22.5",
            ModConfig = new Dictionary<string, JsonObject> { ["OreDatabase.json"] = big },
        };

        Assert.Contains("64KB limit", Assert.Single(manifest.ModConfigProblems()));
    }

    [Fact]
    public void A_pack_that_carries_none_is_unchanged_by_any_of_this()
    {
        var manifest = new PackManifest { Id = "anego", GameVersion = "1.22.5" };

        Assert.Empty(manifest.ModConfigProblems());
        Assert.Empty(manifest.Validate());

        // Absent rather than empty, so the file of a pack that never set one looks exactly
        // as it did before this existed.
        Assert.DoesNotContain("modConfig", JsonSerializer.Serialize(manifest, PackManifest.JsonOptions));
    }
}
