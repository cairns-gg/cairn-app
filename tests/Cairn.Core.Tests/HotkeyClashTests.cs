using Cairn.Core.Hotkeys;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// What fires on the same press as what.
///
/// The rule is asymmetric on purpose, and every exception in it is there because reporting
/// something that is not a problem is worse than useless: it teaches the person reading to
/// stop reading, and the list also holds the five mods that really are all on P.
///
/// Here rather than in the view model because it is a rule. It lived in both for a while,
/// and the two disagreed — the copy here answered from the mods' defaults, so it went on
/// reporting collisions the author had already resolved.
/// </summary>
public class HotkeyClashTests
{
    private static BoundHotkey Mod(string code, string? key) =>
        new(code, KeyBinding.Parse(key), IsGame: false);

    private static BoundHotkey Game(string code, string? key) =>
        new(code, KeyBinding.Parse(key), IsGame: true);

    [Fact]
    public void Two_mods_on_one_key_collide()
    {
        var clash = Assert.Single(HotkeyClashes.Find(
            [Mod("scribepinhud", "P"), Mod("prospector-config", "P"), Mod("carry", "K")]));

        Assert.Equal(["scribepinhud", "prospector-config"], clash.Codes);
        Assert.True(clash.Counts);
        Assert.Equal("P", clash.Binding.ToString());
    }

    [Fact]
    public void A_mod_landing_on_a_vanilla_key_is_the_one_that_matters()
    {
        // The most common way a pack goes wrong, and the reason the game's own assembly is
        // worth reading at all.
        var clash = Assert.Single(HotkeyClashes.Find(
            [Game("inventory", "E"), Mod("someguide", "E")]));

        Assert.True(clash.Counts);
    }

    [Fact]
    public void Vanilla_overlapping_itself_is_not_reported()
    {
        // Space is jump and fly, and it has shipped that way for years.
        Assert.Empty(HotkeyClashes.Find([Game("jump", "Space"), Game("fly", "Space")]));
    }

    [Fact]
    public void A_held_modifier_is_named_but_not_counted()
    {
        // CarryOn's pick-up is Shift-click; several mods hold the same key on purpose. Worth
        // saying out loud, because a shared key nobody explained is its own puzzle — and not
        // worth counting, because eight rows about Shift bury the five mods on P.
        var clash = Assert.Single(HotkeyClashes.Find(
            [Mod("carry-pickup", "LShift"), Mod("haul-modifier", "LShift")]));

        Assert.True(clash.Shared);
        Assert.False(clash.Counts);
    }

    [Fact]
    public void Vanilla_sharing_a_held_key_is_still_named()
    {
        // Checked before the vanilla rule, not after. Sneak, the click modifier, the middle
        // mouse button and pick block are all on LShift; dropping them as "the game
        // overlapping itself" would leave a shared key with nothing said about it at all.
        var clash = Assert.Single(HotkeyClashes.Find(
            [Game("sneak", "LShift"), Game("shiftclick", "LShift")]));

        Assert.True(clash.Shared);
    }

    [Fact]
    public void Two_hotkeys_on_no_key_do_not_collide()
    {
        // Both switched off. Neither fires, so neither can fire at the same time.
        Assert.Empty(HotkeyClashes.Find([Mod("a", "none"), Mod("b", "none")]));
    }

    [Fact]
    public void A_hotkey_with_no_binding_at_all_is_not_in_the_running()
    {
        Assert.Empty(HotkeyClashes.Find([Mod("a", null), Mod("b", null)]));
    }

    [Fact]
    public void Modifiers_are_part_of_the_press()
    {
        Assert.Empty(HotkeyClashes.Find([Mod("a", "Ctrl-P"), Mod("b", "P")]));
    }

    [Fact]
    public void The_same_code_twice_is_one_hotkey()
    {
        // A fork of the same mod, or a library vendored beside it. The game registers it
        // once, so it cannot collide with itself.
        Assert.Empty(HotkeyClashes.Find([Mod("scribepinhud", "P"), Mod("SCRIBEPINHUD", "P")]));
    }

    [Fact]
    public void A_second_key_is_part_of_the_combination()
    {
        Assert.Empty(HotkeyClashes.Find([Mod("a", "Ctrl-K,M"), Mod("b", "Ctrl-K,N")]));
        Assert.Single(HotkeyClashes.Find([Mod("a", "Ctrl-K,M"), Mod("b", "Ctrl-K,M")]));
    }

    /// <summary>
    /// The rule answers about what is in force, not about what the mods ship — which is the
    /// difference the divergent copy got wrong, and the difference that makes the tab worth
    /// opening at all.
    /// </summary>
    [Fact]
    public void Rebinding_one_of_them_resolves_it()
    {
        Assert.Single(HotkeyClashes.Find([Mod("scribe", "P"), Mod("prospector", "P")]));
        Assert.Empty(HotkeyClashes.Find([Mod("scribe", "Ctrl-P"), Mod("prospector", "P")]));
    }
}
