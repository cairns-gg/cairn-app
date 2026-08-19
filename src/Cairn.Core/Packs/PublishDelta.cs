using System.Text.Json.Nodes;

namespace Cairn.Core.Packs;

/// <summary>
/// What publishing would change about the revision already at a pack's address.
///
/// Read before pressing Publish, and only that: nothing here decides anything. The share
/// window otherwise says what the pack *contains*, which answers a different question — the
/// one somebody has on a first publish. After that the question is what is about to move,
/// and a pack an author has been playing for a month has moved in ways they will not
/// remember: five mods updated by a sync, a setting tuned in game, a hotkey rebound.
///
/// Compared against what the site actually serves rather than against anything recorded
/// here. The publish record keeps a fingerprint of the document and not the document, so
/// there is nothing local to diff — but the revision is a document at a URL, which is the
/// same one a follower reads to find out what changed. Asking is a request; being unable to
/// ask is not a reason to refuse to publish, so it is reported as not known.
/// </summary>
/// <param name="ModsAdded">Mods this publish would add to the pack.</param>
/// <param name="ModsRemoved">Mods it would drop.</param>
/// <param name="ModsMoved">Mods whose version would change, pinned or locked.</param>
/// <param name="SettingsChanged">Mod settings whose value would change, be added or go.</param>
/// <param name="HotkeysChanged">Hotkeys likewise.</param>
public sealed record PublishDelta(
    int ModsAdded,
    int ModsRemoved,
    int ModsMoved,
    int SettingsChanged,
    int HotkeysChanged,
    bool ConnectChanged,
    string? GameVersionFrom,
    string? GameVersionTo,
    bool DetailsChanged = false)
{
    public bool GameVersionChanged => GameVersionFrom is not null;

    public bool Anything =>
        ModsAdded > 0 || ModsRemoved > 0 || ModsMoved > 0
        || SettingsChanged > 0 || HotkeysChanged > 0
        || ConnectChanged || GameVersionChanged || DetailsChanged;

    /// <summary>
    /// The line somebody reads before publishing. Counts rather than names: this sits above
    /// a list that names the mods, and "which ones" is a question the list answers.
    /// </summary>
    public string Describe()
    {
        var parts = new List<string>();

        if (GameVersionChanged)
            parts.Add(Lang.Get("publish-delta-game", GameVersionFrom, GameVersionTo));

        if (ModsAdded > 0) parts.Add(Lang.Plural("publish-delta-added", ModsAdded, ModsAdded));
        if (ModsRemoved > 0) parts.Add(Lang.Plural("publish-delta-removed", ModsRemoved, ModsRemoved));
        if (ModsMoved > 0) parts.Add(Lang.Plural("publish-delta-moved", ModsMoved, ModsMoved));

        if (SettingsChanged > 0)
            parts.Add(Lang.Plural("publish-delta-settings", SettingsChanged, SettingsChanged));

        if (HotkeysChanged > 0)
            parts.Add(Lang.Plural("publish-delta-hotkeys", HotkeysChanged, HotkeysChanged));

        if (ConnectChanged) parts.Add(Lang.Get("publish-delta-connect"));

        // Last, because it is the one somebody already knows they did: renaming a pack or
        // rewriting its description is a deliberate edit, where five mods moving under it is
        // not. Named at all because leaving it out made a revision that changed only this
        // report itself as nothing having changed, on a screen whose button said otherwise.
        if (DetailsChanged) parts.Add(Lang.Get("publish-delta-details"));

        return parts.Count == 0 ? "" : string.Join(", ", parts);
    }

    /// <summary>
    /// Works out the difference between the revision on the site and the document about to
    /// be sent.
    /// </summary>
    /// <param name="published">The bundle fetched from the pack's own address.</param>
    /// <param name="pending">
    /// The document publishing would send, parsed back. Both sides therefore carry the same
    /// treatment of the server address — a pack published with it stripped must not read as
    /// having gained one.
    /// </param>
    public static PublishDelta Between(PackBundle published, PackBundle pending)
    {
        var was = published.Pack ?? new PackManifest();
        var now = pending.Pack ?? new PackManifest();

        // The mod half is PackUpdatePlan's, deliberately, rather than a second comparison
        // written to look the same. A mod's version lives in the manifest only when somebody
        // pinned it and in the lockfile the rest of the time — which is exactly what one copy
        // of this rule forgot once, leaving every pure mod-update revision reporting itself
        // as no change at all. One rule, in the place that already had to get it right.
        var plan = PackUpdatePlan.Between(
            was, now, was, myLock: published.Lock, theirLock: pending.Lock);

        var moved = plan.Changes.Count(c =>
            c.Kind is ModChangeKind.Repinned or ModChangeKind.Relocked);

        return new PublishDelta(
            ModsAdded: plan.Changes.Count(c => c.Kind == ModChangeKind.Added),
            ModsRemoved: plan.Changes.Count(c => c.Kind == ModChangeKind.Removed),
            ModsMoved: moved,
            SettingsChanged: CountDifferences(was.ModConfig, now.ModConfig),
            HotkeysChanged: CountDifferences(was.Keybinds, now.Keybinds),
            ConnectChanged: !string.Equals(
                was.Connect ?? "", now.Connect ?? "", StringComparison.OrdinalIgnoreCase),
            GameVersionFrom: string.Equals(was.GameVersion, now.GameVersion, StringComparison.OrdinalIgnoreCase)
                ? null
                : was.GameVersion,
            GameVersionTo: now.GameVersion,
            DetailsChanged:
                !string.Equals(was.Name ?? "", now.Name ?? "", StringComparison.Ordinal)
                || !string.Equals(was.Description ?? "", now.Description ?? "", StringComparison.Ordinal));
    }

    /// <summary>
    /// Keys that differ between two mod config sections, counting one that appears or goes
    /// as a change like any other — from the reader's side those are the same event, and a
    /// line distinguishing "2 changed and 1 added" is answering a question nobody asked
    /// before pressing a button.
    /// </summary>
    private static int CountDifferences(
        IReadOnlyDictionary<string, JsonObject>? was, IReadOnlyDictionary<string, JsonObject>? now)
    {
        var before = Flatten(was);
        var after = Flatten(now);

        return before.Keys.Union(after.Keys, StringComparer.Ordinal)
            .Count(key =>
            {
                var had = before.TryGetValue(key, out var a);
                var has = after.TryGetValue(key, out var b);

                return had != has || !JsonNode.DeepEquals(a, b);
            });
    }

    /// <summary>Every leaf as "file:section.key", so two sections can be compared as sets.</summary>
    private static Dictionary<string, JsonNode?> Flatten(
        IReadOnlyDictionary<string, JsonObject>? config)
    {
        var flat = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (config is null) return flat;

        foreach (var (file, root) in config) Walk(file + ":", root, flat);

        return flat;

        static void Walk(string prefix, JsonObject node, Dictionary<string, JsonNode?> into)
        {
            foreach (var (key, value) in node)
            {
                if (value is JsonObject section) Walk(prefix + key + ".", section, into);
                else into[prefix + key] = value;
            }
        }
    }

    /// <summary>Keys that differ between two hotkey sets.</summary>
    private static int CountDifferences(
        IReadOnlyDictionary<string, string>? was, IReadOnlyDictionary<string, string>? now)
    {
        var before = was ?? new Dictionary<string, string>();
        var after = now ?? new Dictionary<string, string>();

        return before.Keys.Union(after.Keys, StringComparer.Ordinal)
            .Count(key =>
            {
                var had = before.TryGetValue(key, out var a);
                var has = after.TryGetValue(key, out var b);

                return had != has || !string.Equals(a, b, StringComparison.Ordinal);
            });
    }
}
