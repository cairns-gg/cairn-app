using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Cairn.Core.Runtime;

namespace Cairn.Core;

/// <summary>
/// A located Vintage Story installation.
/// </summary>
public sealed class GameInstall
{
    /// <summary>Assumed when the game ships no runtimeconfig we can read.</summary>
    private static readonly Version FallbackFramework = new(10, 0, 0);

    public required string Directory { get; init; }
    public required string Executable { get; init; }

    /// <summary>Version read from VintagestoryAPI.dll metadata, e.g. "1.22.5".</summary>
    public required string Version { get; init; }

    /// <summary>
    /// What this build is, when it is not the stock game — "Optimum", say. Null for a
    /// plain install, which is nearly all of them.
    ///
    /// Read from a <c>.cairn-variant</c> file dropped in the directory rather than guessed
    /// from the folder name or the version. A modified client reports whatever version it
    /// was forked from, so it is indistinguishable from the real thing by metadata alone —
    /// and a variant silently satisfying every pack that asks for that version is the one
    /// outcome worth ruling out by construction.
    /// </summary>
    public string? Variant { get; init; }

    public bool IsVariant => !string.IsNullOrWhiteSpace(Variant);

    /// <summary>"1.22.5" or "1.22.5 (Optimum)", for anywhere an install is named.</summary>
    public string Describe() => IsVariant ? $"{Version} ({Variant})" : Version;

    /// <summary>Marks a directory as holding something other than the stock game.</summary>
    public const string VariantMarker = ".cairn-variant";

    /// <summary>
    /// Architecture of the game's apphost. The published clients are x64 on every
    /// platform, so this exists to be checked against an available runtime rather than
    /// to support other targets.
    /// </summary>
    public required ExecutableArch Architecture { get; init; }

    /// <summary>
    /// Microsoft.NETCore.App version the game asks for, taken from
    /// Vintagestory.runtimeconfig.json. The game is framework-dependent — it bundles no
    /// runtime — so this has to be satisfied by an install on the machine.
    /// </summary>
    public required Version RequiredFramework { get; init; }

    /// <summary>
    /// The data path the game would pick on its own. Computed with the same call the
    /// game makes (GamePaths' static ctor), so it agrees on every platform rather
    /// than us hardcoding per-OS guesses.
    /// </summary>
    public static string DefaultDataPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.DoNotVerify),
        "VintagestoryData");

    private static string ExecutableName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Vintagestory.exe" : "Vintagestory";

    /// <summary>Candidate install directories, best guess first. VINTAGE_STORY always wins.</summary>
    public static IEnumerable<string> CandidateDirectories()
    {
        var env = Environment.GetEnvironmentVariable("VINTAGE_STORY");
        if (!string.IsNullOrWhiteSpace(env)) yield return env;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return "/Applications/Vintagestory.app";
            yield return Path.Combine(home, "Applications", "Vintagestory.app");

            // Extracted tarballs are commonly renamed with a version suffix, e.g.
            // "vintagestory-1.22.5.app", so scan rather than only probing exact names.
            foreach (var dir in ScanFor("/Applications", "vintagestory"))
                yield return dir;
            foreach (var dir in ScanFor(Path.Combine(home, "Applications"), "vintagestory"))
                yield return dir;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolderOption.DoNotVerify);
            yield return Path.Combine(appdata, "Vintagestory");
            yield return @"C:\Program Files\Vintagestory";
        }
        else
        {
            yield return "/usr/share/vintagestory";
            yield return "/usr/lib/vintagestory";
            yield return Path.Combine(home, ".local", "share", "vintagestory");

            foreach (var dir in ScanFor(Path.Combine(home, ".local", "share"), "vintagestory"))
                yield return dir;
        }
    }

    /// <summary>Subdirectories of <paramref name="parent"/> whose name starts with the prefix.</summary>
    private static IEnumerable<string> ScanFor(string parent, string prefix)
    {
        string[] entries;
        try
        {
            if (!System.IO.Directory.Exists(parent)) return [];
            entries = System.IO.Directory.GetDirectories(parent);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return entries
            .Where(d => Path.GetFileName(d).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            // Newest-looking first, so a versioned name beats an older one.
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase);
    }

    public static GameInstall? TryLocate()
    {
        foreach (var dir in CandidateDirectories())
        {
            var found = TryAt(dir);
            if (found is not null) return found;
        }

        return null;
    }

    public static GameInstall? TryAt(string dir)
    {
        if (!System.IO.Directory.Exists(dir)) return null;

        var exe = Path.Combine(dir, ExecutableName);
        var api = Path.Combine(dir, "VintagestoryAPI.dll");
        if (!File.Exists(exe) || !File.Exists(api)) return null;

        return new GameInstall
        {
            Directory = dir,
            Executable = exe,
            Version = ReadVersion(api),
            Architecture = ExecutableImage.ReadArchitecture(exe),
            RequiredFramework = ReadRequiredFramework(dir) ?? FallbackFramework,
            Variant = ReadVariant(dir),
        };
    }

    /// <summary>Parses runtimeOptions.framework.version out of the game's runtimeconfig.</summary>
    private static Version? ReadRequiredFramework(string dir)
    {
        var path = Path.Combine(dir, "Vintagestory.runtimeconfig.json");
        if (!File.Exists(path)) return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("runtimeOptions", out var options)) return null;
            if (!options.TryGetProperty("framework", out var framework)) return null;
            if (!framework.TryGetProperty("version", out var version)) return null;

            // Fully qualified: the Version property on this class shadows the type here.
            return System.Version.TryParse(version.GetString(), out var parsed) ? parsed : null;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads metadata only — never loads the assembly, so this works from a process of a
    /// different architecture than the game.
    ///
    /// Prefers GameVersion.ShortGameVersion, the constant the game itself reports. The
    /// assembly attributes are not trustworthy across releases: 1.22.5 carries
    /// AssemblyVersion 1.22.5.0, but 1.21.5 carries 1.0.0.0 with FileVersion 1.21.0.
    /// </summary>
    /// <summary>
    /// The label in the directory's variant marker, or null for a stock install.
    ///
    /// Silent about an unreadable one: a marker nobody can read means the same thing as no
    /// marker for every decision that follows, and refusing to see an install over it would
    /// be worse than treating it as ordinary.
    /// </summary>
    private static string? ReadVariant(string dir)
    {
        try
        {
            var path = Path.Combine(dir, VariantMarker);
            if (!File.Exists(path)) return null;

            var label = File.ReadAllText(path).Trim();

            // A marker with nothing in it still says "not the stock game", so it names
            // itself after its folder rather than reading as ordinary.
            return label.Length > 0 ? label : Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ReadVersion(string apiDllPath)
    {
        var declared = AssemblyConstantReader.ReadStringConstant(
            apiDllPath, "Vintagestory.API.Config", "GameVersion", "ShortGameVersion");

        if (!string.IsNullOrWhiteSpace(declared)) return declared;

        try
        {
            var v = AssemblyName.GetAssemblyName(apiDllPath).Version;
            return v is null ? "unknown" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch (Exception e) when (e is BadImageFormatException or FileLoadException or IOException)
        {
            return "unknown";
        }
    }
}
