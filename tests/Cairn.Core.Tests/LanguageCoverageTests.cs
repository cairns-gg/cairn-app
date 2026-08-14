using System.Text.RegularExpressions;
using Cairn.Core;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Every key the source asks for exists, and every key the catalog holds is asked for.
///
/// This is what makes translating the rest of the application safe to do. A missing string
/// shows up as a raw key in a window, which nothing else notices — not a compiler, not a
/// test that renders a control and reads its text, because the control renders the key
/// perfectly happily. Sweeping several hundred strings across two front ends without this
/// would be sweeping them and hoping.
///
/// It reads the working tree rather than the built assembly, because the keys live in XAML
/// attributes that nothing else can see once compiled.
/// </summary>
public class LanguageCoverageTests
{
    /// <summary><c>{l:Tr some-key}</c> in markup, and Lang.Get/Plural("some-key") in code.</summary>
    private static readonly Regex InMarkup = new(@"\{l:Tr\s+([A-Za-z0-9_.-]+)\s*\}", RegexOptions.Compiled);

    /// <summary>
    /// Lang.Get/Plural("key") for a sentence rendered on the spot, and new Message("key") for
    /// one Core has decided on and left for whoever reads it to render — see
    /// <see cref="Message"/>. Both are references, and a scanner that knew only the first
    /// reported every composed reason as an unused string.
    /// </summary>
    private static readonly Regex InCode = new(
        @"(?:Lang\.(?:Get|Plural)|new Message)\(\s*""([A-Za-z0-9_.-]+)""", RegexOptions.Compiled);

    /// <summary>
    /// The plural forms CLDR names. A key used as Lang.Plural("carried") is answered by
    /// carried-one and carried-other, so neither of those is unused.
    /// </summary>
    private static readonly string[] PluralForms = ["zero", "one", "two", "few", "many", "other"];

    /// <summary>
    /// Walks up from the test assembly to the checkout, which is the only way to reach the
    /// .axaml files from a test that runs out of bin.
    /// </summary>
    private static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !Directory.EnumerateFiles(dir.FullName, "Cairn.sln*").Any())
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static IEnumerable<(string File, string Key)> Referenced()
    {
        var src = Path.Combine(Repo(), "src");

        foreach (var file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;

            var pattern = Path.GetExtension(file) switch
            {
                ".axaml" => InMarkup,
                ".cs" => InCode,
                _ => null,
            };

            if (pattern is null) continue;

            var name = Path.GetRelativePath(src, file);

            foreach (Match match in pattern.Matches(File.ReadAllText(file)))
                yield return (name, match.Groups[1].Value);
        }
    }

    [Fact]
    public void Every_key_the_source_asks_for_is_in_the_English_catalog()
    {
        var english = LanguageCatalog.Load(LanguageCatalog.Default);

        var missing = Referenced()
            .Where(r => !english.Has(r.Key) && !PluralForms.Any(f => english.Has($"{r.Key}-{f}")))
            .Select(r => $"{r.Key}  ({r.File})")
            .Distinct()
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "These keys are asked for and nothing answers them, so they render as themselves:\n  "
            + string.Join("\n  ", missing));
    }

    /// <summary>
    /// The other direction, which is the one that rots quietly. A string whose last caller
    /// was deleted stays in the file, gets translated by somebody working through it in good
    /// faith, and is never seen.
    /// </summary>
    [Fact]
    public void Every_string_in_the_English_catalog_is_asked_for_somewhere()
    {
        var english = LanguageCatalog.Load(LanguageCatalog.Default);
        var asked = Referenced().Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unused = Keys()
            .Where(k => !k.StartsWith('_'))
            .Where(k => !asked.Contains(k))
            // carried-one is used by whoever asks for carried.
            .Where(k => !PluralForms.Any(f =>
                k.EndsWith($"-{f}", StringComparison.Ordinal)
                && asked.Contains(k[..^(f.Length + 1)])))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(unused.Count == 0,
            "These strings are in the catalog and nothing asks for them:\n  "
            + string.Join("\n  ", unused));
    }

    /// <summary>
    /// The catalog is read for its keys here rather than exposed by LanguageCatalog, which
    /// has no reason to hand out its table to anything but a test.
    /// </summary>
    private static IEnumerable<string> Keys()
    {
        var path = Path.Combine(Repo(), "src", "Cairn.Core", "assets", "cairn", "lang", "en.json");

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));

        foreach (var property in document.RootElement.EnumerateObject())
            yield return property.Name;
    }

    /// <summary>
    /// A placeholder a translator cannot see is a placeholder they will drop. Keeping the
    /// count consistent is the catalog's job; this only checks the English is well-formed,
    /// since a stray brace here becomes a FormatException swallowed at every call site.
    /// </summary>
    [Fact]
    public void Every_English_string_is_a_usable_format_string()
    {
        var english = LanguageCatalog.Load(LanguageCatalog.Default);

        foreach (var key in Keys().Where(k => !k.StartsWith('_')))
        {
            var text = english.Get(key);

            // Ten arguments is more than any of these takes; the point is that formatting
            // does not throw, not what it produces.
            var formatted = string.Format(text, new object?[10]);
            Assert.NotNull(formatted);
        }
    }
}

/// <summary>
/// Every translation, shipped or drafted, held against the English it is a translation of.
///
/// A translator works in a text editor with no compiler and no types. The two mistakes that
/// costs are a key that does not exist — silently dead, the English shows instead and
/// nothing says why — and a placeholder dropped or renumbered, which is a value that does
/// not appear in the sentence somebody reads. Neither is visible without looking, and
/// looking is what this does.
/// </summary>
public class TranslationTests
{
    private static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !Directory.EnumerateFiles(dir.FullName, "Cairn.sln*").Any())
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>
    /// The shipped catalogs, plus the drafts in <c>translations/</c> that are loaded with
    /// CAIRN_LANG_DIR rather than embedded. A draft nobody checks is a draft that arrives
    /// broken on the day somebody promotes it.
    /// </summary>
    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        var repo = Repo();

        foreach (var dir in new[]
                 {
                     Path.Combine(repo, "src", "Cairn.Core", "assets", "cairn", "lang"),
                     Path.Combine(repo, "translations"),
                 })
        {
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
                if (Path.GetFileNameWithoutExtension(file) != LanguageCatalog.Default)
                    data.Add(file);
        }

        return data;
    }

    private static Dictionary<string, string> Read(string path)
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));

        return document.RootElement.EnumerateObject()
            .Where(p => p.Value.ValueKind == System.Text.Json.JsonValueKind.String)
            .ToDictionary(p => p.Name, p => p.Value.GetString()!);
    }

    private static Dictionary<string, string> English() => Read(
        Path.Combine(Repo(), "src", "Cairn.Core", "assets", "cairn", "lang", "en.json"));

    [Theory]
    [MemberData(nameof(Files))]
    public void Every_key_it_translates_is_one_English_has(string path)
    {
        var english = English();

        var unknown = Read(path).Keys
            .Where(k => !k.StartsWith('_') && !english.ContainsKey(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(unknown.Count == 0,
            $"{Path.GetFileName(path)} translates keys that do not exist, so they are never "
            + "shown and the English is used instead:\n  " + string.Join("\n  ", unknown));
    }

    /// <summary>
    /// Same set of placeholders, not the same order — a translation is allowed to move {1}
    /// in front of {0}, and several languages will need to. What it may not do is lose one.
    /// </summary>
    [Theory]
    [MemberData(nameof(Files))]
    public void Every_value_English_puts_in_a_sentence_survives_the_translation(string path)
    {
        var english = English();
        var holes = new Regex(@"\{(\d+)", RegexOptions.Compiled);

        var dropped = Read(path)
            .Where(e => !e.Key.StartsWith('_') && english.ContainsKey(e.Key))
            .Where(e => holes.Matches(e.Value).Select(m => m.Groups[1].Value).ToHashSet()
                        .SetEquals(holes.Matches(english[e.Key]).Select(m => m.Groups[1].Value)) is false)
            .Select(e => $"{e.Key}\n      en: {english[e.Key]}\n      {Path.GetFileNameWithoutExtension(path)}: {e.Value}")
            .ToList();

        Assert.True(dropped.Count == 0,
            $"{Path.GetFileName(path)} does not use the same values as the English:\n  "
            + string.Join("\n  ", dropped));
    }

    /// <summary>
    /// A plural key needs the forms its own language selects between, not the ones English
    /// does. French takes the singular for zero as well as one, which is the rule that made
    /// LanguageCatalog.PluralForm need a table rather than a comparison.
    /// </summary>
    [Theory]
    [MemberData(nameof(Files))]
    public void A_translated_plural_has_the_form_it_falls_back_to(string path)
    {
        var strings = Read(path);

        var missing = strings.Keys
            .Where(k => k.EndsWith("-one", StringComparison.Ordinal))
            .Select(k => k[..^"-one".Length] + "-other")
            .Where(other => !strings.ContainsKey(other))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            $"{Path.GetFileName(path)} has a singular with no plural beside it:\n  "
            + string.Join("\n  ", missing));
    }
}

/// <summary>
/// No catalog names a key twice.
///
/// JSON allows it and every reader here takes the last one, so a duplicate is invisible:
/// the file parses, the application runs, and one of the two strings is simply never used.
/// The sweep that moved the interface onto the catalog introduced one — Cancel, added once
/// for the small windows and again for the main one — and nothing noticed for eleven
/// commits.
/// </summary>
public class DuplicateKeyTests
{
    [Theory]
    [MemberData(nameof(TranslationTests.Files), MemberType = typeof(TranslationTests))]
    public void A_translation_names_no_key_twice(string path) => AssertNoDuplicates(path);

    [Fact]
    public void The_English_catalog_names_no_key_twice() => AssertNoDuplicates(
        Path.Combine(
            new DirectoryInfo(AppContext.BaseDirectory).FullName, "..", "..", "..", "..", "..",
            "src", "Cairn.Core", "assets", "cairn", "lang", "en.json"));

    private static void AssertNoDuplicates(string path)
    {
        var names = new Regex("""^\s*"([^"]+)"\s*:""", RegexOptions.Multiline)
            .Matches(File.ReadAllText(path))
            .Select(m => m.Groups[1].Value)
            .ToList();

        var twice = names.GroupBy(n => n, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} ({g.Count()} times)")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(twice.Count == 0,
            $"{Path.GetFileName(path)} names a key more than once, so all but the last are "
            + "dead:\n  " + string.Join("\n  ", twice));
    }
}
