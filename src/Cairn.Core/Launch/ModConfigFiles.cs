using Cairn.Core;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cairn.Core.Launch;

/// <summary>What became of one value a pack declared for a mod's config file.</summary>
public enum ModConfigOutcome
{
    /// <summary>Written into the file.</summary>
    Applied,

    /// <summary>Left as it was, because it has been changed since the pack last spoke about it.</summary>
    Kept,

    /// <summary>The pack used to set this and no longer does, so Cairn has stopped claiming it.</summary>
    Released,

    /// <summary>
    /// The pack names a setting the file does not have, in a file where one cannot be added.
    ///
    /// Only ConfigLib's YAML: it rebuilds those files from its own settings when it saves, so
    /// a key it does not recognise is deleted on the next load. Reporting it is the whole
    /// difference between a manifest typo and a setting that silently never applies.
    /// </summary>
    Missing,

    /// <summary>A whole file was left alone. <see cref="ModConfigChange.Detail"/> says why.</summary>
    Refused,
}

/// <summary>
/// One thing that happened while applying a pack's mod config. <see cref="Key"/> is a
/// dotted path into the file — <c>Rooms.Enabled</c> — and is empty for
/// <see cref="ModConfigOutcome.Refused"/>, which is about the file rather than a value in it.
/// </summary>
public sealed record ModConfigChange(
    string File, string Key, ModConfigOutcome Outcome, Message? Detail = null,
    JsonNode? Value = null)
{
    /// <summary>
    /// The sentence a front end prints. Here rather than in each front end so the launcher
    /// and the CLI say the same thing about the same event — and so the wording of "Cairn
    /// changed a file a mod owns" is decided once.
    /// </summary>
    public string Describe() => Outcome switch
    {
        ModConfigOutcome.Applied => Lang.Get("modconfig-log-set", File, Key),
        ModConfigOutcome.Kept when Detail is null => Lang.Get("modconfig-log-kept", File, Key),
        ModConfigOutcome.Kept => Lang.Get("modconfig-log-kept-why", File, Key, Detail),
        ModConfigOutcome.Released => Lang.Get("modconfig-log-released", File, Key),
        ModConfigOutcome.Missing => Lang.Get("modconfig-log-missing", File, Key),
        _ when Key.Length > 0 => Lang.Get("modconfig-log-refused-key", File, Key, Detail),
        _ => Lang.Get("modconfig-log-refused", File, Detail),
    };
}

/// <summary>
/// Applies the config values a pack declares to the mods' own config files under the pack's
/// data path.
///
/// The case this exists for: two mods need a line in one of their config files to work
/// together, the author works that out once, and without somewhere to put the answer every
/// person who installs the pack works it out again — or, more often, does not, and the pack
/// is quietly worse than the author's copy of it.
///
/// **The rule <see cref="ClientHotkeys"/> uses does not work here.** That one fills only
/// codes the settings file has no entry for, which is safe because <c>keyMapping</c> is a
/// sparse delta — most entries are simply absent. A mod config file is the opposite: the
/// mod rewrites it in full on every load, so every key is present at its default from the
/// first launch onwards. "Fill only what is missing" would do nothing at all for anybody who
/// has ever pressed Play.
///
/// So this keeps a record of what the pack last asked for, in
/// <see cref="RecordName"/> beside the data it describes, and compares three values rather
/// than two:
///
/// <list type="bullet">
/// <item>the file has no such key — write it;</item>
/// <item>the record has nothing about this key — the pack has never spoken about it, so this
/// is its first word and it is applied. A player cannot have overridden a pack that had not
/// yet said anything;</item>
/// <item>the file still holds what the pack last asked for — the pack owns it, so a changed
/// value is applied;</item>
/// <item>the file holds something else — somebody changed it after Cairn wrote it. It is
/// theirs. Report it and change nothing.</item>
/// </list>
///
/// Which gives the lifecycle worth having: a pack's value arrives once, and the moment a
/// player moves it, it is theirs permanently — including against later pack updates.
///
/// The record lives inside the data path rather than beside the manifest, because it
/// describes those files and must die with them. Kept in the pack directory it would
/// survive Delete data, and the next launch would see the mod's freshly written defaults
/// disagree with a record claiming Cairn had written something else — reading a brand new
/// file as a player's deliberate edits, and refusing to apply the pack to it forever.
/// </summary>
public static class ModConfigFiles
{
    /// <summary>
    /// What the pack last asked for, per file, in the same sparse shape as the manifest.
    ///
    /// Not "what Cairn wrote": a value the player owns is recorded too, and has to be. Were
    /// only writes recorded, a key Cairn declined to overwrite would have no entry next
    /// launch, "no record" means first word, and the pack would take the value away from
    /// them again on every single launch.
    /// </summary>
    public const string RecordName = "cairn-modconfig.json";

    /// <summary>
    /// The first content Cairn ever saw for each config file, so an author can be shown what
    /// they have changed rather than being asked to remember it.
    ///
    /// There is no other source for a mod's defaults. They live in field initialisers inside
    /// the mod's own assembly, and the only honest way to learn them from outside the game is
    /// to look at what the mod wrote the first time it ran. First observation wins and is
    /// never updated: the point of the file is to be older than the author's edits.
    ///
    /// Two things it cannot see, both stated on the tab rather than papered over. A value the
    /// author changed during the very first session is already in the file by the time
    /// anything observes it, and the pack's own declared values are written before the mod
    /// first runs, so they are in the baseline too — harmless, because those are exactly the
    /// keys the manifest already names and the tab already shows as carried.
    /// </summary>
    public const string BaselineName = "cairn-modconfig-baseline.json";

    /// <summary>The game's own directory for these, hardcoded under the data path.</summary>
    internal const string ConfigDir = "ModConfig";

    /// <summary>
    /// Where a pack's mod config files live. The one place that knows the folder name, since
    /// the game fixes it under the data path and a front end offering to open it should not
    /// have to spell it a second time.
    /// </summary>
    public static string DirectoryIn(string dataPath) => Path.Combine(dataPath, ConfigDir);

    /// <summary>
    /// A config file past this size is not one somebody is hand-tuning two values in — the
    /// largest in a real pack is a 149KB generated ore table — and reading every one of them
    /// into a diff on every launch would be disk nobody asked for.
    /// </summary>
    internal const int MaxFileToSurvey = 256 * 1024;

    private static readonly JsonSerializerOptions Write = new() { WriteIndented = true };

    /// <summary>
    /// Lenient only for reading. Vintage Story parses these with Newtonsoft, which accepts
    /// comments and trailing commas, and two of the hundred-odd config files in a real pack
    /// use comments to document their own settings. Being unable to read those at all would
    /// be worse than being unable to write them — see <see cref="Read"/>.
    /// </summary>
    private static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Puts the pack's declared values into the mods' config files, and returns everything
    /// that happened — including what it decided not to do, which is the half a person needs
    /// to be told about.
    /// </summary>
    /// <param name="dataPath">The pack's data path. <c>ModConfig/</c> sits inside it.</param>
    /// <param name="declared">
    /// <see cref="Packs.PackManifest.ModConfig"/>: file path relative to <c>ModConfig/</c>,
    /// against a sparse object holding only the values the pack asserts.
    /// </param>
    public static IReadOnlyList<ModConfigChange> Apply(
        string dataPath, IReadOnlyDictionary<string, JsonObject>? declared)
    {
        var record = LoadRecord(dataPath);
        if ((declared is null || declared.Count == 0) && record.Count == 0) return [];

        var changes = new List<ModConfigChange>();
        var next = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);

        foreach (var (file, patch) in declared ?? new Dictionary<string, JsonObject>())
        {
            if (patch is null) continue;

            if (PathProblem(file) is { } problem)
            {
                changes.Add(new ModConfigChange(file, "", ModConfigOutcome.Refused, problem));
                continue;
            }

            record.TryGetValue(file, out var last);

            // Anything the pack has stopped asking for, said once: the record is rewritten
            // below without it, so this does not repeat on every launch.
            if (last is not null) Released(file, last, patch, "", changes);

            var full = Path.Combine(DirectoryIn(dataPath), Native(file));
            var yaml = IsYaml(file);
            var (root, text, rewritable, why) = Read(full);

            if (root is null || !rewritable)
            {
                changes.Add(new ModConfigChange(file, "", ModConfigOutcome.Refused, why));

                // Still recorded. The pack's word has not changed just because this copy
                // cannot be written to, and forgetting it would make the next readable
                // launch treat every key as a first word and take back the player's edits.
                next[file] = patch.DeepClone().AsObject();
                continue;
            }

            // ConfigLib's own key, taken out of the patch rather than merely complained
            // about. Setting it to anything but what the mod's patch file says makes
            // ConfigLib discard the whole file and write its defaults over every setting in
            // it, so a pack that could name this could wipe somebody's config.
            var effective = WithoutVersion(yaml, patch, file, changes);

            var from = changes.Count;
            var wrote = false;

            // A YAML file may only have the keys it already has. ConfigLib rebuilds these
            // from its own settings when it saves — unlike the JSON path, which merges with
            // what is on disk — so a key it does not recognise is deleted on the next load.
            // Writing one would be a setting that appears to work and silently does not.
            Merge(root, effective, last, file, "", changes, ref wrote, mayAdd: !yaml);

            if (wrote)
            {
                var written = yaml ? ModConfigYaml.Apply(text!, Applied(changes, from)) : Text(root);

                if (!Save(full, written))
                    changes.Add(new ModConfigChange(file, "", ModConfigOutcome.Refused,
                        new Message("modconfig-why-unwritable")));
            }

            next[file] = patch.DeepClone().AsObject();
        }

        // Files dropped from the manifest entirely.
        foreach (var (file, last) in record)
            if (!next.ContainsKey(file))
                Released(file, last, new JsonObject(), "", changes);

        SaveRecord(dataPath, next);
        return changes;
    }

    /// <summary>
    /// Records the content of any config file not seen before, so the Mod config tab can
    /// show an author what they have changed.
    ///
    /// Called on the way into a launch — before <see cref="Apply"/>, so the pack's own values
    /// are not mistaken for the mod's — and again on the way out, because the first launch of
    /// a pack is the one where the files do not exist yet on the way in. Idempotent by
    /// design: a file already in the baseline is never re-read.
    /// </summary>
    /// <returns>The files newly recorded, which is empty on nearly every launch.</returns>
    public static IReadOnlyList<string> Capture(string dataPath)
    {
        var root = DirectoryIn(dataPath);
        if (!Directory.Exists(root)) return [];

        var baseline = LoadDocument(Path.Combine(dataPath, BaselineName));
        var added = new List<string>();

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => PathProblem(Path.GetFileName(f)) is null)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        foreach (var full in files)
        {
            var rel = Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
            if (baseline.ContainsKey(rel)) continue;

            try
            {
                if (new FileInfo(full).Length > MaxFileToSurvey) continue;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // Only what a survey could read anyway. A file with comments in it is refused by
            // Apply, so recording a baseline for it would promise an edit that cannot land.
            var (content, _, rewritable, _) = Read(full);
            if (content is null || !rewritable) continue;

            baseline[rel] = content;
            added.Add(rel);
        }

        if (added.Count > 0) SaveDocument(Path.Combine(dataPath, BaselineName), baseline);
        return added;
    }

    /// <summary>What the mod first wrote, per file, or empty where nothing has been seen.</summary>
    internal static Dictionary<string, JsonObject> Baseline(string dataPath) =>
        LoadDocument(Path.Combine(dataPath, BaselineName));

    /// <summary>
    /// Merges one sparse patch into the file's tree, deciding each leaf on its own.
    ///
    /// Objects recurse; everything else — a number, a string, an array — is a leaf and is
    /// replaced whole. Merging arrays element by element was considered and dropped: there
    /// is no answer to whether a declared list appends, replaces or de-duplicates that is
    /// right for every mod, and a pack that declares the list its author tested is both
    /// predictable and what the manifest appears to say.
    /// </summary>
    /// <summary>
    /// The patch with ConfigLib's <c>version</c> removed, and a word about why.
    ///
    /// One bad key costs one key: the rest of the patch still lands, the same as a mod entry
    /// that cannot be used does not stop a pack syncing.
    /// </summary>
    private static JsonObject WithoutVersion(
        bool yaml, JsonObject patch, string file, List<ModConfigChange> changes)
    {
        if (!yaml) return patch;

        var named = patch.Select(p => p.Key)
            .Where(k => string.Equals(k, ModConfigYaml.VersionKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (named.Count == 0) return patch;

        changes.Add(new ModConfigChange(file, ModConfigYaml.VersionKey, ModConfigOutcome.Refused,
            new Message("modconfig-why-version")));

        var without = patch.DeepClone().AsObject();
        foreach (var key in named) without.Remove(key);

        return without;
    }

    /// <summary>
    /// The leaf values a merge actually wrote, for a flat file that has to be edited as text
    /// rather than rebuilt from a tree. Sound only because a ConfigLib file has no sections,
    /// so every path is one segment — see <see cref="ModConfigYaml"/>.
    /// </summary>
    private static Dictionary<string, JsonNode?> Applied(List<ModConfigChange> changes, int from)
    {
        var written = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);

        for (var i = from; i < changes.Count; i++)
            if (changes[i] is { Outcome: ModConfigOutcome.Applied, Value: var value, Key: var key })
                written[key] = value;

        return written;
    }

    private static void Merge(
        JsonObject target, JsonObject patch, JsonObject? last,
        string file, string prefix, List<ModConfigChange> changes, ref bool wrote,
        bool mayAdd = true)
    {
        foreach (var (key, value) in patch)
        {
            var path = prefix.Length == 0 ? key : $"{prefix}.{key}";

            // The game reads these keys with OrdinalIgnoreCase — JsonObject's own indexer
            // does, and Newtonsoft matches properties the same way — so a manifest saying
            // "enableSlabs" against a file holding "EnableSlabs" means the same key to the
            // mod. Writing the manifest's spelling would leave two keys in the file and let
            // the mod pick, which it does by document order: a setting that silently does
            // nothing. The file's spelling wins wherever it already has one.
            var actual = MatchKey(target, key);
            var hasLast = TryMatch(last, key, out var lastValue);

            if (value is JsonObject section)
            {
                if (actual is null)
                {
                    if (!mayAdd)
                    {
                        changes.Add(new ModConfigChange(file, path, ModConfigOutcome.Missing));
                        continue;
                    }

                    var created = new JsonObject();
                    target[key] = created;
                    Merge(created, section, lastValue as JsonObject, file, path, changes, ref wrote);
                    continue;
                }

                if (target[actual] is JsonObject existing)
                {
                    Merge(existing, section, lastValue as JsonObject, file, path, changes,
                          ref wrote, mayAdd);
                    continue;
                }

                changes.Add(new ModConfigChange(file, path, ModConfigOutcome.Kept,
                    new Message("modconfig-why-value-not-section")));
                continue;
            }

            if (actual is null)
            {
                if (!mayAdd)
                {
                    changes.Add(new ModConfigChange(file, path, ModConfigOutcome.Missing));
                    continue;
                }

                target[key] = value?.DeepClone();
                wrote = true;
                changes.Add(new ModConfigChange(file, path, ModConfigOutcome.Applied, Value: value));
                continue;
            }

            // Already what the pack wants. Not reported: nothing happened, and a launch that
            // listed every value it agreed with would bury the ones it changed.
            if (JsonNode.DeepEquals(target[actual], value)) continue;

            if (hasLast && !JsonNode.DeepEquals(target[actual], lastValue))
            {
                changes.Add(new ModConfigChange(file, path, ModConfigOutcome.Kept));
                continue;
            }

            target[actual] = value?.DeepClone();
            wrote = true;
            changes.Add(new ModConfigChange(file, path, ModConfigOutcome.Applied, Value: value));
        }
    }

    /// <summary>
    /// Reports every leaf the pack used to declare and no longer does. The value is left
    /// exactly as it is: the mod's default for it is not knowable from outside the game —
    /// it lives in a field initialiser in the mod's own assembly — so there is nothing to
    /// put back, and inventing one would be worse than saying so.
    /// </summary>
    private static void Released(
        string file, JsonObject last, JsonObject patch, string prefix, List<ModConfigChange> changes)
    {
        foreach (var (key, value) in last)
        {
            var path = prefix.Length == 0 ? key : $"{prefix}.{key}";
            var present = TryMatch(patch, key, out var now);

            if (value is JsonObject section)
            {
                Released(file, section, now as JsonObject ?? new JsonObject(), path, changes);
                continue;
            }

            if (!present || now is JsonObject)
                changes.Add(new ModConfigChange(file, path, ModConfigOutcome.Released));
        }
    }

    private static string? MatchKey(JsonObject target, string key)
    {
        if (target.ContainsKey(key)) return key;

        foreach (var (existing, _) in target)
            if (string.Equals(existing, key, StringComparison.OrdinalIgnoreCase))
                return existing;

        return null;
    }

    /// <summary>
    /// Case-insensitive lookup that distinguishes an absent key from one holding JSON null,
    /// which <see cref="JsonObject"/>'s indexer does not — both come back as a null node.
    /// The difference decides whether a value is the pack's first word about a key.
    /// </summary>
    private static bool TryMatch(JsonObject? source, string key, out JsonNode? value)
    {
        value = null;
        if (source is null) return false;

        var actual = MatchKey(source, key);
        if (actual is null) return false;

        value = source[actual];
        return true;
    }

    /// <summary>
    /// Reads a config file, saying both what it holds and whether Cairn may write it back.
    ///
    /// Strict first, then lenient. A file that parses only leniently has comments or trailing
    /// commas in it — the mod author documenting their own settings inside the file they
    /// ship — and rewriting it through a JSON writer would delete that documentation without
    /// asking. Better to be able to read it, refuse it, and say why.
    /// </summary>
    /// <summary>
    /// <see cref="Read"/> for <see cref="ModConfigSurvey"/>, which must offer a row only for
    /// a file a tick could actually reach — the same judgement, made once.
    /// </summary>
    internal static (JsonObject? Root, bool Rewritable, Message? Why) ReadForSurvey(string path)
    {
        var (root, _, rewritable, why) = Read(path);
        return (root, rewritable, why);
    }

    /// <summary>
    /// Whether this is one of ConfigLib's own files, which are edited as text rather than
    /// rebuilt from a tree. See <see cref="ModConfigYaml"/>.
    /// </summary>
    internal static bool IsYaml(string file) =>
        file.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase);

    private static (JsonObject? Root, string? Text, bool Rewritable, Message? Why) Read(string path)
    {
        var yaml = IsYaml(path);

        string text;
        try
        {
            if (!File.Exists(path))
                return yaml
                    // ConfigLib writes the whole file itself the first time the mod loads,
                    // and the version line it puts at the top decides whether the file is
                    // honoured at all — get that wrong and it overwrites every setting with
                    // its defaults. So this waits for the file rather than inventing one, and
                    // the cost is exactly one session: it is there from the next launch.
                    ? (null, null, false, new Message("modconfig-why-not-yet"))

                    // Absent JSON is the ordinary first launch, and the whole file is the
                    // pack's to write. The mod fills in whatever it does not find.
                    : (new JsonObject(), null, true, null);

            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, null, false, new Message("modconfig-why-unreadable"));
        }

        if (yaml)
        {
            var (values, why) = ModConfigYaml.Parse(text);
            return values is null ? (null, null, false, why) : (values, text, true, null);
        }

        try
        {
            if (JsonNode.Parse(text) is JsonObject strict) return (strict, text, true, null);
            return (null, null, false, new Message("modconfig-why-list"));
        }
        catch (JsonException)
        {
            // Fall through to the lenient read.
        }

        try
        {
            if (JsonNode.Parse(text, documentOptions: Lenient) is JsonObject)
                return (null, null, false, new Message("modconfig-why-comments"));

            return (null, null, false, new Message("modconfig-why-list"));
        }
        catch (JsonException)
        {
            return (null, null, false, new Message("modconfig-why-not-json"));
        }
    }

    /// <summary>
    /// Staged and moved, so an interrupted write never leaves a mod a half-written config —
    /// which is a mod that either refuses to load or silently reverts to its defaults.
    /// </summary>
    private static string Text(JsonObject root) => root.ToJsonString(Write);

    private static bool Save(string path, string content)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var staging = path + "." + Path.GetRandomFileName();
            File.WriteAllText(staging, content);
            File.Move(staging, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static Dictionary<string, JsonObject> LoadRecord(string dataPath) =>
        LoadDocument(Path.Combine(dataPath, RecordName));

    private static void SaveRecord(string dataPath, Dictionary<string, JsonObject> record) =>
        SaveDocument(Path.Combine(dataPath, RecordName), record);

    /// <summary>
    /// One of Cairn's own bookkeeping files in the data path — the record of what the pack
    /// asked for, or the baseline of what the mods first wrote. Both are file path against
    /// an object, and both are read the same forgiving way.
    /// </summary>
    private static Dictionary<string, JsonObject> LoadDocument(string path)
    {
        var document = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!File.Exists(path)) return document;
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return document;

            foreach (var (file, value) in root)
                if (value is JsonObject entry)
                    document[file] = entry;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A record that cannot be read means every key reads as a first word, which
            // takes the pack's values back from anybody who had moved them. Bad, and still
            // better than refusing to launch over a bookkeeping file. A baseline that cannot
            // be read costs the author a diff, and nothing else.
        }

        return document;
    }

    private static void SaveDocument(string path, Dictionary<string, JsonObject> document)
    {
        try
        {
            if (document.Count == 0)
            {
                File.Delete(path);
                return;
            }

            var root = new JsonObject();
            foreach (var (file, entry) in document.OrderBy(e => e.Key, StringComparer.Ordinal))
                root[file] = entry.DeepClone();

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var staging = path + "." + Path.GetRandomFileName();
            File.WriteAllText(staging, root.ToJsonString(Write));
            File.Move(staging, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Next launch reads the older document, or none. See LoadDocument.
        }
    }

    /// <summary>
    /// Why this path may not be used, or null when it may.
    ///
    /// A manifest arrives from somebody else, and this value is joined onto a path on the
    /// machine that imported it. <c>..</c> is the reason this function exists — the same
    /// reason <see cref="Packs.PackId"/> exists — and the rest is about a manifest meaning
    /// one thing on every machine: backslashes are a separator on Windows and an ordinary
    /// filename character elsewhere, so a pack written on one would quietly write a file
    /// called <c>XLeveling\mining.json</c> on the other.
    /// </summary>
    public static Message? PathProblem(string? file)
    {
        if (string.IsNullOrWhiteSpace(file)) return new Message("modconfig-path-no-name");
        if (file.Contains('\\')) return new Message("modconfig-path-backslash");
        if (Path.IsPathRooted(file) || file.Contains(':'))
            return new Message("modconfig-path-absolute");

        var parts = file.Split('/');
        if (parts.Any(p => p.Length == 0)) return new Message("modconfig-path-empty-segment");
        if (parts.Any(p => p is "." or "..")) return new Message("modconfig-path-outside");
        if (parts.Any(p => p.Any(Path.GetInvalidFileNameChars().Contains)))
            return new Message("modconfig-path-bad-characters");

        // JSON, and ConfigLib's flat YAML. Everything else — an .ini, a mod's own YAML with
        // real structure in it — has no honest way to have one value merged into it while
        // the rest of the file is preserved. Refusing loudly is the difference between a
        // feature that does not cover a mod and one that appears to and does nothing.
        if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !IsYaml(file))
            return new Message("modconfig-path-extension");

        return null;
    }

    private static string Native(string file) =>
        file.Replace('/', Path.DirectorySeparatorChar);
}
