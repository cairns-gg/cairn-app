using System.Text.Json.Nodes;
using Cairn.Core.Launch;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// ConfigLib's flat YAML, which is where seven of the ten ConfigLib mods in a real pack keep
/// their settings — and was the single largest reason a pack could not carry one.
///
/// The fixture is the real generated shape, taken from a live pack: a version, a section
/// banner, a description above each setting and a "(default: …)" note below it.
/// </summary>
public class ModConfigYamlTests : IDisposable
{
    private readonly string _data = Path.Combine(
        Path.GetTempPath(), "cairn-yaml-" + Guid.NewGuid().ToString("n")[..8]);

    public ModConfigYamlTests() => Directory.CreateDirectory(_data);

    public void Dispose()
    {
        if (Directory.Exists(_data)) Directory.Delete(_data, recursive: true);
    }

    /// <summary>Exactly as ConfigLib writes one, down to the trailing spaces.</summary>
    private const string Seafarer =
        "version: 1\n"
        + "\n"
        + "\n"
        + "##################\n"
        + "## Drying Frame ##\n"
        + "##################\n"
        + "\n"
        + "\n"
        + "# Enable rain-based spoilage acceleration on drying frames\n"
        + "drying-enable-rain-rot: true\n"
        + " #  (default: True) \n"
        + "\n"
        + "# Max perish speed multiplier during heavy rain\n"
        + "drying-rain-rot-multiplier: 2\n"
        + " # from 1 to 10 with step of 0.5 (default: 2) \n"
        + "\n"
        + "# Comma-separated list of block codes\n"
        + "Whitelist: ''\n"
        + " #  (default: ) \n";

    private string Config(string name) => Path.Combine(_data, "ModConfig", name);

    private void WriteConfig(string name, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Config(name))!);
        File.WriteAllText(Config(name), text);
    }

    private static Dictionary<string, JsonObject> Declare(string file, string json) =>
        new() { [file] = (JsonNode.Parse(json) as JsonObject)! };

    private IReadOnlyList<ModConfigChange> Apply(Dictionary<string, JsonObject>? declared) =>
        ModConfigFiles.Apply(_data, declared);

    // ---- reading ----

    [Fact]
    public void The_values_come_out_with_the_types_they_are_written_in()
    {
        WriteConfig("seafarer.yaml", Seafarer);

        var settings = ModConfigSurvey.Read(_data, null, includeUnchanged: true)
            .ToDictionary(s => s.Key, s => s.Current);

        Assert.True(settings["drying-enable-rain-rot"]!.GetValue<bool>());
        Assert.Equal(2, settings["drying-rain-rot-multiplier"]!.GetValue<long>());
        Assert.Equal("", settings["Whitelist"]!.GetValue<string>());
        Assert.Equal(1, settings["version"]!.GetValue<long>());
    }

    /// <summary>
    /// This is not a YAML parser and must never become one. Anything outside the generated
    /// shape is refused, because the cost of refusing is one mod's settings and the cost of
    /// guessing is the config of a mod somebody is playing.
    /// </summary>
    [Theory]
    [InlineData("settings:\n  nested: true\n")]
    [InlineData("items:\n  - one\n  - two\n")]
    [InlineData("inline: { a: 1 }\n")]
    [InlineData("list: [1, 2]\n")]
    [InlineData("block: |\n  some text\n")]
    [InlineData("dupe: 1\ndupe: 2\n")]
    public void Anything_that_is_not_flat_scalars_is_refused(string yaml)
    {
        WriteConfig("x.yaml", yaml);

        var refused = Assert.Single(Apply(Declare("x.yaml", """{ "dupe": 3 }""")));
        Assert.Equal(ModConfigOutcome.Refused, refused.Outcome);

        // And left exactly as it was.
        Assert.Equal(yaml, File.ReadAllText(Config("x.yaml")));
    }

    // ---- writing ----

    /// <summary>
    /// The whole point of editing by line: ConfigLib regenerates this file on its next load,
    /// but between Cairn writing it and the game running, what is on disk is what a person
    /// opening it sees. A launcher that reformats somebody's config to change one number has
    /// done more than it said it would.
    /// </summary>
    [Fact]
    public void Setting_a_value_changes_that_value_and_nothing_else()
    {
        WriteConfig("seafarer.yaml", Seafarer);

        Apply(Declare("seafarer.yaml", """{ "drying-rain-rot-multiplier": 5 }"""));

        Assert.Equal(
            Seafarer.Replace("drying-rain-rot-multiplier: 2", "drying-rain-rot-multiplier: 5"),
            File.ReadAllText(Config("seafarer.yaml")));
    }

    [Theory]
    [InlineData("""{ "drying-enable-rain-rot": false }""", "drying-enable-rain-rot: false")]
    [InlineData("""{ "drying-rain-rot-multiplier": 1.5 }""", "drying-rain-rot-multiplier: 1.5")]
    [InlineData("""{ "Whitelist": "game:soil-*" }""", "Whitelist: 'game:soil-*'")]
    public void Values_are_written_the_way_ConfigLib_writes_them(string patch, string expected)
    {
        WriteConfig("seafarer.yaml", Seafarer);

        Apply(Declare("seafarer.yaml", patch));

        Assert.Contains(expected + "\n", File.ReadAllText(Config("seafarer.yaml")));
    }

    [Fact]
    public void An_apostrophe_in_a_value_is_escaped_the_way_YAML_wants()
    {
        WriteConfig("x.yaml", "version: 1\nname: 'plain'\n");

        Apply(Declare("x.yaml", """{ "name": "it's here" }"""));

        Assert.Equal("version: 1\nname: 'it''s here'\n", File.ReadAllText(Config("x.yaml")));
    }

    [Fact]
    public void Windows_line_endings_survive()
    {
        WriteConfig("x.yaml", "version: 1\r\n# a note\r\nvalue: 1\r\n");

        Apply(Declare("x.yaml", """{ "value": 2 }"""));

        Assert.Equal("version: 1\r\n# a note\r\nvalue: 2\r\n", File.ReadAllText(Config("x.yaml")));
    }

    [Fact]
    public void A_trailing_comment_on_the_same_line_is_kept()
    {
        WriteConfig("x.yaml", "version: 1\nvalue: 1 # hand-written\n");

        Apply(Declare("x.yaml", """{ "value": 7 }"""));

        Assert.Equal("version: 1\nvalue: 7 # hand-written\n", File.ReadAllText(Config("x.yaml")));
    }

    // ---- the two rules ConfigLib's own code forces ----

    /// <summary>
    /// ConfigLib rebuilds these files from its own settings when it saves — unlike its JSON
    /// path, which merges with what is on disk — so a key it does not recognise is deleted on
    /// the next load. Writing one would be a setting that appears to work and never does.
    /// </summary>
    [Fact]
    public void A_setting_the_mod_does_not_have_is_reported_rather_than_added()
    {
        WriteConfig("seafarer.yaml", Seafarer);

        var missing = Assert.Single(Apply(Declare("seafarer.yaml", """{ "notASetting": true }""")));

        Assert.Equal(ModConfigOutcome.Missing, missing.Outcome);
        Assert.Equal("seafarer.yaml: notASetting is not a setting this mod has", missing.Describe());
        Assert.Equal(Seafarer, File.ReadAllText(Config("seafarer.yaml")));
    }

    /// <summary>
    /// Config.Parse compares this against the version in the mod's own patch file, and on a
    /// mismatch does not merely decline the file — it writes the mod's defaults over every
    /// setting in it. A pack that could set this would be a pack that could wipe a config.
    /// </summary>
    [Fact]
    public void The_version_line_is_refused_and_never_written()
    {
        WriteConfig("seafarer.yaml", Seafarer);

        var changes = Apply(Declare("seafarer.yaml", """{ "version": 99, "drying-enable-rain-rot": false }"""));

        var refused = Assert.Single(changes, c => c.Outcome == ModConfigOutcome.Refused);
        Assert.Equal("version", refused.Key);
        Assert.Equal("modconfig-why-version", refused.Detail!.Key);

        // The rest of the patch still lands; one bad key costs one key.
        Assert.Contains("version: 1\n", File.ReadAllText(Config("seafarer.yaml")));
        Assert.Contains("drying-enable-rain-rot: false\n", File.ReadAllText(Config("seafarer.yaml")));
    }

    /// <summary>
    /// ConfigLib writes the whole file itself the first time the mod loads, and the version
    /// it puts at the top is the thing Cairn cannot know from outside. So this waits rather
    /// than inventing one — and the cost is exactly one session.
    /// </summary>
    [Fact]
    public void A_file_that_does_not_exist_yet_is_waited_for_rather_than_invented()
    {
        var refused = Assert.Single(Apply(Declare("seafarer.yaml", """{ "drying-enable-wind": false }""")));

        Assert.Equal(ModConfigOutcome.Refused, refused.Outcome);
        Assert.Equal("modconfig-why-not-yet", refused.Detail!.Key);
        Assert.False(File.Exists(Config("seafarer.yaml")));
    }

    /// <summary>
    /// The other half of the wait, and the half that was missing: the launch the refusal
    /// promises. Waiting one session is only a cost if the session after it collects.
    ///
    /// It did not. The refusal recorded the patch anyway, so the file ConfigLib wrote in
    /// between — holding nothing but the mod's own defaults — came back as a value differing
    /// from what the pack last asked for, which is the definition of an edit the player owns.
    /// Kept, on that launch and on every launch after it. A dedicated server following a pack
    /// simply never got the author's answer for any ConfigLib mod, and said "left alone" about
    /// settings nobody had touched.
    /// </summary>
    [Fact]
    public void The_launch_after_the_wait_is_the_one_that_collects()
    {
        var declared = Declare("seafarer.yaml", """{ "drying-rain-rot-multiplier": 5 }""");

        Assert.Equal(ModConfigOutcome.Refused, Assert.Single(Apply(declared)).Outcome);

        // The session in between: the mod loads for the first time and ConfigLib writes the
        // file, with the mod's defaults in it and no idea the pack ever asked for anything.
        WriteConfig("seafarer.yaml", Seafarer);

        var applied = Assert.Single(Apply(declared));

        Assert.Equal(ModConfigOutcome.Applied, applied.Outcome);
        Assert.Contains("drying-rain-rot-multiplier: 5\n", File.ReadAllText(Config("seafarer.yaml")));
    }

    /// <summary>
    /// Waiting is not the pack giving the setting up. Nothing is recorded for a file that is
    /// not there, and the report of what the pack has stopped asking for reads the same
    /// record — so the gap must not be mistaken for a key dropped from the manifest and
    /// announced as released to somebody who is still waiting for it.
    /// </summary>
    [Fact]
    public void A_config_file_deleted_between_launches_is_waited_for_rather_than_released()
    {
        var declared = Declare("seafarer.yaml", """{ "drying-rain-rot-multiplier": 5 }""");

        WriteConfig("seafarer.yaml", Seafarer);
        Assert.Equal(ModConfigOutcome.Applied, Assert.Single(Apply(declared)).Outcome);

        // The mod is removed from the pack's Mods folder, or the admin clears ModConfig out.
        File.Delete(Config("seafarer.yaml"));

        var refused = Assert.Single(Apply(declared));
        Assert.Equal(ModConfigOutcome.Refused, refused.Outcome);
        Assert.Equal("modconfig-why-not-yet", refused.Detail!.Key);

        // And the wait still collects, rather than the file coming back to a record that
        // says the pack already had its say about these keys.
        WriteConfig("seafarer.yaml", Seafarer);
        Assert.Equal(ModConfigOutcome.Applied, Assert.Single(Apply(declared)).Outcome);
    }

    // ---- and everything else behaves as it does for JSON ----

    /// <summary>
    /// A number read out of YAML is a long; the same number read out of the manifest is a
    /// JSON element. If those did not compare equal, every launch would rewrite the file and
    /// report a change nobody made — and the file's own mtime is what ConfigLib's file
    /// watcher reacts to.
    /// </summary>
    [Fact]
    public void A_second_launch_writes_nothing_and_says_nothing()
    {
        WriteConfig("seafarer.yaml", Seafarer);
        var declared = Declare("seafarer.yaml", """{ "drying-rain-rot-multiplier": 5 }""");

        Assert.Single(Apply(declared));

        var after = File.ReadAllText(Config("seafarer.yaml"));
        var written = File.GetLastWriteTimeUtc(Config("seafarer.yaml"));

        Assert.Empty(Apply(declared));
        Assert.Equal(after, File.ReadAllText(Config("seafarer.yaml")));
        Assert.Equal(written, File.GetLastWriteTimeUtc(Config("seafarer.yaml")));
    }

    [Fact]
    public void A_value_changed_after_Cairn_wrote_it_is_still_the_players()
    {
        WriteConfig("seafarer.yaml", Seafarer);
        Apply(Declare("seafarer.yaml", """{ "drying-rain-rot-multiplier": 5 }"""));

        // The player, through ConfigLib's own settings screen.
        WriteConfig("seafarer.yaml", Seafarer.Replace("multiplier: 2", "multiplier: 9"));

        var kept = Assert.Single(Apply(Declare("seafarer.yaml", """{ "drying-rain-rot-multiplier": 5 }""")));

        Assert.Equal(ModConfigOutcome.Kept, kept.Outcome);
        Assert.Contains("multiplier: 9", File.ReadAllText(Config("seafarer.yaml")));
    }

    [Fact]
    public void A_yaml_path_is_allowed_in_a_manifest_and_an_ini_is_not()
    {
        var manifest = new PackManifest { Id = "anego", GameVersion = "1.22.5" };

        manifest.ModConfig = Declare("seafarer.yaml", """{ "v": 1 }""");
        Assert.Empty(manifest.ModConfigProblems());

        manifest.ModConfig = Declare("imgui.ini", """{ "v": 1 }""");
        Assert.Contains(".json and .yaml", Assert.Single(manifest.ModConfigProblems()));
    }

    [Fact]
    public void The_tab_offers_a_yaml_setting_like_any_other()
    {
        WriteConfig("seafarer.yaml", Seafarer);
        ModConfigFiles.Capture(_data);

        WriteConfig("seafarer.yaml", Seafarer.Replace("rain-rot: true", "rain-rot: false"));

        var changed = Assert.Single(ModConfigSurvey.Read(_data, null));
        Assert.Equal("drying-enable-rain-rot", changed.Key);
        Assert.Equal("true", changed.BaselineText);
        Assert.Equal("false", changed.CurrentText);

        var carried = ModConfigSurvey.ToManifest([changed])!;
        Assert.False(carried["seafarer.yaml"]["drying-enable-rain-rot"]!.GetValue<bool>());
    }
}
