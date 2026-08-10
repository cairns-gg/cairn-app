using System.Text.Json.Nodes;
using Cairn.Core.Hotkeys;

namespace Cairn.Core.Launch;

/// <summary>
/// Puts a pack's hotkeys into the settings file the game reads, without taking any that
/// belong to the player.
///
/// The pack's author reconciled the collisions once. Everybody else gets the result on
/// first launch and never learns there was anything to reconcile — which is the whole
/// point, and also the reason this has to be careful: a launcher that silently changes
/// somebody's keyboard is a launcher nobody trusts twice.
///
/// So it fills, and never overwrites. A code the player has already bound is theirs: they
/// either set it in game, or the game recorded it, and either way the pack's opinion is
/// older than their decision. That also makes the operation safe to repeat on every
/// launch, which matters because a pack update can add a mod — and its hotkey should
/// arrive with it rather than only for people who had not installed the pack yet.
/// </summary>
public static class ClientHotkeys
{
    private const string Bucket = "keyMapping";

    /// <summary>
    /// Writes the bindings this pack declares that the settings file has no entry for.
    /// Returns the codes it bound, so a launch can say so rather than change the keyboard
    /// in silence.
    /// </summary>
    public static IReadOnlyList<string> Apply(
        string clientSettingsPath, IReadOnlyDictionary<string, string>? keybinds)
    {
        if (keybinds is null || keybinds.Count == 0) return [];

        var root = ClientSettingsFile.TryLoad(clientSettingsPath) ?? new JsonObject();

        if (root[Bucket] is not JsonObject mapping)
        {
            mapping = new JsonObject();
            root[Bucket] = mapping;
        }

        var bound = new List<string>();

        foreach (var (code, text) in keybinds)
        {
            if (string.IsNullOrWhiteSpace(code)) continue;

            // Already answered on this machine. Not compared with the pack's value on
            // purpose: "the player set something different" and "the player set the same
            // thing" both mean the question is settled.
            if (mapping.ContainsKey(code)) continue;

            // A combination this build cannot name is left out rather than approximated.
            // The manifest is hand-editable and a typo should cost one missing binding,
            // not a key bound to whatever the parse fell through to.
            if (KeyBinding.Parse(text) is not { } binding) continue;

            mapping[code] = binding.ToJson();
            bound.Add(code);
        }

        if (bound.Count == 0) return [];

        ClientSettingsFile.Write(clientSettingsPath, root);
        return bound;
    }

    /// <summary>
    /// What the settings file currently binds, for an editor that wants to show the player
    /// their own answer alongside the pack's.
    ///
    /// Nothing calls this yet, and it stays because it is the only thing that reads what
    /// <see cref="Apply"/> writes. <see cref="KeyBinding.ToJson"/> has to use the game's own
    /// property names — a file with different ones deserialises to defaults, which is to say
    /// to no binding at all — and that is a silent failure with no test that could catch it
    /// short of launching the game. The round trip through here is that test.
    /// </summary>
    public static IReadOnlyDictionary<string, KeyBinding> Read(string clientSettingsPath)
    {
        var result = new Dictionary<string, KeyBinding>(StringComparer.OrdinalIgnoreCase);

        if (ClientSettingsFile.TryLoad(clientSettingsPath)?[Bucket] is not JsonObject mapping)
            return result;

        foreach (var (code, node) in mapping)
            if (KeyBinding.FromJson(node) is { } binding)
                result[code] = binding;

        return result;
    }
}
