using Avalonia.Input;
using Cairn.Core.Hotkeys;
using Xunit;

namespace Cairn.App.Tests;

/// <summary>
/// Turning a key Avalonia reports into the code the game stores.
///
/// Worth testing exhaustively rather than by sample, because every failure here is silent:
/// a key that maps to the wrong code writes a binding that looks fine in the manifest and
/// does the wrong thing in game, and one that maps to nothing is a key somebody cannot bind
/// with no message saying why.
/// </summary>
[Collection(AvaloniaTests.Collection)]
public class KeyCodesTests
{
    /// <summary>
    /// Keys the game names that a keyboard event cannot produce, so nothing maps to them.
    ///
    /// F25 upwards because Avalonia's enum stops at F24 and no keyboard has gone further.
    /// Unknown and LastKey are not keys. The keypad's Enter is the real gap: the game keeps
    /// it apart from the main one, a key event does not tell them apart on any platform we
    /// ship for, and inventing a mapping would bind the wrong one.
    /// </summary>
    private static readonly HashSet<string> Unreachable =
    [
        "Unknown", "LastKey", "KeypadEnter",
        "F25", "F26", "F27", "F28", "F29", "F30", "F31", "F32", "F33", "F34", "F35",
    ];

    [Fact]
    public void Every_key_the_game_can_name_can_be_pressed()
    {
        var reached = Enum.GetValues<Key>().Select(KeyCodes.Of).OfType<int>().ToHashSet();

        var missing = Enumerable.Range(0, GlKeys.Count)
            .Where(code => !reached.Contains(code) && !Unreachable.Contains(GlKeys.All[code]))
            .Select(code => GlKeys.All[code])
            .ToList();

        Assert.Empty(missing);
    }

    [Theory]
    // The ones the two genuinely disagree about, which is what the written-out table is for.
    [InlineData(Key.D4, "Number4")]
    [InlineData(Key.NumPad4, "Keypad4")]
    [InlineData(Key.Subtract, "KeypadMinus")]
    [InlineData(Key.Return, "Enter")]
    [InlineData(Key.Scroll, "ScrollLock")]
    [InlineData(Key.Apps, "Menu")]
    [InlineData(Key.OemQuestion, "Slash")]
    [InlineData(Key.OemPipe, "BackSlash")]
    [InlineData(Key.LeftShift, "LShift")]
    [InlineData(Key.RightAlt, "AltRight")]
    // And a few the fallback handles, so the fallback is not quietly lost.
    [InlineData(Key.A, "A")]
    [InlineData(Key.F7, "F7")]
    [InlineData(Key.Escape, "Escape")]
    [InlineData(Key.Back, "Back")]
    [InlineData(Key.CapsLock, "CapsLock")]
    [InlineData(Key.PrintScreen, "PrintScreen")]
    public void A_key_maps_to_the_code_the_game_calls_it(Key key, string name)
    {
        Assert.True(GlKeys.TryParse(name, out var expected));
        Assert.Equal(expected, KeyCodes.Of(key));
    }

    /// <summary>
    /// Several of Avalonia's names are enum aliases sharing one value — OemTilde is Oem3 —
    /// and which name ToString returns is not specified. Looked up by name alone, the answer
    /// for Tilde would depend on which spelling the runtime happened to hand back.
    /// </summary>
    [Fact]
    public void An_aliased_name_does_not_decide_the_answer()
    {
        Assert.True(GlKeys.TryParse("Tilde", out var tilde));
        Assert.Equal(tilde, KeyCodes.Of(Key.OemTilde));
        Assert.Equal(tilde, KeyCodes.Of(Key.Oem3));
    }

    [Fact]
    public void A_key_the_game_has_no_name_for_binds_nothing()
    {
        // Better than binding the wrong thing, which is what a fallback guess would do.
        Assert.Null(KeyCodes.Of(Key.MediaPlayPause));
        Assert.Null(KeyCodes.Of(Key.None));
    }

    [Theory]
    [InlineData(Key.LeftCtrl)]
    [InlineData(Key.RightShift)]
    [InlineData(Key.LWin)]
    public void A_modifier_on_its_own_is_not_a_binding(Key key)
    {
        // Pressing Ctrl to type Ctrl-P sends a Ctrl keypress first, and taking it would
        // make every combination "Ctrl".
        Assert.True(KeyCodes.IsModifier(key));
    }
}
