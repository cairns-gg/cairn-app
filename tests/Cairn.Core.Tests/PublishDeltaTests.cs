using System.Text.Json.Nodes;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// What publishing would change about the revision already at a pack's address.
///
/// The share window otherwise says what the pack contains, which answers the question
/// somebody has on a first publish. After that the question is what is about to move — and a
/// pack its author has been playing for a month has moved in ways they will not remember: a
/// sync updated five mods, a setting was tuned in game, a hotkey was rebound.
///
/// Compared against what the site serves, because nothing local can answer it: the publish
/// record keeps a fingerprint of the document rather than the document.
/// </summary>
public class PublishDeltaTests
{
    private static PackBundle Bundle(
        PackManifest pack, params (string Id, string Version)[] locked) => new()
    {
        Pack = pack,
        Lock = new PackLock
        {
            GameVersion = pack.GameVersion,
            Mods = [.. locked.Select(m => new LockedMod { ModId = m.Id, Version = m.Version })],
        },
    };

    private static PackManifest Pack(params string[] mods) => new()
    {
        Id = "anego",
        GameVersion = "1.22.5",
        Mods = [.. mods.Select(m => new PackMod { ModId = m })],
    };

    private static PublishDelta Between(PackBundle was, PackBundle now) =>
        PublishDelta.Between(was, now);

    [Fact]
    public void A_pack_that_has_not_moved_has_nothing_to_say()
    {
        var was = Bundle(Pack("carryon"), ("carryon", "1.2.0"));
        var now = Bundle(Pack("carryon"), ("carryon", "1.2.0"));

        var delta = Between(was, now);

        Assert.False(delta.Anything);
        Assert.Equal("", delta.Describe());
    }

    [Fact]
    public void Mods_added_and_dropped_are_counted()
    {
        var was = Bundle(Pack("carryon", "scribe"), ("carryon", "1.2.0"), ("scribe", "1.1.1"));
        var now = Bundle(Pack("carryon", "betterruins"), ("carryon", "1.2.0"), ("betterruins", "0.5.0"));

        var delta = Between(was, now);

        Assert.Equal(1, delta.ModsAdded);
        Assert.Equal(1, delta.ModsRemoved);
        Assert.Contains("1 mod added", delta.Describe());
        Assert.Contains("1 removed", delta.Describe());
    }

    /// <summary>
    /// A version that moved in the lockfile alone, which is what a pure mod-update publish
    /// is and what somebody is least likely to remember doing. The manifests are identical
    /// here; a comparison of those alone would report nothing at all, which is the bug that
    /// made every such revision invisible to followers.
    /// </summary>
    [Fact]
    public void A_version_that_moved_only_in_the_lock_is_counted()
    {
        var was = Bundle(Pack("scribe"), ("scribe", "1.1.1"));
        var now = Bundle(Pack("scribe"), ("scribe", "1.2.1"));

        var delta = Between(was, now);

        Assert.Equal(1, delta.ModsMoved);
        Assert.Contains("1 at a different version", delta.Describe());
    }

    [Fact]
    public void Mod_settings_are_counted_by_value_rather_than_by_file()
    {
        var was = Pack("carryon");
        was.ModConfig = new Dictionary<string, JsonObject>
        {
            ["terrainslabs.json"] = (JsonNode.Parse("""{"compatibleMods":[],"enableSlabs":true}""") as JsonObject)!,
        };

        var now = Pack("carryon");
        now.ModConfig = new Dictionary<string, JsonObject>
        {
            // One changed, one unchanged, and one that has appeared.
            ["terrainslabs.json"] = (JsonNode.Parse(
                """{"compatibleMods":["footprints"],"enableSlabs":true}""") as JsonObject)!,
            ["BedSpawn.json"] = (JsonNode.Parse("""{"Rooms":{"Enabled":true}}""") as JsonObject)!,
        };

        var delta = Between(Bundle(was), Bundle(now));

        Assert.Equal(2, delta.SettingsChanged);
        Assert.Contains("2 mod settings changed", delta.Describe());
    }

    /// <summary>A setting that goes counts as a change: from the reader's side it is one.</summary>
    [Fact]
    public void A_setting_the_pack_stops_carrying_counts_too()
    {
        var was = Pack("carryon");
        was.ModConfig = new Dictionary<string, JsonObject>
        {
            ["terrainslabs.json"] = (JsonNode.Parse("""{"compatibleMods":["footprints"]}""") as JsonObject)!,
        };

        var delta = Between(Bundle(was), Bundle(Pack("carryon")));

        Assert.Equal(1, delta.SettingsChanged);
    }

    [Fact]
    public void Hotkeys_the_server_address_and_the_game_version_are_all_noticed()
    {
        var was = Pack("carryon");
        was.Keybinds = new Dictionary<string, string> { ["walk"] = "W" };

        var now = Pack("carryon");
        now.Keybinds = new Dictionary<string, string> { ["walk"] = "K", ["jump"] = "SPACE" };
        now.Connect = "play.example:42420";
        now.GameVersion = "1.22.6";

        var delta = Between(Bundle(was), Bundle(now));

        Assert.Equal(2, delta.HotkeysChanged);
        Assert.True(delta.ConnectChanged);
        Assert.True(delta.GameVersionChanged);

        var text = delta.Describe();
        Assert.Contains("2 hotkeys changed", text);
        Assert.Contains("the server address", text);
        Assert.Contains("game 1.22.5 → 1.22.6", text);
    }

    /// <summary>
    /// A renamed pack, or a rewritten description. Left out at first, which put "nothing has
    /// changed" on the same screen as an enabled Publish button — the document knew, and the
    /// summary was reading a shorter list than the document is.
    /// </summary>
    [Fact]
    public void Renaming_the_pack_is_a_change()
    {
        var was = Pack("carryon");
        was.Name = "Anego Server";

        var now = Pack("carryon");
        now.Name = "Anego Server (hardcore)";

        var delta = Between(Bundle(was), Bundle(now));

        Assert.True(delta.DetailsChanged);
        Assert.True(delta.Anything);
        Assert.Contains("name or description", delta.Describe());
    }

    [Fact]
    public void So_is_rewriting_the_description()
    {
        var was = Pack("carryon");
        var now = Pack("carryon");
        now.Description = "Now with more bees.";

        Assert.True(Between(Bundle(was), Bundle(now)).DetailsChanged);
    }

    /// <summary>
    /// What the lockfile records about a mod besides its version — which release it is on
    /// ModDB, which file, which side it runs on. Nobody chooses these; a sync fills them in.
    ///
    /// Named because a revision published before Cairn recorded them differs from one
    /// published after, which is a real difference on a real pack — and "something has
    /// changed" is the answer that helps least.
    /// </summary>
    [Fact]
    public void What_the_lockfile_records_about_a_download_is_named()
    {
        var was = new PackBundle
        {
            Pack = Pack("scribe"),
            Lock = new PackLock
            {
                GameVersion = "1.22.5",
                Mods = [new LockedMod { ModId = "scribe", Version = "1.2.1" }],
            },
        };

        var now = new PackBundle
        {
            Pack = Pack("scribe"),
            Lock = new PackLock
            {
                GameVersion = "1.22.5",
                Mods =
                [
                    new LockedMod
                    {
                        ModId = "scribe", Version = "1.2.1",
                        ReleaseId = 50887, FileId = 110599, Side = "both",
                    },
                ],
            },
        };

        var delta = Between(was, now);

        Assert.True(delta.DownloadsChanged);
        Assert.Equal(0, delta.ModsMoved);
        Assert.Contains("lockfile records", delta.Describe());
    }

    /// <summary>
    /// A version that moved is not counted twice. It is already the mods-moved count, and
    /// naming it again would read as two separate things having happened.
    /// </summary>
    [Fact]
    public void A_version_that_moved_is_not_also_reported_as_a_download_change()
    {
        var was = Bundle(Pack("scribe"), ("scribe", "1.1.1"));
        var now = Bundle(Pack("scribe"), ("scribe", "1.2.1"));

        var delta = Between(was, now);

        Assert.Equal(1, delta.ModsMoved);
        Assert.False(delta.DownloadsChanged);
    }

    /// <summary>And a mod only one side has is the added or removed count, not this.</summary>
    [Fact]
    public void A_mod_only_one_side_has_is_not_a_download_change()
    {
        var was = Bundle(Pack("scribe"), ("scribe", "1.2.1"));
        var now = Bundle(Pack("scribe", "carryon"), ("scribe", "1.2.1"), ("carryon", "1.0.0"));

        var delta = Between(was, now);

        Assert.Equal(1, delta.ModsAdded);
        Assert.False(delta.DownloadsChanged);
    }
}
