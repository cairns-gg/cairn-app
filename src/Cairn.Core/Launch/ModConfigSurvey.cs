using System.Text.Json.Nodes;

namespace Cairn.Core.Launch;

/// <summary>
/// One setting in one mod's config file, as the Mod config tab shows it.
/// </summary>
/// <param name="Path">
/// The key as segments rather than a dotted string. Config keys are usually plain, but
/// nothing stops one containing a dot, and a dotted string would then be ambiguous about
/// whether it names one key or two levels — silently writing the wrong shape into somebody
/// else's config file. The dotted form exists for display only.
/// </param>
public sealed record ModConfigSetting(
    string File,
    IReadOnlyList<string> Path,
    JsonNode? Current,
    JsonNode? Baseline,
    JsonNode? Declared,
    bool HasBaseline)
{
    /// <summary>The key as a person reads it: <c>Rooms.Enabled</c>.</summary>
    public string Key => string.Join('.', Path);

    /// <summary>Carried by the pack, which is the tick in the tab.</summary>
    public bool IsCarried => Declared is not null;

    /// <summary>
    /// Differs from what the mod first wrote — which is to say, somebody changed it here.
    /// The default list, because it is a short answer to "what did I actually tune?".
    ///
    /// False where there is no baseline: a file first seen after this feature existed has
    /// one, an older pack's does not, and claiming "changed" of a value nothing has ever
    /// observed would put the whole file in the list as though the author had tuned all of it.
    /// </summary>
    public bool IsChanged => HasBaseline && !JsonNode.DeepEquals(Current, Baseline);

    public string CurrentText => Text(Current);
    public string BaselineText => HasBaseline ? Text(Baseline) : "—";

    /// <summary>
    /// What the pack would carry, which is the current value — falling back to what it
    /// already declares for the row whose file no longer has the key.
    ///
    /// Without that fallback, a mod renaming a setting would erase the pack's old entry the
    /// moment somebody ticked anything else in the tab, silently and from the shared
    /// document. The row is visible and can be unticked, which is where that decision belongs.
    /// </summary>
    public JsonNode? WouldCarry => Current ?? Declared;

    private static string Text(JsonNode? node) => node switch
    {
        null => "—",
        JsonArray array => array.Count == 0 ? "[]" : $"[{string.Join(", ", array.Select(Text))}]",
        _ => node.ToJsonString().Trim('"'),
    };
}

/// <summary>
/// Reads a pack's mod config files and works out what an author has changed, so the answer
/// can be ticked into the manifest instead of transcribed from memory.
///
/// This is the authoring half of <see cref="ModConfigFiles"/>. That one takes what a pack
/// declares and puts it into the files; this one takes the files and offers what the pack
/// could declare. They meet at <see cref="Packs.PackManifest.ModConfig"/> and share nothing
/// else — deliberately, because applying runs on every launch on everybody's machine, and
/// this runs when one person opens one tab.
/// </summary>
public static class ModConfigSurvey
{
    /// <summary>
    /// Every setting worth showing: the ones changed from what the mod first wrote, and the
    /// ones the pack already carries.
    /// </summary>
    /// <param name="includeUnchanged">
    /// Every readable setting instead, which is the way out of the one thing the baseline
    /// cannot see. A value the author changed during the first session was already in the
    /// file before anything observed it, so it never reads as changed — and an author who
    /// knows they moved it needs to be able to find and tick it anyway.
    /// </param>
    public static IReadOnlyList<ModConfigSetting> Read(
        string dataPath,
        IReadOnlyDictionary<string, JsonObject>? declared,
        bool includeUnchanged = false)
    {
        var root = ModConfigFiles.DirectoryIn(dataPath);
        var baseline = ModConfigFiles.Baseline(dataPath);
        var settings = new List<ModConfigSetting>();

        foreach (var file in Files(root))
        {
            var (content, rewritable, _) = ReadConfig(System.IO.Path.Combine(root, Native(file)));

            // Only files a tick could actually reach. A YAML file, a file whose top level is
            // a list, or one with comments in it cannot be written by Apply, and offering a
            // row that silently would not land is worse than not offering it.
            if (content is null || !rewritable) continue;

            baseline.TryGetValue(file, out var was);

            JsonObject? carried = null;
            declared?.TryGetValue(file, out carried);

            Walk(file, content, was, carried, [], settings, includeUnchanged);
        }

        // Carried first, then changed, then the rest — and alphabetically within each, so a
        // list somebody is working down does not reorder under them as they tick.
        return settings
            .OrderByDescending(s => s.IsCarried)
            .ThenByDescending(s => s.IsChanged)
            .ThenBy(s => s.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void Walk(
        string file, JsonObject current, JsonObject? baseline, JsonObject? declared,
        List<string> prefix, List<ModConfigSetting> into, bool includeUnchanged)
    {
        foreach (var (key, value) in current)
        {
            prefix.Add(key);

            if (value is JsonObject section)
            {
                Walk(file, section, Child(baseline, key), Child(declared, key),
                     prefix, into, includeUnchanged);
                prefix.RemoveAt(prefix.Count - 1);
                continue;
            }

            var hasBaseline = TryGet(baseline, key, out var was);
            var isDeclared = TryGet(declared, key, out var carried);

            var setting = new ModConfigSetting(
                file, [.. prefix], value, was, isDeclared ? carried : null, hasBaseline);

            if (includeUnchanged || setting.IsCarried || setting.IsChanged) into.Add(setting);

            prefix.RemoveAt(prefix.Count - 1);
        }

        if (declared is null) return;

        // A key the pack carries that the file no longer has. Rare and worth surfacing: it
        // is what a mod renaming a setting looks like from here, and the row is the only
        // place somebody could untick it.
        foreach (var (key, carried) in declared)
        {
            if (carried is JsonObject || TryGet(current, key, out _)) continue;

            prefix.Add(key);
            into.Add(new ModConfigSetting(file, [.. prefix], null, null, carried, false));
            prefix.RemoveAt(prefix.Count - 1);
        }
    }

    /// <summary>
    /// Turns the ticked rows back into what the manifest carries.
    ///
    /// Built from the ticked rows alone rather than merged over what the manifest already
    /// says — the opposite of the hotkey tab, and for a reason that reverses there. A hotkey
    /// row exists only if the scan could read its registration, so rebuilding from rows would
    /// silently drop the ones it could not; every mod config row, by contrast, comes from a
    /// file that is right there, and a key the manifest names that no file has is surfaced as
    /// its own row rather than hidden. So the rows are the whole truth here, and rebuilding
    /// is what lets unticking remove one.
    /// </summary>
    public static Dictionary<string, JsonObject>? ToManifest(IEnumerable<ModConfigSetting> carried)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);

        foreach (var setting in carried)
        {
            if (setting.WouldCarry is null) continue;

            if (!result.TryGetValue(setting.File, out var target))
                result[setting.File] = target = new JsonObject();

            for (var i = 0; i < setting.Path.Count - 1; i++)
            {
                if (target[setting.Path[i]] is not JsonObject next)
                    target[setting.Path[i]] = next = new JsonObject();

                target = next;
            }

            target[setting.Path[^1]] = setting.WouldCarry.DeepClone();
        }

        // Null rather than empty, so a pack that carries none looks exactly as it did before
        // this existed — and reads as unchanged against what was published.
        return result.Count == 0 ? null : result;
    }

    private static IEnumerable<string> Files(string root)
    {
        if (!Directory.Exists(root)) yield break;

        List<string> found;
        try
        {
            found = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => ModConfigFiles.PathProblem(Path.GetFileName(f)) is null)
                .Where(f => Length(f) is > 0 and <= ModConfigFiles.MaxFileToSurvey)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var full in found)
        {
            var rel = System.IO.Path.GetRelativePath(root, full)
                .Replace(System.IO.Path.DirectorySeparatorChar, '/');

            // Cairn's own bookkeeping lives in the data path, not in ModConfig, so nothing
            // here should ever be one — but a name check is cheaper than the confusion of
            // offering somebody their own record file as a mod setting.
            if (rel is ModConfigFiles.RecordName or ModConfigFiles.BaselineName) continue;

            yield return rel;
        }
    }

    private static long Length(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return -1; }
    }

    private static JsonObject? Child(JsonObject? source, string key) =>
        TryGet(source, key, out var value) ? value as JsonObject : null;

    private static bool TryGet(JsonObject? source, string key, out JsonNode? value)
    {
        value = null;
        if (source is null) return false;

        if (source.ContainsKey(key))
        {
            value = source[key];
            return true;
        }

        foreach (var (existing, node) in source)
            if (string.Equals(existing, key, StringComparison.OrdinalIgnoreCase))
            {
                value = node;
                return true;
            }

        return false;
    }

    private static string Native(string file) =>
        file.Replace('/', System.IO.Path.DirectorySeparatorChar);

    private static (JsonObject? Root, bool Rewritable, Message? Why) ReadConfig(string path) =>
        ModConfigFiles.ReadForSurvey(path);
}
