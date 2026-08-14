using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cairn.Core;

/// <summary>
/// Which language to start in, when nobody has said.
///
/// The order is the same shape as <see cref="CairnHome"/>'s, and for the same reason: the
/// environment always wins, then what this machine was told, then what can be inferred, then
/// a default that always works. Every step is re-read rather than cached, because the tests
/// move the environment per class.
/// </summary>
public static class LanguageChoice
{
    /// <summary>
    /// Outranks everything, including the saved preference.
    ///
    /// Not only for tests. A translator wants to see one screen in the language they are
    /// writing without changing a setting they then have to remember to change back, and a
    /// bug report is far easier to reproduce when the reporter can say which value they ran
    /// with than when it depends on a file nobody thought to ask about.
    /// </summary>
    public const string EnvironmentVariable = "CAIRN_LANG";

    /// <summary>
    /// A folder of lang files read in preference to the built-in ones, so a translation can
    /// be tested without a build. Empty means the shipped catalogs only.
    /// </summary>
    public const string OverrideVariable = "CAIRN_LANG_DIR";

    /// <summary>
    /// The language to use, and where the answer came from — the second half so Preferences
    /// can say "following Vintage Story" rather than showing a choice nobody made.
    /// </summary>
    public static (string Code, LanguageSource Source) Resolve(
        string? saved = null, string? gameSettingsPath = null)
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } env)
            return (LanguageCatalog.Normalise(env), LanguageSource.Environment);

        if (!string.IsNullOrWhiteSpace(saved))
            return (LanguageCatalog.Normalise(saved), LanguageSource.Chosen);

        // The language they already play Vintage Story in is a better guess than the one
        // their operating system is in — somebody running an English Windows in German has
        // already told the game which they would rather read.
        if (FromGame(gameSettingsPath) is { } game)
            return (game, LanguageSource.Game);

        return (LanguageCatalog.Normalise(SystemLanguage()), LanguageSource.System);
    }

    /// <summary>
    /// What the operating system is set to, or null when it will not say.
    ///
    /// CurrentUICulture is asked first and usually answers nothing: the whole repository builds
    /// with InvariantGlobalization, so there is no ICU and every culture reports as the
    /// invariant one. That made this step dead code — it could only ever return English —
    /// which is not what a chain with four steps in it is supposed to do.
    ///
    /// So the POSIX locale variables are read too, which is what they are for and what needs no
    /// ICU. LC_ALL wins, then LC_MESSAGES, then LANG, and "de_DE.UTF-8" is cut down to "de-de".
    /// A value of "C" or "POSIX" means the system declining to say rather than a language.
    ///
    /// Windows has no equivalent without ICU, so it falls through to English. That is a smaller
    /// loss than it looks: the step above this one reads the language Vintage Story is set to,
    /// which for this audience is both a better signal and one that works everywhere.
    /// </summary>
    private static string? SystemLanguage()
    {
        if (CultureInfo.CurrentUICulture.Name is { Length: > 0 } culture) return culture;

        foreach (var name in (string[])["LC_ALL", "LC_MESSAGES", "LANG"])
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value)) continue;

            // "de_DE.UTF-8@euro" — the language is everything before the encoding or modifier.
            var tag = value.Split('.', '@')[0];
            if (tag is "C" or "POSIX" or "") continue;

            return tag;
        }

        return null;
    }

    /// <summary>The folder of loose lang files to prefer, or null for the built-in ones.</summary>
    public static string? OverrideDir =>
        Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 } dir
        && Directory.Exists(dir)
            ? dir
            : null;

    /// <summary>
    /// The game's own <c>language</c> setting, out of the player's <c>clientsettings.json</c>.
    ///
    /// Read only, and out of the player's own data path rather than a pack's — the same file
    /// <see cref="Launch.PackData"/> seeds from and the same rule about never writing to it.
    /// A pack's copy would do as well but says nothing extra, and reaching into one to answer
    /// a question about the whole application would be the wrong direction.
    /// </summary>
    private static string? FromGame(string? path)
    {
        path ??= Path.Combine(GameInstall.DefaultDataPath, "clientsettings.json");

        try
        {
            if (!File.Exists(path)) return null;

            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return null;
            if (root["stringSettings"] is not JsonObject strings) return null;

            var code = strings["language"]?.GetValue<string>();

            return string.IsNullOrWhiteSpace(code) ? null : LanguageCatalog.Normalise(code);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException
                                      or FormatException or InvalidOperationException)
        {
            // Not a question worth failing over. The system culture answers next.
            return null;
        }
    }
}

/// <summary>Where a language came from, for a settings screen that would rather not lie.</summary>
public enum LanguageSource
{
    /// <summary><see cref="LanguageChoice.EnvironmentVariable"/>, which outranks the setting.</summary>
    Environment,

    /// <summary>Picked in Preferences and saved.</summary>
    Chosen,

    /// <summary>Taken from the language Vintage Story is set to.</summary>
    Game,

    /// <summary>The operating system's, which is the last guess before English.</summary>
    System,
}
