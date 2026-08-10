using System.Text.Json;
using System.Text.Json.Nodes;
using Cairn.Core.Hotkeys;
using Cairn.Core.Launch;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Reading and writing key combinations. The numbers are the game's and are not ASCII —
/// A is 83, Backspace is 53 — so every one of these is a fact about the game rather than
/// an arrangement of our own.
/// </summary>
public class KeyBindingTests
{
    [Theory]
    [InlineData("W", 105)]
    [InlineData("BackSpace", 53)]        // the alias
    [InlineData("Back", 53)]             // and the name the enum lists first
    [InlineData("Space", 51)]
    [InlineData("F1", 10)]
    [InlineData("Number0", 109)]
    public void Keys_are_read_by_the_names_the_game_uses(string name, int code)
    {
        Assert.Equal(code, KeyBinding.Parse(name)!.KeyCode);
    }

    [Fact]
    public void Modifiers_round_trip_in_a_fixed_order()
    {
        var parsed = KeyBinding.Parse("Shift+Ctrl+P")!;

        Assert.True(parsed is { Ctrl: true, Shift: true, Alt: false });

        // Written back in the canonical order, so saving a pack twice does not report a
        // change nobody made.
        Assert.Equal("Ctrl-Shift-P", parsed.ToString());
    }

    [Fact]
    public void A_two_key_combination_keeps_its_tail()
    {
        var parsed = KeyBinding.Parse("Ctrl+K,M")!;

        Assert.Equal(KeyBinding.Parse("M")!.KeyCode, parsed.SecondKeyCode);
        Assert.Equal("Ctrl-K,M", parsed.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Nonsense")]
    [InlineData("K+M")]                  // two keys with no comma is not a thing the game has
    [InlineData("Ctrl+K,Nonsense")]
    public void Anything_it_cannot_name_is_refused_rather_than_guessed(string text)
    {
        // A binding written into somebody's settings as whatever the parse fell through to
        // is worse than a binding that did not arrive.
        Assert.Null(KeyBinding.Parse(text));
    }

    [Fact]
    public void The_json_matches_what_the_game_writes()
    {
        var json = KeyBinding.Parse("Ctrl+BackSpace")!.ToJson();

        // Property names are the game's, casing included: it deserialises into a type with
        // these members, and different ones would read back as no binding at all.
        Assert.Equal(53, json["KeyCode"]!.GetValue<int>());
        Assert.True(json["Ctrl"]!.GetValue<bool>());
        Assert.False(json["Shift"]!.GetValue<bool>());
        Assert.Null(json["SecondKeyCode"]);

        Assert.Equal(KeyBinding.Parse("Ctrl+BackSpace"), KeyBinding.FromJson(json));
    }

    [Fact]
    public void A_clash_is_the_same_press_and_not_merely_the_same_key()
    {
        var plain = KeyBinding.Parse("P")!;

        Assert.True(plain.Clashes(KeyBinding.Parse("P")!));
        Assert.False(plain.Clashes(KeyBinding.Parse("Ctrl+P")!));
    }
}

/// <summary>
/// Hotkeys arriving with a pack.
///
/// The rule this suite exists for: fill, never overwrite. A pack that reaches into a
/// keyboard somebody has already set up is one nobody installs twice.
/// </summary>
public class ClientHotkeyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-hotkeys-" + Guid.NewGuid().ToString("n")[..8]);

    private string Settings => Path.Combine(_root, "clientsettings.json");

    public ClientHotkeyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private JsonObject Read() =>
        (JsonNode.Parse(File.ReadAllText(Settings)) as JsonObject)!;

    [Fact]
    public void A_pack_binding_lands_in_a_file_that_has_none()
    {
        var bound = ClientHotkeys.Apply(Settings, new Dictionary<string, string>
        {
            ["scribepinhud"] = "Ctrl+P",
        });

        Assert.Equal(["scribepinhud"], bound);

        var mapping = (JsonObject)Read()["keyMapping"]!;
        Assert.Equal(98, mapping["scribepinhud"]!["KeyCode"]!.GetValue<int>());   // P
        Assert.True(mapping["scribepinhud"]!["Ctrl"]!.GetValue<bool>());
    }

    [Fact]
    public void A_binding_the_player_already_has_is_left_alone()
    {
        File.WriteAllText(Settings, """
        { "keyMapping": { "scribepinhud": { "KeyCode": 53, "Ctrl": false, "Alt": false, "Shift": false } } }
        """);

        var bound = ClientHotkeys.Apply(Settings, new Dictionary<string, string>
        {
            ["scribepinhud"] = "Ctrl+P",
            ["cateyesonoff"] = "L",
        });

        // Only the one they had no answer for. Their own binding is a decision, and it is
        // newer than the pack's.
        Assert.Equal(["cateyesonoff"], bound);
        Assert.Equal(53, ((JsonObject)Read()["keyMapping"]!)["scribepinhud"]!["KeyCode"]!.GetValue<int>());
    }

    [Fact]
    public void Everything_else_in_the_settings_survives()
    {
        File.WriteAllText(Settings, """
        { "stringSettings": { "language": "en" }, "intSettings": { "viewDistance": 256 } }
        """);

        ClientHotkeys.Apply(Settings, new Dictionary<string, string> { ["cateyesonoff"] = "L" });

        var root = Read();
        Assert.Equal("en", root["stringSettings"]!["language"]!.GetValue<string>());
        Assert.Equal(256, root["intSettings"]!["viewDistance"]!.GetValue<int>());
    }

    [Fact]
    public void A_combination_that_cannot_be_read_costs_one_binding_and_nothing_else()
    {
        var bound = ClientHotkeys.Apply(Settings, new Dictionary<string, string>
        {
            ["good"] = "Ctrl+K",
            ["typo"] = "Crtl+K",
        });

        Assert.Equal(["good"], bound);
        Assert.False(((JsonObject)Read()["keyMapping"]!).ContainsKey("typo"));
    }

    [Fact]
    public void Nothing_is_written_when_the_pack_declares_nothing()
    {
        Assert.Empty(ClientHotkeys.Apply(Settings, null));
        Assert.Empty(ClientHotkeys.Apply(Settings, new Dictionary<string, string>()));

        // Not even an empty file: the game reads this at startup and writes it at exit, and
        // a launcher touching it in between should be able to change nothing.
        Assert.False(File.Exists(Settings));
    }

    [Fact]
    public void Reading_back_gives_what_the_player_has_bound()
    {
        ClientHotkeys.Apply(Settings, new Dictionary<string, string> { ["a"] = "Ctrl+K,M" });

        var read = ClientHotkeys.Read(Settings);
        Assert.Equal("Ctrl-K,M", read["a"].ToString());
    }

    /// <summary>
    /// The pack's hotkeys arrive through the launch, which is the only thing that reaches
    /// into a pack's data directory.
    /// </summary>
    [Fact]
    public void A_launch_applies_the_packs_hotkeys()
    {
        var store = new PackStore(Path.Combine(_root, "packs"));
        var manifest = store.Create("anego", "1.22.5", "Anego");
        manifest.Keybinds = new Dictionary<string, string> { ["scribepinhud"] = "Ctrl+P" };
        manifest.Save(store.ManifestPath("anego"));

        var data = new PackData(store, Path.Combine(_root, "session.json"), Path.Combine(_root, "shared"));

        var bound = new List<string>();
        data.BeforeLaunch("anego", bound);

        Assert.Equal(["scribepinhud"], bound);

        var settings = JsonNode.Parse(
            File.ReadAllText(Path.Combine(store.DataDir("anego"), "clientsettings.json")))!;

        Assert.Equal(98, settings["keyMapping"]!["scribepinhud"]!["KeyCode"]!.GetValue<int>());
    }

    /// <summary>
    /// The bindings arrive whether or not the caller wanted to print about them.
    ///
    /// The reporting collection was briefly what switched the hotkey work on, so a launch
    /// that did not ask for the list quietly did not apply the pack's keyboard either. A
    /// front end that never printed the line would have been a front end where the feature
    /// silently did not exist.
    /// </summary>
    [Fact]
    public void A_launch_that_reports_nothing_still_applies_them()
    {
        var store = new PackStore(Path.Combine(_root, "packs"));
        var manifest = store.Create("anego", "1.22.5", "Anego");
        manifest.Keybinds = new Dictionary<string, string> { ["scribepinhud"] = "Ctrl-P" };
        manifest.Save(store.ManifestPath("anego"));

        var data = new PackData(store, Path.Combine(_root, "session.json"), Path.Combine(_root, "shared"));

        data.BeforeLaunch("anego");

        var settings = JsonNode.Parse(
            File.ReadAllText(Path.Combine(store.DataDir("anego"), "clientsettings.json")))!;

        Assert.Equal(98, settings["keyMapping"]!["scribepinhud"]!["KeyCode"]!.GetValue<int>());
    }

    /// <summary>
    /// A second launch says nothing, because there is nothing new to say — the bindings
    /// are already there, and a line per launch would be noise that trains people to skip
    /// the one that matters.
    /// </summary>
    [Fact]
    public void A_second_launch_binds_nothing_further()
    {
        var store = new PackStore(Path.Combine(_root, "packs"));
        var manifest = store.Create("anego", "1.22.5", "Anego");
        manifest.Keybinds = new Dictionary<string, string> { ["scribepinhud"] = "Ctrl-P" };
        manifest.Save(store.ManifestPath("anego"));

        var data = new PackData(store, Path.Combine(_root, "session.json"), Path.Combine(_root, "shared"));

        var first = new List<string>();
        data.BeforeLaunch("anego", first);
        Assert.Single(first);

        var second = new List<string>();
        data.BeforeLaunch("anego", second);
        Assert.Empty(second);
    }

    [Fact]
    public void The_keybinds_survive_a_manifest_round_trip()
    {
        var path = Path.Combine(_root, "pack.json");

        new PackManifest
        {
            Id = "anego", GameVersion = "1.22.5",
            Keybinds = new Dictionary<string, string> { ["scribepinhud"] = "Ctrl-P" },
        }.Save(path);

        // Readable by eye — no \u002B — because a shared file nobody can review is a
        // shared file nobody should accept.
        Assert.Contains("\"Ctrl-P\"", File.ReadAllText(path));
        Assert.Equal("Ctrl-P", PackManifest.Load(path).Keybinds!["scribepinhud"]);
    }

    [Fact]
    public void A_pack_with_no_keybinds_writes_no_key_for_them()
    {
        var path = Path.Combine(_root, "pack.json");
        new PackManifest { Id = "anego", GameVersion = "1.22.5" }.Save(path);

        // A pack that never touched this looks exactly as it did before the feature existed.
        Assert.DoesNotContain("keybinds", File.ReadAllText(path));
    }
}

/// <summary>
/// A hotkey deliberately left on no key.
///
/// Distinct from the pack saying nothing about it: that hands the hotkey back to whatever
/// its mod ships, and this is a decision that it should not fire at all.
/// </summary>
public class UnboundKeyTests
{
    [Fact]
    public void It_reads_and_writes_as_a_word_rather_than_a_number()
    {
        var unbound = KeyBinding.Parse("none")!;

        Assert.True(unbound.IsUnbound);
        Assert.Equal("none", unbound.ToString());
        Assert.Equal(unbound, KeyBinding.Parse("unbound"));
        Assert.Equal(unbound, KeyBinding.Parse("NONE"));
    }

    [Fact]
    public void The_game_is_given_a_code_it_treats_as_unset()
    {
        // Negative rather than zero: the game's own KeyCombination.ToString answers "?" for
        // a negative code, where zero is GlKeys.Unknown and would render as that word. No
        // key event carries either, so the hotkey never fires.
        Assert.Equal(-1, KeyBinding.Unbound.ToJson()["KeyCode"]!.GetValue<int>());
    }

    [Fact]
    public void An_unbound_hotkey_is_not_worth_reporting_against_anything()
    {
        // Clashes stays a plain comparison — the tab's search uses it to find everything on
        // a given key, including the ones set to none. Whether a shared binding is a problem
        // is a separate question.
        Assert.False(KeyBinding.Unbound.Collides);
        Assert.True(KeyBinding.Parse("P")!.Collides);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("Ctrl-Unknown")]
    [InlineData("Ctrl-K,Unknown")]
    public void The_word_Unknown_is_a_typo_and_not_a_key(string text)
    {
        // It is a name in the game's enum and not a key on anybody's keyboard. Read as
        // code 0 it is neither known nor negative, so it is not Unbound either — a manifest
        // with that word in it would have written a real-looking mapping into somebody's
        // settings for a key that cannot be pressed. "none" is how a pack means no key.
        Assert.Null(KeyBinding.Parse(text));
        Assert.NotNull(KeyBinding.Parse("none"));
    }
}

/// <summary>
/// Keys that are held rather than pressed.
///
/// Shift and Ctrl are shared by design. Vanilla puts four things on LShift — sneak, the
/// click modifier, the middle mouse button and pick block — and mods join in deliberately:
/// CarryOn's pick-up is Shift-click and its swap to back is Ctrl-click. Counting those as
/// conflicts buried the five mods sitting on P.
/// </summary>
public class HeldModifierTests
{
    [Theory]
    [InlineData("LShift")]
    [InlineData("RShift")]
    [InlineData("LControl")]
    [InlineData("RControl")]
    [InlineData("AltLeft")]
    [InlineData("AltRight")]
    public void A_bare_modifier_is_held_and_not_worth_reporting(string key)
    {
        var binding = KeyBinding.Parse(key)!;

        Assert.True(binding.IsHeldModifier);
        Assert.False(binding.Collides);
    }

    [Theory]
    [InlineData("Ctrl-LShift")]          // held with something else is a combination
    [InlineData("P")]
    [InlineData("Ctrl-P")]
    [InlineData("Space")]
    public void Anything_else_is_a_press_and_is_reported(string key)
    {
        var binding = KeyBinding.Parse(key)!;

        Assert.False(binding.IsHeldModifier);
        Assert.True(binding.Collides);
    }

    [Fact]
    public void They_are_still_the_same_press_for_anyone_asking()
    {
        // The search box asks "what is on Ctrl?" and has to be answered, whether or not
        // sharing it is a problem.
        Assert.True(KeyBinding.Parse("LControl")!.Clashes(KeyBinding.Parse("LControl")!));
    }
}

/// <summary>
/// Hotkeys are part of the pack, so changing them is a change worth publishing.
///
/// They live in the manifest, and the manifest is most of the published document — which
/// means this falls out of the fingerprint rather than needing anything of its own. It is
/// asserted anyway: "the button did not notice" is the failure this projection exists to
/// prevent, and a value that quietly stopped reaching the document would be invisible.
/// </summary>
public class HotkeySharingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-hotkeyshare-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly PackStore _store;

    public HotkeySharingTests() => _store = new PackStore(Path.Combine(_root, "packs"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>A pack published exactly as it stands now.</summary>
    private PackManifest Published(string id)
    {
        var manifest = _store.Create(id, "1.22.5", id);

        _store.SaveLink(id, new PackLink
        {
            Role = PackRole.Author,
            Url = $"https://cairns.gg/dizzyd/{id}",
            Published = new PublishRecord
            {
                Fingerprint = PackLink.Fingerprint(_store.PublishedDocument(id, stripConnect: false)),
                Visibility = "public",
                Connect = "included",
            },
        });

        Assert.Equal(ShareStatus.Shared, _store.ShareStateFor(id).Status);
        return manifest;
    }

    [Fact]
    public void Binding_a_hotkey_makes_the_pack_publishable_again()
    {
        var manifest = Published("anego");

        manifest.Keybinds = new Dictionary<string, string> { ["scribepinhud"] = "Ctrl-K" };
        manifest.Save(_store.ManifestPath("anego"));

        var state = _store.ShareStateFor("anego");

        Assert.Equal(ShareStatus.Pending, state.Status);
        Assert.Equal("Publish changes", state.Label);
    }

    [Fact]
    public void Unbinding_one_counts_as_a_change_too()
    {
        var manifest = Published("anego");

        // "No key" is a decision the pack is making, not an absence of one.
        manifest.Keybinds = new Dictionary<string, string> { ["scribepinhud"] = "none" };
        manifest.Save(_store.ManifestPath("anego"));

        Assert.Equal(ShareStatus.Pending, _store.ShareStateFor("anego").Status);
    }

    [Fact]
    public void Taking_a_binding_back_off_leaves_the_pack_where_it_started()
    {
        var manifest = Published("anego");

        manifest.Keybinds = new Dictionary<string, string> { ["scribepinhud"] = "Ctrl-K" };
        manifest.Save(_store.ManifestPath("anego"));
        Assert.Equal(ShareStatus.Pending, _store.ShareStateFor("anego").Status);

        // Back to a manifest with no keybinds key at all, which is byte for byte the
        // document that was published.
        manifest.Keybinds = null;
        manifest.Save(_store.ManifestPath("anego"));

        Assert.Equal(ShareStatus.Shared, _store.ShareStateFor("anego").Status);
    }

    [Fact]
    public void The_hotkeys_are_in_what_a_follower_receives()
    {
        var manifest = Published("anego");
        manifest.Keybinds = new Dictionary<string, string> { ["scribepinhud"] = "Ctrl-K" };
        manifest.Save(_store.ManifestPath("anego"));

        // The whole point: the work reaches the people who did not do it.
        var bundle = PackBundle.Parse(_store.PublishedDocument("anego", stripConnect: false));

        Assert.Equal("Ctrl-K", bundle.Pack!.Keybinds!["scribepinhud"]);
    }
}
