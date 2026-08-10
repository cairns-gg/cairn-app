using Avalonia.Input;
using Cairn.Core.Hotkeys;

namespace Cairn.App;

/// <summary>
/// Turns a key Avalonia reports into the code the game stores.
///
/// The two enumerations agree on most names — <c>A</c>, <c>F1</c>, <c>Escape</c> — and
/// disagree on exactly the keys whose names nobody agrees on anyway: the number row, the
/// keypad, and the punctuation Windows calls "Oem" something. So the table is tried first
/// and the names are the fallback, which keeps the written-out part short enough to check
/// by eye and means a key added to either side does not silently map to something else.
///
/// The table has to come first, and not only for the keys the two genuinely disagree about.
/// Several of Avalonia's are enum aliases — <c>OemTilde</c> shares its value with
/// <c>Oem3</c> — and which of the two names <c>ToString</c> returns is not specified, so the
/// fallback alone would answer "Oem3" for a key the game calls Tilde. Named here, the value
/// is what is looked up and the spelling never comes into it.
///
/// What has no answer is the keypad's Enter: the game separates it from the main one, and a
/// key event does not, so it comes through as Enter on every platform. A pack cannot bind it
/// from here, which is a real gap and a small one.
/// </summary>
public static class KeyCodes
{
    private static readonly Dictionary<Key, string> Named = new()
    {
        // The number row. Avalonia calls them D0..D9; the game calls them Number0..Number9,
        // and its Keypad0 is a different key.
        [Key.D0] = "Number0", [Key.D1] = "Number1", [Key.D2] = "Number2", [Key.D3] = "Number3",
        [Key.D4] = "Number4", [Key.D5] = "Number5", [Key.D6] = "Number6", [Key.D7] = "Number7",
        [Key.D8] = "Number8", [Key.D9] = "Number9",

        [Key.NumPad0] = "Keypad0", [Key.NumPad1] = "Keypad1", [Key.NumPad2] = "Keypad2",
        [Key.NumPad3] = "Keypad3", [Key.NumPad4] = "Keypad4", [Key.NumPad5] = "Keypad5",
        [Key.NumPad6] = "Keypad6", [Key.NumPad7] = "Keypad7", [Key.NumPad8] = "Keypad8",
        [Key.NumPad9] = "Keypad9",

        [Key.Add] = "KeypadAdd", [Key.Subtract] = "KeypadMinus", [Key.Multiply] = "KeypadMultiply",
        [Key.Divide] = "KeypadDivide", [Key.Decimal] = "KeypadDecimal",

        [Key.Return] = "Enter",
        [Key.Scroll] = "ScrollLock",

        // The context-menu key, between right Alt and right Ctrl. Avalonia calls it Apps
        // and the game calls it Menu, which is the only name on either side that the other
        // does not share — everything else the two disagree about is in the groups above.
        [Key.Apps] = "Menu",

        [Key.OemTilde] = "Tilde", [Key.OemMinus] = "Minus", [Key.OemPlus] = "Plus",
        [Key.OemOpenBrackets] = "LBracket", [Key.OemCloseBrackets] = "BracketRight",
        [Key.OemSemicolon] = "Semicolon", [Key.OemQuotes] = "Quote",
        [Key.OemComma] = "Comma", [Key.OemPeriod] = "Period",
        [Key.OemQuestion] = "Slash", [Key.OemPipe] = "BackSlash", [Key.OemBackslash] = "BackSlash",

        // The game distinguishes left from right; a keyboard event does too, but only
        // through the key rather than the modifier flags.
        [Key.LeftShift] = "LShift", [Key.RightShift] = "RShift",
        [Key.LeftCtrl] = "LControl", [Key.RightCtrl] = "RControl",
        [Key.LeftAlt] = "AltLeft", [Key.RightAlt] = "AltRight",
    };

    /// <summary>
    /// The game's code for this key, or null for one it has no name for — a media key, or
    /// something a particular keyboard invented. Nothing is bound in that case, which is
    /// better than binding the wrong thing.
    /// </summary>
    public static int? Of(Key key)
    {
        if (Named.TryGetValue(key, out var name) && GlKeys.TryParse(name, out var mapped))
            return mapped;

        return GlKeys.TryParse(key.ToString(), out var direct) ? direct : null;
    }

    /// <summary>
    /// Whether this press is only a modifier being held. Capture waits for the key the
    /// modifiers apply to: pressing Ctrl to type Ctrl-P sends a Ctrl keypress first, and
    /// binding that would make every combination "Ctrl".
    /// </summary>
    public static bool IsModifier(Key key) => key
        is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
}
