using Cairn.Core;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// The string catalog, which every sentence a person reads now goes through.
/// </summary>
public class LanguageCatalogTests
{
    private static LanguageCatalog Catalog(
        string code, Dictionary<string, string> strings, LanguageCatalog? fallback = null) =>
        new(code, strings, fallback);

    [Fact]
    public void A_key_comes_back_as_its_text()
    {
        var lang = Catalog("en", new() { ["button-play"] = "Play" });

        Assert.Equal("Play", lang.Get("button-play"));
    }

    /// <summary>
    /// The game's own Lang.Get does this, and it is the right failure: a missing string shows
    /// up in the interface as the key rather than as an empty label somebody has to go
    /// hunting for the cause of.
    /// </summary>
    [Fact]
    public void A_key_nothing_answers_comes_back_as_itself()
    {
        Assert.Equal("button-play", Catalog("en", []).Get("button-play"));
    }

    [Fact]
    public void Placeholders_are_filled_in()
    {
        var lang = Catalog("en", new() { ["modconfig-set"] = "{0}: set {1}" });

        Assert.Equal("terrainslabs.json: set compatibleMods",
            lang.Get("modconfig-set", "terrainslabs.json", "compatibleMods"));
    }

    /// <summary>
    /// One bad translation is one wrong label. An exception out of a string lookup is a
    /// launcher that will not open a window.
    /// </summary>
    [Fact]
    public void A_translation_with_a_broken_placeholder_costs_one_label()
    {
        var lang = Catalog("en", new() { ["oops"] = "a stray { brace" });

        Assert.Equal("a stray { brace", lang.Get("oops", "x"));
    }

    // ---- falling back ----

    [Fact]
    public void What_a_translation_has_not_reached_yet_shows_in_English()
    {
        var english = Catalog("en", new() { ["a"] = "Play", ["b"] = "Share" });
        var german = Catalog("de", new() { ["a"] = "Spielen" }, english);

        Assert.Equal("Spielen", german.Get("a"));

        // Not "b". A half-finished translation should read as half-finished, not as broken.
        Assert.Equal("Share", german.Get("b"));
    }

    [Fact]
    public void A_regional_language_falls_back_to_its_base_then_to_English()
    {
        var english = Catalog("en", new() { ["a"] = "colour", ["c"] = "third" });
        var portuguese = Catalog("pt", new() { ["a"] = "cor", ["b"] = "segundo" }, english);
        var brazilian = Catalog("pt-br", new() { ["a"] = "côr" }, portuguese);

        Assert.Equal("côr", brazilian.Get("a"));
        Assert.Equal("segundo", brazilian.Get("b"));
        Assert.Equal("third", brazilian.Get("c"));
    }

    // ---- plurals ----

    /// <summary>
    /// The codebase wrote this as count == 1 ? "" : "s" in a dozen places, which is a rule
    /// about English baked into a string nobody can translate around.
    /// </summary>
    [Theory]
    [InlineData(0, "0 settings")]
    [InlineData(1, "1 setting")]
    [InlineData(2, "2 settings")]
    public void A_count_chooses_its_plural_form(int count, string expected)
    {
        var lang = Catalog("en", new()
        {
            ["carried-one"] = "{0} setting",
            ["carried-other"] = "{0} settings",
        });

        Assert.Equal(expected, lang.Plural("carried", count, count));
    }

    /// <summary>
    /// Russian and Polish select between one, few and many. Nothing implements those rules
    /// yet — no such translation exists — but a language that asks for a form this build
    /// cannot choose must still read, so -other answers.
    /// </summary>
    [Fact]
    public void A_form_this_build_cannot_choose_falls_to_other()
    {
        var lang = Catalog("ru", new() { ["carried-other"] = "{0} настроек" });

        Assert.Equal("2 настроек", lang.Plural("carried", 2, 2));
    }

    // ---- tags ----

    [Theory]
    [InlineData("de", "de")]
    [InlineData("DE", "de")]
    [InlineData("pt-BR", "pt-br")]
    [InlineData("pt_BR", "pt-br")]
    [InlineData("zh-Hans-CN", "zh-hans")]
    [InlineData("", "en")]
    [InlineData(null, "en")]
    public void Language_tags_are_read_the_same_however_they_are_written(string? given, string expected)
    {
        Assert.Equal(expected, LanguageCatalog.Normalise(given));
    }

    // ---- what ships ----

    /// <summary>
    /// The English catalog is embedded rather than copied beside the executable, because all
    /// three published projects trim and the app ships as a zip somebody unpacks. A loose file
    /// would arrive missing as a window full of raw keys, and nothing else would notice.
    /// </summary>
    [Fact]
    public void English_is_built_into_the_assembly()
    {
        Assert.Contains("en", LanguageCatalog.Shipped);

        var english = LanguageCatalog.Load("en");

        Assert.True(english.Count > 0);
        Assert.Equal("Mod config", english.Get("tab-modconfig"));
    }

    [Fact]
    public void A_language_that_ships_no_file_reads_entirely_in_English()
    {
        var klingon = LanguageCatalog.Load("tlh");

        Assert.Equal("Mod config", klingon.Get("tab-modconfig"));
    }

    /// <summary>
    /// A translator should be able to drop a file in and restart, rather than build the
    /// project. That is the difference between a translation somebody finishes and one they
    /// abandon.
    /// </summary>
    [Fact]
    public void A_loose_file_is_read_in_preference_to_the_built_in_one()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cairn-lang-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);

        try
        {
            File.WriteAllText(Path.Combine(dir, "de.json"), """{ "tab-modconfig": "Mod-Konfiguration" }""");

            var german = LanguageCatalog.Load("de", dir);

            Assert.Equal("Mod-Konfiguration", german.Get("tab-modconfig"));

            // And everything it does not say still falls through to English.
            Assert.Equal("Hotkeys", german.Get("tab-hotkeys"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_half_written_file_leaves_the_application_in_English()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cairn-lang-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);

        try
        {
            File.WriteAllText(Path.Combine(dir, "de.json"), "{ this is not json");

            Assert.Equal("Mod config", LanguageCatalog.Load("de", dir).Get("tab-modconfig"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>
/// Which language to start in. Same shape as CairnHome's order, and for the same reason:
/// the environment always wins, then what this machine was told, then what can be inferred.
/// </summary>
public class LanguageChoiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "cairn-langchoice-" + Guid.NewGuid().ToString("n")[..8]);

    public LanguageChoiceTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable(LanguageChoice.EnvironmentVariable, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(LanguageChoice.EnvironmentVariable, null);
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string GameSettings(string json)
    {
        var path = Path.Combine(_dir, "clientsettings.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void The_environment_outranks_the_saved_choice()
    {
        Environment.SetEnvironmentVariable(LanguageChoice.EnvironmentVariable, "fr");

        var (code, source) = LanguageChoice.Resolve(saved: "de");

        Assert.Equal("fr", code);
        Assert.Equal(LanguageSource.Environment, source);
    }

    [Fact]
    public void A_saved_choice_outranks_what_can_be_inferred()
    {
        var settings = GameSettings("""{ "stringSettings": { "language": "fr" } }""");

        var (code, source) = LanguageChoice.Resolve(saved: "de", gameSettingsPath: settings);

        Assert.Equal("de", code);
        Assert.Equal(LanguageSource.Chosen, source);
    }

    /// <summary>
    /// Somebody running an English Windows in German has already told the game which they
    /// would rather read, which is a better guess than the operating system's.
    /// </summary>
    [Fact]
    public void The_language_Vintage_Story_is_set_to_is_the_next_best_guess()
    {
        var settings = GameSettings("""{ "stringSettings": { "language": "de", "playername": "x" } }""");

        var (code, source) = LanguageChoice.Resolve(gameSettingsPath: settings);

        Assert.Equal("de", code);
        Assert.Equal(LanguageSource.Game, source);
    }

    [Fact]
    public void A_game_with_no_settings_file_falls_through_to_the_system()
    {
        var (_, source) = LanguageChoice.Resolve(
            gameSettingsPath: Path.Combine(_dir, "nothing-here.json"));

        Assert.Equal(LanguageSource.System, source);
    }

    [Fact]
    public void A_settings_file_that_will_not_parse_is_not_worth_failing_over()
    {
        var settings = GameSettings("{ not json at all");

        var (_, source) = LanguageChoice.Resolve(gameSettingsPath: settings);

        Assert.Equal(LanguageSource.System, source);
    }
}
