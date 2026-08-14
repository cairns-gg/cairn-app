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

    private static readonly Regex InCode = new(
        @"Lang\.(?:Get|Plural)\(\s*""([A-Za-z0-9_.-]+)""", RegexOptions.Compiled);

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
