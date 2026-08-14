using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Cairn.Core;

/// <summary>
/// One language's strings, and where to look when it has no answer.
///
/// The file format is the game's own: a flat JSON object of key against text, one file per
/// language, exactly what every Vintage Story mod ships in
/// <c>assets/&lt;domain&gt;/lang/&lt;code&gt;.json</c>. Cairn already reads that shape out of
/// mod archives in <see cref="Hotkeys.HotkeyLang"/>, and it is the format the people who
/// would write a translation for this already know how to write — which for a thing that
/// lives or dies on contributions is worth more than any technical merit .resx has.
///
/// Immutable, and separate from <see cref="Lang"/>, so a test can build one and look things
/// up in it without touching which language the application is currently in.
/// </summary>
public sealed class LanguageCatalog
{
    /// <summary>The language every other one falls back to, and the only complete one.</summary>
    public const string Default = "en";

    private readonly Dictionary<string, string> _strings;

    /// <summary>
    /// Consulted for anything this catalog has no entry for — the base language for a
    /// regional one, then English. A half-finished translation should show the English
    /// sentence for what it has not reached yet, not a raw key.
    /// </summary>
    private readonly LanguageCatalog? _fallback;

    public LanguageCatalog(
        string code, IReadOnlyDictionary<string, string> strings, LanguageCatalog? fallback = null)
    {
        Code = code;
        _strings = new Dictionary<string, string>(strings, StringComparer.OrdinalIgnoreCase);
        _fallback = fallback;
    }

    /// <summary>The language tag this holds, e.g. <c>de</c> or <c>pt-br</c>.</summary>
    public string Code { get; }

    /// <summary>
    /// What this language calls itself — "Deutsch", not "German" — for the picker.
    ///
    /// Out of the file rather than out of CultureInfo, which cannot answer: the whole
    /// repository builds with InvariantGlobalization, so there is no ICU and every culture
    /// reports its own tag as its name. Asking the translator to write it is the better answer
    /// anyway. They know what their language is called, and nobody else gets a vote.
    ///
    /// Underscored so <c>LanguageCoverageTests</c> passes over it: it is metadata about the
    /// file rather than a string the interface asks for by key.
    /// </summary>
    /// Its own entry only, never the fallback's: a file that has not named itself would
    /// otherwise report "English", and a picker listing English twice is worse than one
    /// listing a language by its tag.
    public string Name =>
        _strings.TryGetValue("_language-name", out var name) ? name : Code;

    /// <summary>Keys this catalog answers itself, not counting anything it falls back for.</summary>
    public int Count => _strings.Count;

    /// <summary>
    /// The text for a key, with <c>{0}</c>-style placeholders filled in.
    ///
    /// A key nothing answers comes back as itself. That is what the game's own Lang.Get does,
    /// and it is the right failure: a missing string shows up in the interface as
    /// <c>modconfig-set</c> rather than as an empty label somebody has to go looking for the
    /// cause of. <c>LanguageCoverageTests</c> is what stops one shipping.
    /// </summary>
    public string Get(string key, params object?[] args)
    {
        var text = Find(key) ?? key;
        if (args.Length == 0) return text;

        try
        {
            // The current culture, not the invariant one: a count in a German sentence should
            // be written the way German writes numbers.
            return string.Format(CultureInfo.CurrentCulture, text, args);
        }
        catch (FormatException)
        {
            // A translation with a stray brace in it, or one placeholder too many. One wrong
            // label is a bad translation; an exception out of a string lookup is a launcher
            // that will not open a window.
            return text;
        }
    }

    /// <summary>
    /// The text for a count, choosing between the plural forms a language has.
    ///
    /// English needs two — "1 setting carried", "3 settings carried" — and the codebase wrote
    /// that as <c>count == 1 ? "" : "s"</c> in a dozen places, which is a rule about English
    /// baked into a string nobody can translate around. So the key names the form:
    /// <c>modconfig-carried-one</c> and <c>modconfig-carried-other</c>.
    ///
    /// The forms are CLDR's, so the naming has room for the languages that need more than
    /// two — Russian and Polish select between <c>one</c>, <c>few</c> and <c>many</c> — and
    /// <see cref="PluralForm"/> is where those rules go when such a translation arrives.
    /// Until then it answers with the Germanic rule, which is right for the language that
    /// exists and wrong quietly rather than silently for the ones that do not.
    /// </summary>
    public string Plural(string key, int count, params object?[] args)
    {
        var form = $"{key}-{PluralForm(Code, count)}";

        // A translation that has not written the form this count needs falls to -other,
        // which every language has. A missing -other comes back as the key, like any
        // missing string.
        return Get(Has(form) ? form : $"{key}-other", args);
    }

    /// <summary>
    /// Which CLDR plural category a count falls in for a language.
    ///
    /// Two rules, because two are needed. English and its neighbours take the singular for one
    /// and the plural for everything else; French and Brazilian Portuguese take the singular
    /// for zero as well — "0 mod" and "1 mod", then "2 mods". A French translation written
    /// against the English rule says "0 mods", which is the kind of wrong that reads as
    /// nobody having looked.
    ///
    /// Anything not listed gets the English rule, which is a guess and is marked as one here
    /// rather than presented as a decision. Russian and Polish need -few and -many and are not
    /// covered; the keys already have somewhere for those to go, and this is where the rule
    /// goes with them.
    /// </summary>
    private static string PluralForm(string code, int count)
    {
        // The region can disagree with its own language, which is why this looks at the whole
        // tag before the base. CLDR gives pt the Brazilian rule — zero takes the singular — and
        // pt-PT the European one, where it does not. A player whose Vintage Story is set to
        // pt-pt reads Portuguese out of pt.json through the fallback, and would otherwise get
        // Brazilian agreement with European words.
        if (code == "pt-pt") return count == 1 ? "one" : "other";

        return code.Split('-')[0] switch
        {
            "fr" or "pt" => count is 0 or 1 ? "one" : "other",
            _ => count == 1 ? "one" : "other",
        };
    }

    private string? Find(string key) =>
        _strings.TryGetValue(key, out var text) ? text : _fallback?.Find(key);

    /// <summary>Whether this catalog or anything it falls back to answers this key.</summary>
    public bool Has(string key) => Find(key) is not null;

    // ---- loading ----

    /// <summary>
    /// Embedded rather than sitting beside the executable, because the app publishes trimmed
    /// and single-file-ish and a loose file is one more thing that can fail to be copied. A
    /// translator testing their work does not need to rebuild — see <see cref="Load"/>.
    /// </summary>
    private const string ResourcePrefix = "Cairn.Core.assets.cairn.lang.";

    /// <summary>Every language built into this assembly, in no particular order.</summary>
    public static IReadOnlyList<string> Shipped { get; } = typeof(LanguageCatalog).Assembly
        .GetManifestResourceNames()
        .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                    && n.EndsWith(".json", StringComparison.Ordinal))
        .Select(n => n[ResourcePrefix.Length..^".json".Length])
        .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>
    /// Every language that can be picked: what is built in, plus whatever loose files are in
    /// <paramref name="overrideDir"/>.
    ///
    /// The two have to be one list. CAIRN_LANG_DIR is the translator's whole workflow — drop
    /// fr.json in, restart, see your work — and it was only half of one: the file loaded
    /// perfectly well and the picker never offered it, so the only way to reach it was to also
    /// set CAIRN_LANG. A mechanism for people who are not going to build the project should not
    /// need a second environment variable to become visible.
    /// </summary>
    public static IReadOnlyList<string> Available(string? overrideDir = null)
    {
        var codes = new HashSet<string>(Shipped, StringComparer.OrdinalIgnoreCase);

        try
        {
            if (overrideDir is not null && Directory.Exists(overrideDir))
                foreach (var file in Directory.EnumerateFiles(overrideDir, "*.json"))
                    codes.Add(Normalise(Path.GetFileNameWithoutExtension(file)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The built-in ones are still selectable, which is the important half.
        }

        // English first because it is the complete one, then by tag so the list is stable.
        return [.. codes.OrderByDescending(c => c == Default).ThenBy(c => c, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The catalog for a language, with its fallback chain already built: a regional tag
    /// falls back to its base language, and everything falls back to English.
    ///
    /// <paramref name="overrideDir"/> is read before the embedded copy, one file per language,
    /// so somebody writing a translation can drop <c>de.json</c> in and restart rather than
    /// build the project. That is the difference between a translation somebody finishes and
    /// one they abandon.
    /// </summary>
    public static LanguageCatalog Load(string code, string? overrideDir = null)
    {
        code = Normalise(code);

        var english = code == Default
            ? null
            : new LanguageCatalog(Default, Read(Default, overrideDir));

        // "pt-br" is served by pt where it says nothing of its own.
        var dash = code.IndexOf('-');
        if (dash > 0)
        {
            var baseCode = code[..dash];
            var under = new LanguageCatalog(baseCode, Read(baseCode, overrideDir), english);
            return new LanguageCatalog(code, Read(code, overrideDir), under);
        }

        return new LanguageCatalog(code, Read(code, overrideDir), english);
    }

    private static Dictionary<string, string> Read(string code, string? overrideDir)
    {
        if (overrideDir is not null)
        {
            var path = Path.Combine(overrideDir, code + ".json");

            try
            {
                if (File.Exists(path)) return Parse(File.ReadAllText(path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                // Fall through to what is built in. A translator's half-written file should
                // leave the application in English, not broken.
            }
        }

        try
        {
            using var stream = typeof(LanguageCatalog).Assembly
                .GetManifestResourceStream(ResourcePrefix + code + ".json");

            if (stream is null) return [];

            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return [];
        }
    }

    private static Dictionary<string, string> Parse(string json)
    {
        var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Values only. The game's lang files are flat, and a nested object here would be a
        // file written against a different convention than the one this reads.
        foreach (var (key, value) in
                 JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [])
            if (value.ValueKind == JsonValueKind.String)
                table[key] = value.GetString()!;

        return table;
    }

    /// <summary>
    /// A language tag as this uses it: lower case, dashes, no more than language and region.
    /// <c>de-DE</c> and <c>de</c> name the same file here; nothing ships a de-DE.
    /// </summary>
    public static string Normalise(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return Default;

        var parts = code.Trim().Replace('_', '-').ToLowerInvariant().Split('-');

        return parts.Length == 1 ? parts[0] : $"{parts[0]}-{parts[1]}";
    }
}
