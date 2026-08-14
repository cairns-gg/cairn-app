namespace Cairn.Core;

/// <summary>
/// Which language the application is speaking, and every string it says.
///
/// A static facade over one <see cref="LanguageCatalog"/>, in Core rather than in a front
/// end, because that is where the wording already lives. <c>ModConfigChange.Describe</c>,
/// <c>PackManifest.Validate</c> and the sync results are Core types that return finished
/// sentences on purpose — so the launcher and the CLI say the same thing about the same
/// event — and the alternative, having Core hand back codes for each front end to render,
/// would put the catalog back in Core anyway and lose that property on the way.
///
/// Static and mutable, like <see cref="UiScale"/> in the app and for the same reason: it is
/// one setting the whole process shares, and a change to it has to reach windows that are
/// already open. <see cref="Changed"/> is how they follow.
/// </summary>
public static class Lang
{
    /// <summary>
    /// English, before anybody asks for anything else.
    ///
    /// Loaded here rather than left empty until something initialises it, because the failure
    /// mode of the empty version is a window full of raw keys — and it would appear only in
    /// whatever forgot to call <see cref="Use"/>: a test host, a second window, a front end
    /// added later. Starting in English means forgetting to choose a language costs the
    /// translation and nothing else.
    /// </summary>
    private static LanguageCatalog _catalog = LanguageCatalog.Load(LanguageCatalog.Default);

    /// <summary>
    /// Raised when the language changes, so open windows can rebind rather than telling
    /// somebody to restart.
    /// </summary>
    public static event EventHandler? Changed;

    /// <summary>The language tag in force, e.g. <c>en</c>.</summary>
    public static string Current => _catalog.Code;

    /// <summary>Languages this build ships, English first because it is the complete one.</summary>
    public static IReadOnlyList<string> Available { get; } =
        LanguageCatalog.Shipped.OrderByDescending(c => c == LanguageCatalog.Default)
            .ThenBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Switches language, loading the catalog and its fallbacks.
    ///
    /// <paramref name="overrideDir"/> lets a translator point at a folder of lang files and
    /// see their work without building the project — see <see cref="LanguageCatalog.Load"/>.
    /// </summary>
    public static void Use(string? code, string? overrideDir = null)
    {
        var wanted = LanguageCatalog.Normalise(code);
        if (wanted == _catalog.Code && overrideDir is null) return;

        _catalog = LanguageCatalog.Load(wanted, overrideDir);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Puts the language back to English and forgets any loaded catalog. For tests, which
    /// share a process and would otherwise leave each other in whatever they last set.
    /// </summary>
    public static void Reset() => Use(LanguageCatalog.Default);

    /// <summary>The text for a key, with <c>{0}</c>-style placeholders filled in.</summary>
    public static string Get(string key, params object?[] args) => _catalog.Get(key, args);

    /// <summary>The text for a count, in whichever plural form this language wants.</summary>
    public static string Plural(string key, int count, params object?[] args) =>
        _catalog.Plural(key, count, args);

    /// <summary>Whether anything answers this key. Used by the coverage test, not by callers.</summary>
    public static bool Has(string key) => _catalog.Has(key);

    /// <summary>
    /// The catalog itself, so a test can assert against a language without switching the
    /// process into it.
    /// </summary>
    public static LanguageCatalog Catalog => _catalog;
}
