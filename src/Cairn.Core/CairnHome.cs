namespace Cairn.Core;

/// <summary>Which of the three answers decided where Cairn's root is.</summary>
public enum HomeSource
{
    /// <summary>Nothing said otherwise, so <c>~/.cairn</c>.</summary>
    Default,

    /// <summary>CAIRN_HOME was set.</summary>
    Environment,

    /// <summary>A pointer file at the default location named somewhere else.</summary>
    Pointer,
}

/// <param name="Root">Where Cairn's state lives. Always a value; never blank.</param>
/// <param name="Source">Which rule produced it.</param>
/// <param name="Problem">
/// What was wrong with a setting that was found and could not be used, or null. A problem
/// never stops <see cref="CairnHome.Resolve()"/> returning a usable root — it falls back —
/// because the alternative is a property that throws, and <c>Root</c> is read from
/// everywhere. Somebody has to report it instead: see <see cref="CairnHome.Preflight"/>.
/// </param>
public sealed record HomeResolution(string Root, HomeSource Source, string? Problem);

/// <summary>
/// Where Cairn keeps its state, and how that is decided.
///
/// CAIRN_HOME has always moved the root and still does. What it cannot do is stick: an
/// environment variable set in a shell does not reach a Start-menu launch, a desktop entry,
/// an .app bundle or a cairn:// activation, which is how the launcher is actually started.
/// So a file can name the root as well.
///
/// The file lives at the *default* location and holds one absolute path. It cannot live in
/// settings.json, which is inside the root it would be configuring — and would not survive
/// there anyway, since UiScale.Save serialises every key it knows and moves the result into
/// place. Plain text rather than JSON because this is read before anything else works, and
/// has to be repairable by hand by somebody whose data is currently unreachable.
///
/// Order is CAIRN_HOME, then the pointer, then the default, and the environment has to keep
/// winning: ServerUnit writes <c>Environment=CAIRN_HOME=</c> into systemd units, so a
/// pointer that outranked it would quietly redirect a running server.
/// </summary>
public static class CairnHome
{
    /// <summary>The name of the pointer file, inside the default root.</summary>
    public const string PointerName = "home";

    /// <summary>
    /// Where the root is when nothing says otherwise.
    ///
    /// CAIRN_DEFAULT_HOME moves it, which is not the same thing as CAIRN_HOME and exists for
    /// a different reason. CAIRN_HOME overrides the answer and outranks the pointer file, so
    /// a sandbox built on it exercises the one branch almost nobody takes — and, since the
    /// launcher will not offer to move a root the environment decided, made the move
    /// impossible to try in the very setup meant for trying things. This moves the *default*
    /// instead, so a sandboxed run behaves exactly like a real one: the pointer works, the
    /// move works, and none of it is near the developer's own ~/.cairn.
    /// </summary>
    public static string DefaultRoot
    {
        get
        {
            var sandbox = Environment.GetEnvironmentVariable("CAIRN_DEFAULT_HOME");

            return string.IsNullOrWhiteSpace(sandbox)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cairn")
                : sandbox.Trim();
        }
    }

    /// <summary>
    /// The pointer file, which is always looked for here and not under the root it names —
    /// a pointer inside the directory it points at could only ever be found by somebody who
    /// already knew the answer.
    /// </summary>
    public static string PointerPath => Path.Combine(DefaultRoot, PointerName);

    /// <summary>
    /// Where the root is, and why.
    ///
    /// Evaluated every time rather than cached, which is a deliberate cost: the test suites
    /// set CAIRN_HOME per class and clear it again afterwards, so a root read once at
    /// start-up would be the first test's answer for the whole run. The cost is one small
    /// file read, and only when CAIRN_HOME is unset.
    /// </summary>
    public static HomeResolution Resolve() =>
        Resolve(Environment.GetEnvironmentVariable("CAIRN_HOME"), DefaultRoot, ReadPointer);

    /// <summary>
    /// The rules, with the three things they read passed in, so they can be tested without
    /// a real home directory to stand in. Everything above is the wiring.
    /// </summary>
    /// <param name="environment">CAIRN_HOME's value, or null when unset.</param>
    /// <param name="defaultRoot">Where the root is when nothing says otherwise.</param>
    /// <param name="readPointer">
    /// Reads the pointer file, returning null when there is none. Anything it throws is a
    /// problem to report rather than one to propagate: a root that cannot be worked out is
    /// still better reported than thrown from a property.
    /// </param>
    public static HomeResolution Resolve(
        string? environment, string defaultRoot, Func<string, string?> readPointer)
    {
        // Set-but-blank is treated as unset. It used to be taken literally, which made Root
        // the empty string and every path under it relative to the working directory —
        // packs at ./packs, wherever the launcher happened to be started from.
        if (!string.IsNullOrWhiteSpace(environment))
            return new HomeResolution(environment.Trim(), HomeSource.Environment, null);

        var pointerPath = Path.Combine(defaultRoot, PointerName);

        string? pointed;
        try
        {
            pointed = readPointer(pointerPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new HomeResolution(defaultRoot, HomeSource.Default,
                $"{pointerPath} could not be read ({e.Message}), so the default is being used.");
        }

        if (pointed is null) return new HomeResolution(defaultRoot, HomeSource.Default, null);

        // A hand-edited file has a trailing newline, and an empty one is a half-finished
        // edit rather than an instruction to use the default.
        var trimmed = pointed.Trim();

        if (trimmed.Length == 0)
            return new HomeResolution(defaultRoot, HomeSource.Default,
                $"{pointerPath} is empty, so the default is being used.");

        // Relative would resolve against the working directory, which for a launcher started
        // from a Dock tile or a protocol handler is not a place anybody chose.
        if (!Path.IsPathFullyQualified(trimmed))
            return new HomeResolution(defaultRoot, HomeSource.Default,
                Lang.Get("home-pointer-relative", pointerPath, trimmed));

        return new HomeResolution(trimmed, HomeSource.Pointer, null);
    }

    /// <summary>
    /// What is wrong with the current setting, or null when nothing is.
    ///
    /// Separate from <see cref="Resolve()"/> and from <c>Root</c> because it does the one
    /// check that must not happen on every path lookup, and because the answer is a
    /// front-end's to act on: refusing to start is right, and a property cannot do it.
    ///
    /// A pointer at a directory that is not there is the case worth catching. An unplugged
    /// disk, a network share that is down, a drive letter that moved — falling back to the
    /// default would start Cairn with an empty root, which reads as "everything is gone" and
    /// invites re-downloading the game beside data that is perfectly fine.
    ///
    /// Directory.CreateDirectory is emphatically not this check. On Windows <c>D:\cairn</c>
    /// with no D: fails; on macOS <c>/Volumes/Gone/cairn</c> cheerfully creates a directory
    /// on the boot volume, which is the same silent empty root wearing a different hat.
    /// </summary>
    public static string? Preflight() => Preflight(Resolve(), Directory.Exists);

    /// <param name="exists">Whether a directory is there, injected for testing.</param>
    public static string? Preflight(HomeResolution resolution, Func<string, bool> exists)
    {
        if (resolution.Problem is not null) return resolution.Problem;

        // Only for a pointer. The default not existing yet is an ordinary first run, and
        // CAIRN_HOME is set by somebody who is looking at what they set it to.
        if (resolution.Source is HomeSource.Pointer && !exists(resolution.Root))
            return Lang.Get("home-pointer-missing", PointerPath, resolution.Root);

        return null;
    }

    /// <summary>
    /// Same directory, allowing for a trailing separator and for Windows not caring about
    /// case. Its own helper rather than string equality, because "the same place written
    /// differently" is exactly the shape of the bug it exists to avoid.
    /// </summary>
    private static bool PathsEqual(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Null when there is no pointer file, which is the ordinary case.</summary>
    private static string? ReadPointer(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : null;

    /// <summary>
    /// Points the root somewhere else, or removes the pointer when <paramref name="root"/>
    /// is null. Moves nothing: this says where Cairn should look, and what is already in the
    /// old place stays there.
    /// </summary>
    public static void SetPointer(string? root)
    {
        // Pointing at the default is the same as not pointing anywhere, and saying it the
        // long way has a cost: `home` would report the root as decided by a file, and moving
        // back to ~/.cairn would leave that directory holding a note about itself. Somebody
        // who has moved home again should be back where they started, not one indirection
        // away from it.
        if (root is null || PathsEqual(root, DefaultRoot))
        {
            File.Delete(PointerPath);
            return;
        }

        // The directory holding the pointer, not the one it names — the target may well be
        // on a disk that is the whole reason for doing this.
        Directory.CreateDirectory(DefaultRoot);

        // A newline because it is a text file people will open, and every editor adds one
        // anyway; Resolve trims it back off.
        File.WriteAllText(PointerPath, root + System.Environment.NewLine);
    }
}
