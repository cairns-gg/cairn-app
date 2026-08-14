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
///
/// <para><b>What does not go through here.</b> Every exception Core throws can reach a person
/// — the front ends catch <c>Exception</c> and show <c>e.Message</c> in eighteen places — so
/// "is it an exception?" is not the question. The question is whether a person can cause it.
///
/// A download that fails its checksum, a ModDB id that does not exist, a sign-in that
/// expires, a move that runs out of disk: somebody did something, and what they are told is
/// the product speaking to them. Those are translated.
///
/// A guard that fires because a path escaped the store, or because an id generator ran out,
/// or because an SDK unpacked without an SDK in it, is Cairn saying it has a bug. Those stay
/// in English on purpose, and each one says so where it is thrown. The audience for that
/// sentence is whoever reads the issue it gets pasted into, and a translated one is worse
/// there: it cannot be searched for, and the person best placed to fix it cannot read it.
/// Leaving them is a decision, not an omission.</para>
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

    /// <summary>
    /// Languages that can be chosen, English first because it is the complete one. Includes
    /// loose files in CAIRN_LANG_DIR, so a translation being worked on is selectable.
    ///
    /// Computed rather than held: a file dropped in while the launcher is open should be
    /// offered by the next Preferences window rather than only after a restart.
    /// </summary>
    public static IReadOnlyList<string> Available =>
        LanguageCatalog.Available(LanguageChoice.OverrideDir);

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
