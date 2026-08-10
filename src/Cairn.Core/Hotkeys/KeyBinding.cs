using System.Text.Json.Nodes;

namespace Cairn.Core.Hotkeys;

/// <summary>
/// One key combination, as <c>clientsettings.json</c> stores it and as a person writes it.
///
/// The file holds an object per hotkey code:
///
/// <code>
/// "keyMapping": {
///   "statushudconfiggui": { "KeyCode": 53, "SecondKeyCode": null,
///                           "Ctrl": false, "Alt": false, "Shift": false, "OnKeyUp": false }
/// }
/// </code>
///
/// A manifest writes "Ctrl+BackSpace" instead. Both directions matter: the numbers are what
/// the game reads, and a pack file that says <c>53</c> is a pack file nobody can review.
/// </summary>
public sealed record KeyBinding(
    int KeyCode,
    bool Ctrl = false,
    bool Alt = false,
    bool Shift = false,
    int? SecondKeyCode = null)
{
    /// <summary>
    /// A hotkey deliberately left on no key at all.
    ///
    /// Negative rather than zero because that is what the game means by it: its own
    /// <c>KeyCombination.ToString</c> answers "?" for a negative code, where zero is
    /// <c>GlKeys.Unknown</c> and would render as the word. No key event carries either, so
    /// a hotkey mapped here never fires — which is the point. It is how a pack says "this
    /// mod's hotkey is not worth a key in this pack" without asking every player to work
    /// that out for themselves.
    /// </summary>
    public const int UnboundKey = -1;

    public static readonly KeyBinding Unbound = new(UnboundKey);

    public bool IsUnbound => KeyCode < 0;

    /// <summary>How a manifest and a button write it. Read back by <see cref="Parse"/>.</summary>
    public const string UnboundText = "none";

    /// <summary>
    /// Written back out with the game's own casing. The game deserialises into a type with
    /// these property names, and a file with different ones would parse to defaults —
    /// which is to say, to no binding at all.
    /// </summary>
    public JsonObject ToJson() => new()
    {
        ["KeyCode"] = KeyCode,
        ["SecondKeyCode"] = SecondKeyCode,
        ["Ctrl"] = Ctrl,
        ["Alt"] = Alt,
        ["Shift"] = Shift,
        ["OnKeyUp"] = false,
    };

    public static KeyBinding? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        if (Int(o, "KeyCode") is not { } key) return null;

        return new KeyBinding(key, Bool(o, "Ctrl"), Bool(o, "Alt"), Bool(o, "Shift"), Int(o, "SecondKeyCode"));

        static int? Int(JsonObject o, string key) =>
            o[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

        static bool Bool(JsonObject o, string key) =>
            o[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    }

    /// <summary>
    /// Written with hyphens — "Ctrl-Shift-P" — and read with either.
    ///
    /// A plus would be the obvious separator and is accepted, but System.Text.Json's
    /// default encoder escapes it: the manifest would hold <c>"Ctrl+P"</c>, and so
    /// would every published bundle. The whole reason these are key names rather than the
    /// numbers the game stores is that somebody can read the file before importing it, and
    /// an escape sequence gives that back. Relaxing the encoder instead would have reached
    /// the bundle serialiser, whose exact bytes are what a publish record is fingerprinted
    /// against — every published pack would have reported itself changed.
    ///
    /// Modifiers first and in a fixed order, so the same combination always writes the same
    /// string; one that reorders itself between saves is one that reports a change nobody
    /// made.
    /// </summary>
    public override string ToString()
    {
        if (IsUnbound) return UnboundText;

        var parts = new List<string>(4);
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        parts.Add(GlKeys.Name(KeyCode));

        var text = string.Join('-', parts);
        return SecondKeyCode is { } second ? $"{text},{GlKeys.Name(second)}" : text;
    }

    /// <summary>
    /// Reads "Ctrl-Shift-K" or "Ctrl+Shift+K", or null for anything that is not a
    /// combination this build can name. Both separators, because the file is hand-editable
    /// and a plus is what anybody would type; see <see cref="ToString"/> for why only one
    /// of them is ever written.
    ///
    /// Refused rather than guessed: a binding Cairn does not understand must not be written
    /// into somebody's settings as whatever it parsed to.
    /// </summary>
    public static KeyBinding? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Written by the Unbind button, and typeable by hand in a manifest.
        if (text.Trim() is var trimmed
            && (trimmed.Equals(UnboundText, StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("unbound", StringComparison.OrdinalIgnoreCase)))
            return Unbound;

        // A second key is separated by a comma — the game supports two-key combinations,
        // and dropping the tail would silently bind the first key on its own.
        var halves = text.Split(',', 2, StringSplitOptions.TrimEntries);

        int? second = null;
        if (halves.Length == 2)
        {
            if (!Key(halves[1], out var s)) return null;
            second = s;
        }

        bool ctrl = false, alt = false, shift = false;
        int? key = null;

        var separators = new[] { '-', '+' };

        foreach (var part in halves[0].Split(separators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; continue;
                case "alt": alt = true; continue;
                case "shift": shift = true; continue;
            }

            // Two named keys with no comma is not a combination the game can express, and
            // taking the last one would bind something nobody asked for.
            if (key is not null) return null;
            if (!Key(part, out var code)) return null;
            key = code;
        }

        return key is { } k ? new KeyBinding(k, ctrl, alt, shift, second) : null;

        // "Unknown" is a name in the enum and not a key on anybody's keyboard. Left to
        // GlKeys.TryParse it read as code 0, which is not negative and so is not Unbound —
        // a manifest with that word in it would have written a real-looking mapping into
        // somebody's settings for a key that cannot be pressed. "none" is how a pack says
        // it means no key; this is a typo.
        static bool Key(string name, out int code) =>
            GlKeys.TryParse(name, out code) && GlKeys.IsKnown(code);
    }

    /// <summary>
    /// Whether two bindings are the same press. One method rather than a comparison spelled
    /// out at each call site, because it is also how the tab's search finds "everything on
    /// Ctrl-P".
    ///
    /// Being the same press is not the same as being a conflict — see <see cref="Collides"/>.
    /// </summary>
    public bool Clashes(KeyBinding other) =>
        KeyCode == other.KeyCode && Ctrl == other.Ctrl && Alt == other.Alt
        && Shift == other.Shift && SecondKeyCode == other.SecondKeyCode;

    /// <summary>
    /// A key that is only ever held: Shift, Ctrl, Alt on either side, with nothing else in
    /// the combination.
    ///
    /// These are shared by design. Vanilla puts four things on LShift — sneak, the click
    /// modifier, the middle mouse button and pick block — and resolves between them by
    /// context. Mods do the same on purpose: CarryOn's pick-up is Shift-click and its swap
    /// to back is Ctrl-click, which is the feature working, not two mods fighting.
    /// </summary>
    public bool IsHeldModifier =>
        !Ctrl && !Alt && !Shift && SecondKeyCode is null
        && KeyCode is >= 1 and <= 6;      // LShift, RShift, LControl, RControl, AltLeft, AltRight

    /// <summary>
    /// Whether sharing this binding with another hotkey is worth reporting.
    ///
    /// Two hotkeys on no key are both switched off, and two on a held modifier are how the
    /// game is designed. Counting either as a conflict buries the ones that are: five mods
    /// on P is the signal, and eight rows about Shift and Ctrl are the noise that teaches
    /// somebody to stop reading the list.
    /// </summary>
    public bool Collides => !IsUnbound && !IsHeldModifier;
}
