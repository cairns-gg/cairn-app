using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Cairn.Core.Launch;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Writing a ConfigLib file before the mod that owns it has ever run.
///
/// The case is the one that made this worth building: a pack sets how far apart BetterRuins
/// puts its megastructures, an admin deploys it to a dedicated server, and the first launch
/// generates the world. Landing that value a launch later is not a setting that was briefly
/// wrong — it is a world generated against the wrong number, and terrain is not revisited.
/// </summary>
public class ConfigLibSeedTests : IDisposable
{
    private readonly string _data = Path.Combine(
        Path.GetTempPath(), "cairn-seed-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly string _mods = Path.Combine(
        Path.GetTempPath(), "cairn-seed-mods-" + Guid.NewGuid().ToString("n")[..8]);

    public ConfigLibSeedTests()
    {
        Directory.CreateDirectory(_data);
        Directory.CreateDirectory(_mods);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _data, _mods })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// The real shape, cut down: a version, groups named after types, and a name and default
    /// per setting. Taken from BetterRuins 0.6.3's own patch file.
    /// </summary>
    private const string BetterRuinsPatches = """
    {
      "version": 12,
      "patches": { "integer": { "betterruins:worldgen/structures.json": {} } },
      "settings": {
        "integer": {
          "MEGASTRUCTURES_MIN_DISTANCE": {
            "name": "megastructures_min_distance",
            "default": 5000,
            "range": { "min": 0, "max": 100000 }
          },
          "MEGASTRUCTURES_MIN_SPAWN_DISTANCE": {
            "name": "megastructures_min_spawn_distance",
            "default": 3000
          }
        },
        "boolean": {
          "SCHEMATIC_CRAFTING": { "name": "schematic_crafting", "default": true }
        }
      }
    }
    """;

    private string Config(string name) => Path.Combine(_data, "ModConfig", name);

    private void WriteMod(string zipName, string domain, string patches)
    {
        using var file = File.Create(Path.Combine(_mods, zipName));
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);

        using var entry = zip.CreateEntry($"assets/{domain}/config/configlib-patches.json").Open();
        entry.Write(Encoding.UTF8.GetBytes(patches));
    }

    private static Dictionary<string, JsonObject> Declare(string file, string json) =>
        new() { [file] = (JsonNode.Parse(json) as JsonObject)! };

    private IReadOnlyList<ModConfigChange> Apply(
        Dictionary<string, JsonObject>? declared, bool withMods = true) =>
        ModConfigFiles.Apply(_data, declared, withMods ? _mods : null);

    /// <summary>
    /// The whole point. What used to be refused as "the mod has not written it yet" is now
    /// written from the mod's own schema and set in the same launch.
    /// </summary>
    [Fact]
    public void A_pack_value_lands_on_the_first_launch_when_the_mod_ships_a_schema()
    {
        WriteMod("BetterRuinsv0.6.3.zip", "betterruins", BetterRuinsPatches);

        var applied = Assert.Single(Apply(Declare("betterruins.yaml",
            """{ "megastructures_min_distance": 2500 }""")));

        Assert.Equal(ModConfigOutcome.Applied, applied.Outcome);
        Assert.Equal("megastructures_min_distance", applied.Key);

        var text = File.ReadAllText(Config("betterruins.yaml"));
        Assert.Contains("megastructures_min_distance: 2500\n", text);
    }

    /// <summary>
    /// ConfigLib compares this against the number in the mod's patch file and, on a mismatch,
    /// does not decline the file but overwrites every setting in it with the mod's defaults.
    /// Writing the wrong one would be worse than not writing at all.
    /// </summary>
    [Fact]
    public void The_seeded_file_carries_the_version_from_the_mods_own_patch_file()
    {
        WriteMod("BetterRuinsv0.6.3.zip", "betterruins", BetterRuinsPatches);

        Apply(Declare("betterruins.yaml", """{ "megastructures_min_distance": 2500 }"""));

        Assert.StartsWith("version: 12\n",
            File.ReadAllText(Config("betterruins.yaml")).Split('\n')
                .First(l => l.StartsWith("version")) + "\n");
    }

    /// <summary>
    /// Seeded with the mod's defaults and not with the pack's values, so the merge that
    /// decides every other file decides this one too — and reports what it did.
    /// </summary>
    [Fact]
    public void Settings_the_pack_says_nothing_about_are_seeded_at_the_mods_own_defaults()
    {
        WriteMod("BetterRuinsv0.6.3.zip", "betterruins", BetterRuinsPatches);

        Apply(Declare("betterruins.yaml", """{ "megastructures_min_distance": 2500 }"""));

        var text = File.ReadAllText(Config("betterruins.yaml"));

        Assert.Contains("megastructures_min_spawn_distance: 3000\n", text);
        Assert.Contains("schematic_crafting: true\n", text);
    }

    /// <summary>
    /// A patch file naming a <c>file</c> is one where ConfigLib is only a settings screen over
    /// the mod's own JSON config. It generates no <c>&lt;domain&gt;.yaml</c>, so writing one
    /// would be a file nothing ever reads — and the setting would go on being wrong.
    /// </summary>
    [Fact]
    public void A_mod_whose_ConfigLib_edits_its_own_json_is_not_given_a_yaml()
    {
        WriteMod("Gravestones.zip", "gravestones", """
        {
          "version": 1,
          "file": "gravestones.json",
          "settings": { "boolean": { "X": { "name": "keep_items", "default": true } } }
        }
        """);

        var refused = Assert.Single(Apply(Declare("gravestones.yaml", """{ "keep_items": false }""")));

        Assert.Equal(ModConfigOutcome.Refused, refused.Outcome);
        Assert.Equal("modconfig-why-not-yet", refused.Detail!.Key);
        Assert.False(File.Exists(Config("gravestones.yaml")));
    }

    [Theory]
    [InlineData("""{ "patches": {}, "settings": { "boolean": { "X": { "name": "a", "default": true } } } }""")]
    [InlineData("""{ "version": 3, "settings": [] }""")]
    [InlineData("""{ "version": 3, "settings": { "boolean": { "X": { "name": "a" } } } }""")]
    [InlineData("not json at all")]
    public void A_schema_that_cannot_be_trusted_waits_exactly_as_it_did_before(string patches)
    {
        WriteMod("Thing.zip", "thing", patches);

        var refused = Assert.Single(Apply(Declare("thing.yaml", """{ "a": false }""")));

        Assert.Equal(ModConfigOutcome.Refused, refused.Outcome);
        Assert.Equal("modconfig-why-not-yet", refused.Detail!.Key);
        Assert.False(File.Exists(Config("thing.yaml")));
    }

    /// <summary>
    /// A default this cannot write on one line, or a name that would not read back as itself,
    /// costs that setting and not the file. ConfigLib fills in whatever is missing, so every
    /// other value the pack declares still lands a launch earlier than it used to.
    /// </summary>
    [Fact]
    public void A_setting_that_cannot_be_written_as_a_flat_scalar_is_left_out_of_the_seed()
    {
        WriteMod("Thing.zip", "thing", """
        {
          "version": 4,
          "settings": {
            "other": {
              "LIST":    { "name": "compatible_mods", "default": ["footprints"] },
              "MAPPING": { "name": "limits",          "default": { "min": 1 } },
              "ODD":     { "name": "not a flat key",  "default": 1 },
              "FINE":    { "name": "enable_rain",     "default": true }
            }
          }
        }
        """);

        var applied = Assert.Single(Apply(Declare("thing.yaml", """{ "enable_rain": false }""")));
        Assert.Equal(ModConfigOutcome.Applied, applied.Outcome);

        var text = File.ReadAllText(Config("thing.yaml"));

        Assert.Contains("enable_rain: false\n", text);
        Assert.DoesNotContain("compatible_mods", text);
        Assert.DoesNotContain("limits", text);
        Assert.DoesNotContain("not a flat key", text);

        // And the file this wrote is one the next launch can still read.
        Assert.Empty(Apply(Declare("thing.yaml", """{ "enable_rain": false }""")));
    }

    [Fact]
    public void A_mod_with_no_schema_at_all_waits_for_ConfigLib_as_it_always_did()
    {
        var refused = Assert.Single(Apply(Declare("seafarer.yaml", """{ "drying-enable-wind": false }""")));

        Assert.Equal(ModConfigOutcome.Refused, refused.Outcome);
        Assert.Equal("modconfig-why-not-yet", refused.Detail!.Key);
    }

    /// <summary>
    /// A file on disk is ConfigLib's or the player's, and seeding has nothing to add to
    /// either — least of all the mod's defaults over somebody's settings.
    /// </summary>
    [Fact]
    public void An_existing_file_is_never_seeded_over()
    {
        WriteMod("BetterRuinsv0.6.3.zip", "betterruins", BetterRuinsPatches);

        Directory.CreateDirectory(Path.GetDirectoryName(Config("betterruins.yaml"))!);
        File.WriteAllText(Config("betterruins.yaml"),
            "version: 12\nmegastructures_min_distance: 9000\nschematic_crafting: false\n");

        Apply(Declare("betterruins.yaml", """{ "megastructures_min_distance": 2500 }"""));

        var text = File.ReadAllText(Config("betterruins.yaml"));

        Assert.Contains("megastructures_min_distance: 2500\n", text);

        // Untouched, rather than reset to the schema's default of true.
        Assert.Contains("schematic_crafting: false\n", text);
    }

    /// <summary>
    /// Without a Mods directory this is the launcher as it was, which is what keeps every
    /// caller that has no pack behind it — and every existing test — honest.
    /// </summary>
    [Fact]
    public void Nothing_is_seeded_when_no_mods_directory_is_given()
    {
        WriteMod("BetterRuinsv0.6.3.zip", "betterruins", BetterRuinsPatches);

        var refused = Assert.Single(Apply(
            Declare("betterruins.yaml", """{ "megastructures_min_distance": 2500 }"""),
            withMods: false));

        Assert.Equal(ModConfigOutcome.Refused, refused.Outcome);
        Assert.False(File.Exists(Config("betterruins.yaml")));
    }

    /// <summary>
    /// The seeded file has to be one ConfigLib will accept and one Cairn can edit again next
    /// launch — which is the same rule, since both read it the same way.
    /// </summary>
    [Fact]
    public void A_seeded_file_reads_back_as_the_values_it_was_written_with()
    {
        WriteMod("BetterRuinsv0.6.3.zip", "betterruins", BetterRuinsPatches);

        var declared = Declare("betterruins.yaml", """{ "megastructures_min_distance": 2500 }""");
        Apply(declared);

        // Second launch over the file the first one wrote: nothing left to do, and nothing
        // reported. A launch that rewrote it would be one changing the file's mtime, which is
        // what ConfigLib's own file watcher reacts to.
        Assert.Empty(Apply(declared));
    }
}
