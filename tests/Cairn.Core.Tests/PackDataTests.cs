using System.Text.Json;
using System.Text.Json.Nodes;
using Cairn.Core.Launch;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Packs get their own worlds and mod configs, but not their own login.
///
/// They used to share one data path so there would be one login — which also meant one
/// set of worlds, reachable from every pack whatever its mods. Carrying just the session
/// keeps the login and drops the sharing.
/// </summary>
public class PackDataTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-packdata-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly PackStore _store;
    private readonly PackData _data;

    private string SharedData => Path.Combine(_root, "shared");
    private string SessionPath => Path.Combine(_root, "session.json");

    public PackDataTests()
    {
        _store = new PackStore(Path.Combine(_root, "packs"));
        _data = new PackData(_store, SessionPath, SharedData);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static string SettingsIn(string dataPath) => Path.Combine(dataPath, "clientsettings.json");

    /// <summary>A settings file shaped like the game's: typed buckets, auth in stringSettings.</summary>
    private static void WriteSettings(
        string dataPath, string? sessionKey = null, string? keybind = null, int? renderDistance = null)
    {
        Directory.CreateDirectory(dataPath);

        var strings = new JsonObject { ["playername"] = "dizzyd" };
        if (sessionKey is not null)
        {
            strings["sessionkey"] = sessionKey;
            strings["sessionsignature"] = "sig-" + sessionKey;
            strings["playeruid"] = "uid-" + sessionKey;
        }

        var root = new JsonObject
        {
            ["stringSettings"] = strings,
            ["intSettings"] = new JsonObject { ["viewDistance"] = renderDistance ?? 256 },
            ["keyMapping"] = new JsonObject { ["walkforward"] = keybind ?? "W" },
        };

        File.WriteAllText(SettingsIn(dataPath), root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static JsonObject ReadSettings(string dataPath) =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(SettingsIn(dataPath)))!;

    private PackManifest NewPack(string id) => _store.Create(id, "1.22.5");

    // ---- which data path a pack launches with ----

    [Fact]
    public void A_new_pack_gets_its_own_data_path()
    {
        NewPack("anego");

        Assert.True(_data.HasOwnData("anego"));
        Assert.Equal(_store.DataDir("anego"), _data.DataPathFor("anego"));
    }

    [Fact]
    public void A_pack_without_one_still_uses_the_shared_path()
    {
        // An existing pack, from before packs had their own data.
        NewPack("legacy");
        Directory.Delete(_store.DataDir("legacy"), recursive: true);

        Assert.False(_data.HasOwnData("legacy"));
        Assert.Equal(SharedData, _data.DataPathFor("legacy"));
    }

    [Fact]
    public void Editing_a_pack_does_not_silently_move_it_off_the_shared_path()
    {
        NewPack("legacy");
        Directory.Delete(_store.DataDir("legacy"), recursive: true);

        // The directory is the flag, so anything that recreated it would migrate someone's
        // worlds without asking. Saving a manifest must not.
        var manifest = _store.Load("legacy");
        manifest.Name = "Renamed";
        _store.Save(manifest);

        Assert.False(_data.HasOwnData("legacy"));
    }

    [Fact]
    public void Opting_in_creates_the_directory_and_seeds_settings_once()
    {
        NewPack("legacy");
        Directory.Delete(_store.DataDir("legacy"), recursive: true);
        WriteSettings(SharedData, sessionKey: "abc", keybind: "Z", renderDistance: 512);

        _data.EnableOwnData("legacy");

        Assert.True(_data.HasOwnData("legacy"));

        // Seeded from what the player already uses, so a first launch is not bare.
        var seeded = ReadSettings(_store.DataDir("legacy"));
        Assert.Equal("Z", seeded["keyMapping"]!["walkforward"]!.GetValue<string>());
        Assert.Equal(512, seeded["intSettings"]!["viewDistance"]!.GetValue<int>());
    }

    [Fact]
    public void Asking_where_a_pack_launches_writes_nothing()
    {
        // cairn-cli launch --dry-run prints the path without starting anything, so
        // resolving it must not create or touch a file.
        WriteSettings(SharedData, sessionKey: "abc");
        NewPack("anego");

        var before = Directory.GetFileSystemEntries(
            _store.DataDir("anego"), "*", SearchOption.AllDirectories).Length;

        _ = _data.DataPathFor("anego");
        _ = _data.HasOwnData("anego");

        var after = Directory.GetFileSystemEntries(
            _store.DataDir("anego"), "*", SearchOption.AllDirectories).Length;

        Assert.Equal(before, after);
    }

    // ---- carrying the login ----

    [Fact]
    public void The_login_reaches_a_pack_that_has_never_been_launched()
    {
        WriteSettings(SharedData, sessionKey: "abc");
        NewPack("anego");

        _data.BeforeLaunch("anego");

        var settings = ReadSettings(_store.DataDir("anego"));
        Assert.Equal("abc", settings["stringSettings"]!["sessionkey"]!.GetValue<string>());
        Assert.Equal("sig-abc", settings["stringSettings"]!["sessionsignature"]!.GetValue<string>());
    }

    [Fact]
    public void Merging_the_login_leaves_every_other_setting_alone()
    {
        WriteSettings(SharedData, sessionKey: "abc");

        NewPack("anego");
        var pack = _store.DataDir("anego");

        // This pack has its own keybind and its own render distance.
        WriteSettings(pack, keybind: "Q", renderDistance: 128);

        _data.BeforeLaunch("anego");

        var settings = ReadSettings(pack);

        // The login arrived...
        Assert.Equal("abc", settings["stringSettings"]!["sessionkey"]!.GetValue<string>());

        // ...and nothing else moved. This is the whole reason for merging named keys
        // rather than copying the file.
        Assert.Equal("Q", settings["keyMapping"]!["walkforward"]!.GetValue<string>());
        Assert.Equal(128, settings["intSettings"]!["viewDistance"]!.GetValue<int>());
    }

    [Fact]
    public void A_login_made_inside_one_pack_reaches_the_next()
    {
        NewPack("first");
        NewPack("second");

        // Signed in while playing "first".
        WriteSettings(_store.DataDir("first"), sessionKey: "fresh");

        _data.BeforeLaunch("second");

        Assert.Equal("fresh",
            ReadSettings(_store.DataDir("second"))["stringSettings"]!["sessionkey"]!.GetValue<string>());
    }

    [Fact]
    public void A_rotated_session_wins_over_an_older_record()
    {
        WriteSettings(SharedData, sessionKey: "old");
        NewPack("anego");
        _data.BeforeLaunch("anego");

        // The game rotates the session while playing; that copy is newer.
        Thread.Sleep(10);
        WriteSettings(_store.DataDir("anego"), sessionKey: "rotated");

        _data.AfterExit("anego");

        NewPack("other");
        _data.BeforeLaunch("other");

        Assert.Equal("rotated",
            ReadSettings(_store.DataDir("other"))["stringSettings"]!["sessionkey"]!.GetValue<string>());
    }

    [Fact]
    public void A_stale_copy_never_overwrites_a_newer_login()
    {
        NewPack("stale");
        NewPack("fresh");

        WriteSettings(_store.DataDir("stale"), sessionKey: "old");
        Thread.Sleep(10);
        WriteSettings(_store.DataDir("fresh"), sessionKey: "new");

        _data.CaptureLatest();

        // Newest-wins by timestamp; first-found would have signed the player out.
        Assert.Equal("new", ClientSession.Load(SessionPath).Values["sessionkey"]);
    }

    [Fact]
    public void Cairn_never_writes_to_the_players_own_data_path()
    {
        WriteSettings(SharedData, sessionKey: "abc");
        var before = File.GetLastWriteTimeUtc(SettingsIn(SharedData));
        var original = File.ReadAllText(SettingsIn(SharedData));

        NewPack("anego");
        _data.BeforeLaunch("anego");
        _data.AfterExit("anego");

        Assert.Equal(original, File.ReadAllText(SettingsIn(SharedData)));
        Assert.Equal(before, File.GetLastWriteTimeUtc(SettingsIn(SharedData)));
    }

    // ---- never fail a launch over settings ----

    [Fact]
    public void A_missing_settings_file_is_written_rather_than_fatal()
    {
        WriteSettings(SharedData, sessionKey: "abc");
        NewPack("anego");

        // No clientsettings.json in the pack at all.
        _data.BeforeLaunch("anego");

        Assert.True(File.Exists(SettingsIn(_store.DataDir("anego"))));
        Assert.Equal("abc",
            ReadSettings(_store.DataDir("anego"))["stringSettings"]!["sessionkey"]!.GetValue<string>());
    }

    [Fact]
    public void A_corrupt_settings_file_does_not_stop_a_launch()
    {
        WriteSettings(SharedData, sessionKey: "abc");
        NewPack("anego");
        File.WriteAllText(SettingsIn(_store.DataDir("anego")), "{ not json at all");

        _data.BeforeLaunch("anego");   // must not throw

        Assert.Equal("abc",
            ReadSettings(_store.DataDir("anego"))["stringSettings"]!["sessionkey"]!.GetValue<string>());
    }

    [Fact]
    public void No_login_anywhere_is_not_an_error()
    {
        NewPack("anego");
        _data.BeforeLaunch("anego");   // nothing to merge; the game will just ask
    }

    // ---- what deleting costs ----

    [Fact]
    public void A_pack_reports_the_worlds_that_would_go_with_it()
    {
        NewPack("anego");
        var saves = Path.Combine(_store.DataDir("anego"), "Saves");
        Directory.CreateDirectory(saves);
        File.WriteAllBytes(Path.Combine(saves, "My World.vcdbs"), new byte[2048]);
        File.WriteAllBytes(Path.Combine(saves, "Second.vcdbs"), new byte[1024]);

        Assert.Equal(2, _data.Worlds("anego").Count);
        Assert.Contains("My World", _data.Worlds("anego"));
        Assert.True(_data.DataSize("anego") >= 3072);
    }

    [Fact]
    public void A_pack_on_the_shared_path_reports_no_worlds_of_its_own()
    {
        NewPack("legacy");
        Directory.Delete(_store.DataDir("legacy"), recursive: true);

        // Its worlds are the shared ones, and deleting the pack will not touch them.
        Assert.Empty(_data.Worlds("legacy"));
        Assert.Equal(0, _data.DataSize("legacy"));
    }

    [Fact]
    public void Deleting_a_pack_takes_its_data_with_it()
    {
        NewPack("anego");
        var data = _store.DataDir("anego");
        Directory.CreateDirectory(Path.Combine(data, "Saves"));

        _store.Delete("anego");

        Assert.False(Directory.Exists(data));
    }
}
