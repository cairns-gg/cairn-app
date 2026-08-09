using System.Text.Json;
using System.Text.Json.Nodes;
using Cairn.Core.Launch;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// A pack loads its own mods and nobody else's.
///
/// It did not, for a while, and the reason was three steps apart: a new pack's settings are
/// seeded from the player's own clientsettings.json, that file records mod directories as
/// absolute paths, and <c>--addModPath</c> adds to that list rather than replacing it. So a
/// mod installed in plain Vintage Story and then added to a pack loaded twice — the failure
/// somebody reported as "I get two copies of Olla".
///
/// The per-pack data path does not cover this: the leaked entry is a literal path written
/// into the settings, not one the game derives from <c>--dataPath</c> at startup.
/// </summary>
public class ModPathLeakTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-modpaths-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly PackStore _store;
    private readonly PackData _data;

    private string SharedData => Path.Combine(_root, "shared");

    public ModPathLeakTests()
    {
        _store = new PackStore(Path.Combine(_root, "packs"));
        _data = new PackData(_store, Path.Combine(_root, "session.json"), SharedData);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static string SettingsIn(string dataPath) => Path.Combine(dataPath, "clientsettings.json");

    /// <summary>
    /// Settings shaped like the game's, with the mod paths where the game keeps them.
    /// The relative "Mods" and an absolute second entry is exactly what a real
    /// clientsettings.json holds after plain Vintage Story has run once.
    /// </summary>
    private static void WriteSettings(string dataPath, params string[] modPaths)
    {
        Directory.CreateDirectory(dataPath);

        var lists = new JsonObject { ["disabledMods"] = new JsonArray("someothermod") };

        if (modPaths.Length > 0)
            lists["modPaths"] = new JsonArray([.. modPaths.Select(p => (JsonNode)JsonValue.Create(p))]);

        var root = new JsonObject
        {
            ["stringSettings"] = new JsonObject { ["playername"] = "dizzyd" },
            ["intSettings"] = new JsonObject { ["viewDistance"] = 256 },
            ["stringListSettings"] = lists,
        };

        File.WriteAllText(SettingsIn(dataPath),
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static List<string> ModPathsIn(string dataPath)
    {
        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(SettingsIn(dataPath)))!;
        var paths = root["stringListSettings"]?["modPaths"] as JsonArray;

        return paths is null ? [] : [.. paths.Select(p => p!.GetValue<string>())];
    }

    private string OwnMods(string id) => Path.Combine(_store.DataDir(id), "Mods");

    private string PlayersOwnMods => Path.Combine(SharedData, "Mods");

    [Fact]
    public void A_new_pack_does_not_inherit_the_players_own_mods_folder()
    {
        WriteSettings(SharedData, "Mods", PlayersOwnMods);

        _store.Create("anego", "1.22.5");
        _data.BeforeLaunch("anego");

        // The game's own Mods directory holds VSSurvivalMod and friends, so it stays. The
        // player's does not: that is the doubling.
        Assert.Equal(["Mods", OwnMods("anego")], ModPathsIn(_store.DataDir("anego")));
    }

    [Fact]
    public void A_pack_seeded_before_this_existed_is_repaired_on_launch()
    {
        // Written straight into the pack, as a pack created by an older Cairn looks: the
        // seed already happened, and nothing but a launch reaches into it afterwards.
        _store.Create("anego", "1.22.5");
        WriteSettings(_store.DataDir("anego"), "Mods", PlayersOwnMods);

        var dropped = _data.BeforeLaunch("anego");

        Assert.Equal(["Mods", OwnMods("anego")], ModPathsIn(_store.DataDir("anego")));

        // Returned rather than swallowed: this launch has fewer mods in it than the last
        // one, and that is not a thing to discover in-game.
        Assert.Equal([PlayersOwnMods], dropped);
    }

    [Fact]
    public void A_directory_inside_the_pack_is_kept()
    {
        var extra = Path.Combine(_store.DataDir("anego"), "ExtraMods");

        _store.Create("anego", "1.22.5");
        WriteSettings(_store.DataDir("anego"), "Mods", extra, PlayersOwnMods);

        var dropped = _data.BeforeLaunch("anego");

        // Somebody put it there, and it is still the pack's own. Only paths that reach
        // outside the pack are the bug.
        Assert.Contains(extra, ModPathsIn(_store.DataDir("anego")));
        Assert.Equal([PlayersOwnMods], dropped);
    }

    [Fact]
    public void Settings_that_name_no_mod_paths_are_given_the_packs_own()
    {
        // Not left to the game's default. Launched with --dataPath into a pack that had
        // never been played, it logged the player's own Mods folder among the paths it
        // would search, and not the pack's data-path Mods at all.
        _store.Create("anego", "1.22.5");
        WriteSettings(_store.DataDir("anego"));

        _data.BeforeLaunch("anego");

        Assert.Equal(["Mods", OwnMods("anego")], ModPathsIn(_store.DataDir("anego")));
    }

    [Fact]
    public void A_pack_with_no_settings_at_all_still_gets_them()
    {
        // Nothing to seed from either — the player has never run plain Vintage Story.
        _store.Create("anego", "1.22.5");

        _data.BeforeLaunch("anego");

        Assert.Equal(["Mods", OwnMods("anego")], ModPathsIn(_store.DataDir("anego")));
    }

    [Fact]
    public void A_pack_created_through_the_store_is_still_seeded()
    {
        // PackStore.Create makes the data directory to mark that the pack has its own data
        // path. Keying the seed off that directory meant every pack created through the
        // launcher skipped it and started from bare defaults.
        WriteSettings(SharedData, "Mods", PlayersOwnMods);

        _store.Create("anego", "1.22.5");
        _data.BeforeLaunch("anego");

        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(SettingsIn(_store.DataDir("anego"))))!;
        Assert.Equal(256, root["intSettings"]!["viewDistance"]!.GetValue<int>());
    }

    [Fact]
    public void Nothing_else_in_the_settings_moves()
    {
        _store.Create("anego", "1.22.5");
        WriteSettings(_store.DataDir("anego"), "Mods", PlayersOwnMods);

        _data.BeforeLaunch("anego");

        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(SettingsIn(_store.DataDir("anego"))))!;

        Assert.Equal(256, root["intSettings"]!["viewDistance"]!.GetValue<int>());
        Assert.Equal("someothermod",
            ((JsonArray)root["stringListSettings"]!["disabledMods"]!)[0]!.GetValue<string>());
    }

    [Fact]
    public void A_pack_that_is_already_right_is_not_rewritten()
    {
        // The game reads this file at startup and writes it at exit. A launcher touching it
        // in between should be able to change nothing at all.
        _store.Create("anego", "1.22.5");
        WriteSettings(_store.DataDir("anego"), "Mods", OwnMods("anego"));

        var settings = SettingsIn(_store.DataDir("anego"));
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(settings, stamp);

        _data.BeforeLaunch("anego");

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(settings));
    }

    [Fact]
    public void A_settings_file_that_cannot_be_read_does_not_stop_a_launch()
    {
        _store.Create("anego", "1.22.5");
        Directory.CreateDirectory(_store.DataDir("anego"));
        File.WriteAllText(SettingsIn(_store.DataDir("anego")), "{ not json");

        Assert.Empty(_data.BeforeLaunch("anego"));
    }

    [Fact]
    public void The_same_directory_written_differently_is_not_added_twice()
    {
        _store.Create("anego", "1.22.5");

        // A trailing separator names the same directory as the entry that would be kept,
        // and must not become a second copy of it.
        WriteSettings(_store.DataDir("anego"),
            "Mods", OwnMods("anego") + Path.DirectorySeparatorChar, PlayersOwnMods);

        _data.BeforeLaunch("anego");

        Assert.Equal(["Mods", OwnMods("anego")], ModPathsIn(_store.DataDir("anego")));
    }
}
