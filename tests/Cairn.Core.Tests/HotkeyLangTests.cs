using System.IO.Compression;
using System.Text;
using Cairn.Core.Hotkeys;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Turning what a mod registered into what a person can read.
///
/// Every case here came out of a real mod. Mods do not agree on any of it: some pass a
/// finished sentence, some a bare key, some a key with a domain, some a key belonging to a
/// library shipped in a different zip — and some assemble it at runtime, where the honest
/// answer is the hotkey's own id.
/// </summary>
public class HotkeyLangTests
{
    private static ZipArchive Zip(params (string Path, string Json)[] files)
    {
        var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, json) in files)
            {
                using var entry = zip.CreateEntry(path).Open();
                entry.Write(Encoding.UTF8.GetBytes(json));
            }

        buffer.Position = 0;
        return new ZipArchive(buffer, ZipArchiveMode.Read);
    }

    private static HotkeyLang From(params (string Path, string Json)[] files) =>
        HotkeyLang.From(Zip(files));

    /// <summary>scribe: registers "scribe:hotkey-scribepinhud", stores the key without the domain.</summary>
    [Fact]
    public void A_key_with_a_domain_finds_an_entry_stored_without_one()
    {
        var lang = From(("assets/scribe/lang/en.json",
            """{ "hotkey-scribepinhud": "Scribe Mod: Expand/collapse pinned-task HUD" }"""));

        Assert.Equal("Scribe Mod: Expand/collapse pinned-task HUD",
            lang.Resolve("scribe:hotkey-scribepinhud"));
    }

    /// <summary>xSkills: stores the key with its domain in front.</summary>
    [Fact]
    public void A_key_stored_with_a_domain_is_found_by_the_same_key()
    {
        var lang = From(("assets/xskills/lang/en.json",
            """{ "xskills:hotkey-cateyesonoff": "xSkills: Cat Eyes On/Off" }"""));

        Assert.Equal("xSkills: Cat Eyes On/Off", lang.Resolve("xskills:hotkey-cateyesonoff"));
    }

    /// <summary>XLib: registers only the tail of the key its file stores.</summary>
    [Fact]
    public void A_tail_matches_when_exactly_one_entry_ends_that_way()
    {
        var lang = From(("assets/xlib/lang/en.json",
            """{ "xlibfork:xpdrops-hotkey-editmode": "xSkills: Edit GUI layout" }"""));

        Assert.Equal("xSkills: Edit GUI layout", lang.Resolve("editmode"));
    }

    [Fact]
    public void An_ambiguous_tail_resolves_to_nothing()
    {
        var lang = From(("assets/a/lang/en.json",
            """{ "one:thing-toggle": "First toggle", "two:other-toggle": "Second toggle" }"""));

        // Two answers means we cannot say which, and a row labelled with the wrong mod's
        // sentence is worse than one labelled with its own id.
        Assert.Null(lang.Resolve("toggle"));
    }

    /// <summary>
    /// The convention several mods follow, and the only route for a name that never reached
    /// the IL because it was assembled at runtime.
    /// </summary>
    [Fact]
    public void A_missing_name_is_probed_for_by_convention()
    {
        var lang = From(("assets/scribe/lang/en.json",
            """{ "hotkey-scribepinhud": "Scribe Mod: pinned-task HUD" }"""));

        Assert.Equal("Scribe Mod: pinned-task HUD", lang.Label(null, "scribepinhud"));
    }

    [Fact]
    public void A_name_that_is_already_a_sentence_is_kept_as_it_is()
    {
        var lang = From(("assets/x/lang/en.json", """{ "unrelated": "Something else" }"""));

        // statushudcont and others pass the label straight in. It contains a colon, which an
        // earlier version read as "this is a key" and threw away.
        Assert.Equal("Textured Building: Open Config",
            lang.Label("Textured Building: Open Config", "texturedbuilding-config"));
    }

    [Fact]
    public void An_unresolvable_key_leaves_the_row_showing_its_code()
    {
        var lang = From(("assets/x/lang/en.json", """{ "unrelated": "Something else" }"""));

        // Not the key itself: "egocarib-mapmarkers:config-keybind-custom1" is an id with
        // punctuation, and the code is the shorter honest version of the same non-answer.
        Assert.Null(lang.Label("egocarib-mapmarkers:config-keybind-custom1", "egocarib_hkCustomMarker1"));
    }

    /// <summary>
    /// A lang key's domain names a mod, not the zip it was registered from: XLib registers
    /// keys in the <c>xskills</c> domain, whose translations ship in the xSkills zip.
    /// </summary>
    [Fact]
    public void Translations_from_one_mod_label_another_mods_hotkey()
    {
        var lang = new HotkeyLang();

        lang.ReadFrom(Zip(("assets/xlib/lang/en.json", """{ "unrelated": "x" }""")));
        lang.ReadFrom(Zip(("assets/xskills/lang/en.json",
            """{ "xskills:hotkey-effectframehotkey": "xSkills: Show/Hide effects HUD" }""")));

        // The registration is XLib's; the sentence is xSkills'. Resolving per zip leaves
        // this row showing an id for no reason but which file we happened to be holding.
        Assert.Equal("xSkills: Show/Hide effects HUD",
            lang.Label("xskills:hotkey-effectframehotkey", "effectframehotkey"));
    }

    [Fact]
    public void A_zip_with_no_translations_answers_nothing_rather_than_failing()
    {
        var lang = From(("assets/x/lang/de.json", """{ "a": "b" }"""));   // not English

        Assert.Equal(0, lang.Count);
        Assert.Null(lang.Label("a", "code"));
    }

    [Fact]
    public void Unreadable_json_costs_that_file_and_nothing_else()
    {
        var lang = From(
            ("assets/broken/lang/en.json", "{ not json"),
            ("assets/good/lang/en.json", """{ "hotkey-x": "Works" }"""));

        Assert.Equal("Works", lang.Label(null, "x"));
    }
}
