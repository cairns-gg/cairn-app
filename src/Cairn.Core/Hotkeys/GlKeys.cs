namespace Cairn.Core.Hotkeys;

/// <summary>
/// The game's key codes, by the names its own controls screen shows.
///
/// A port of <c>Vintagestory.API.Client.GlKeys</c>, held here for the same reason
/// <see cref="GameVersionComparer"/> is: Cairn.Core references nothing, least of all the
/// game's assemblies, so that it builds in a container with no Vintage Story install.
///
/// The numbers are not arbitrary and are not ASCII — <c>A</c> is 83, <c>F1</c> is 10 — so
/// a keybind is unreadable as a number and has to be written as a name anywhere a person
/// will see it. The codes are what <c>clientsettings.json</c> stores.
/// </summary>
public static class GlKeys
{
    /// <summary>Index is the key code. Order is the enum's own, and must stay that way.</summary>
    private static readonly string[] Names =
    [
        "Unknown", "LShift", "RShift", "LControl", "RControl", "AltLeft", "AltRight", "WinLeft",
        "RWin", "Menu", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
        "F13", "F14", "F15", "F16", "F17", "F18", "F19", "F20", "F21", "F22", "F23", "F24", "F25",
        "F26", "F27", "F28", "F29", "F30", "F31", "F32", "F33", "F34", "F35", "Up", "Down", "Left",
        "Right", "Enter", "Escape", "Space", "Tab", "Back", "Insert", "Delete", "PageUp",
        "PageDown", "Home", "End", "CapsLock", "ScrollLock", "PrintScreen", "Pause", "NumLock",
        "Clear", "Sleep", "Keypad0", "Keypad1", "Keypad2", "Keypad3", "Keypad4", "Keypad5",
        "Keypad6", "Keypad7", "Keypad8", "Keypad9", "KeypadDivide", "KeypadMultiply", "KeypadMinus",
        "KeypadAdd", "KeypadDecimal", "KeypadEnter", "A", "B", "C", "D", "E", "F", "G", "H", "I",
        "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z",
        "Number0", "Number1", "Number2", "Number3", "Number4", "Number5", "Number6", "Number7",
        "Number8", "Number9", "Tilde", "Minus", "Plus", "LBracket", "BracketRight", "Semicolon",
        "Quote", "Comma", "Period", "Slash", "BackSlash", "LastKey",
    ];

    /// <summary>
    /// The enum's second names for codes that have two. Accepted when reading — a
    /// hand-written manifest saying "BackSpace" means the same key as "Back" — and never
    /// produced, so one key has one spelling in anything Cairn writes.
    /// </summary>
    private static readonly Dictionary<string, int> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ShiftLeft"] = 1, ["ShiftRight"] = 2, ["ControlLeft"] = 3, ["ControlRight"] = 4,
        ["LAlt"] = 5, ["RAlt"] = 6, ["LWin"] = 7, ["WinRight"] = 8, ["BackSpace"] = 53,
        ["KeypadSubtract"] = 79, ["KeypadPlus"] = 80, ["BracketLeft"] = 122, ["RBracket"] = 123,
    };

    private static readonly Dictionary<string, int> ByName = Build();

    private static Dictionary<string, int> Build()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < Names.Length; i++) map[Names[i]] = i;
        foreach (var (name, code) in Aliases) map[name] = code;
        return map;
    }

    public static int Count => Names.Length;

    /// <summary>Every key a binding can name, in the order the enum declares them.</summary>
    public static IReadOnlyList<string> All => Names;

    /// <summary>
    /// The name for a code, or the number in brackets when it is not one this build knows.
    /// A later game version adding a key must not make a pack unreadable, and showing
    /// "key 141" is a better answer than dropping the row.
    /// </summary>
    public static string Name(int code) =>
        code >= 0 && code < Names.Length ? Names[code] : $"key {code}";

    public static bool IsKnown(int code) => code > 0 && code < Names.Length;

    public static bool TryParse(string? name, out int code)
    {
        code = 0;
        return !string.IsNullOrWhiteSpace(name) && ByName.TryGetValue(name.Trim(), out code);
    }
}
