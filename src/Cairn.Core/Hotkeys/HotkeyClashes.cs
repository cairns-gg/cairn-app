namespace Cairn.Core.Hotkeys;

/// <summary>
/// One hotkey as the clash rule needs to see it.
/// </summary>
/// <param name="Code">The hotkey id, which is what <c>keyMapping</c> is keyed by.</param>
/// <param name="Effective">
/// What will actually fire: the pack's binding where it has one, else the mod's own. Not
/// the mod's default — the answer changes the moment somebody rebinds one, and a rule fed
/// defaults keeps reporting a collision that has just been fixed.
/// </param>
/// <param name="IsGame">The game's own hotkey rather than one a mod brought.</param>
public readonly record struct BoundHotkey(string Code, KeyBinding? Effective, bool IsGame);

/// <summary>
/// Hotkeys that fire on the same press.
/// </summary>
/// <param name="Shared">
/// A held key — Shift, Ctrl, Alt — which several hotkeys are meant to share. Reported so a
/// shared key is not a mystery, and not counted as a problem, because it is not one.
/// </param>
public sealed record HotkeyClash(
    KeyBinding Binding,
    IReadOnlyList<string> Codes,
    bool Shared)
{
    /// <summary>Whether this is work for somebody, as against something worth mentioning.</summary>
    public bool Counts => !Shared;
}

/// <summary>
/// What in a pack fires on the same press as what.
///
/// In Core because it is a rule and not a rendering. The Hotkeys tab asks it what to mark;
/// <see cref="HotkeyCatalog.Result.Clashes"/> asks it the same question about a pack as its
/// mods ship it, which is what lets anything without a window — the CLI, a server — answer
/// "will this pack fight itself?". There was a copy of this in the view model and a
/// different one here, and the difference was not academic: the Core copy worked off the
/// mods' defaults and so went on reporting collisions the author had already resolved.
/// </summary>
public static class HotkeyClashes
{
    /// <summary>
    /// Every group of two or more hotkeys on one combination, in the order they were given.
    ///
    /// Three things are deliberately not clashes:
    ///
    /// Unbound. Two hotkeys on no key are both switched off, and neither fires.
    ///
    /// A held modifier, which is reported but not counted. Vanilla puts four things on
    /// LShift — sneak, the click modifier, the middle mouse button and pick block — and
    /// resolves between them by context; CarryOn's pick-up is Shift-click and its swap to
    /// back is Ctrl-click. That is the design working. Eight rows about Shift bury the five
    /// mods on P, which is the one somebody opened the list to find.
    ///
    /// Vanilla overlapping itself, which is dropped entirely: Space is both jump and fly,
    /// it has shipped that way for years, and flagging it teaches people to ignore the list
    /// that also holds the real ones. A mod landing on a vanilla key is the opposite — it is
    /// the most common way a pack goes wrong, and it is reported.
    /// </summary>
    public static IReadOnlyList<HotkeyClash> Find(IEnumerable<BoundHotkey> hotkeys)
    {
        var clashes = new List<HotkeyClash>();

        var groups = hotkeys
            .Where(h => h.Effective is { IsUnbound: false })
            .GroupBy(h => h.Effective!.ToString(), StringComparer.Ordinal);

        foreach (var group in groups)
        {
            // The same code twice is one hotkey seen in two files — a fork of the same mod,
            // or a library vendored beside it. The game registers it once, so it cannot
            // collide with itself.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var members = group.Where(h => seen.Add(h.Code)).ToList();

            if (members.Count < 2) continue;

            var binding = members[0].Effective!;

            // Checked before the vanilla rule, not after: the four things vanilla holds on
            // LShift are exactly the case somebody needs explaining, and dropping them as
            // "the game overlapping itself" would leave a shared key with nothing said
            // about it at all.
            if (binding.IsHeldModifier)
            {
                clashes.Add(new HotkeyClash(binding, [.. members.Select(h => h.Code)], Shared: true));
                continue;
            }

            if (members.All(h => h.IsGame)) continue;

            clashes.Add(new HotkeyClash(binding, [.. members.Select(h => h.Code)], Shared: false));
        }

        return clashes;
    }
}
