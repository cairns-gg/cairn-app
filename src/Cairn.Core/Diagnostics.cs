using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Cairn.Core.Games;
using Cairn.Core.Packs;

namespace Cairn.Core;

/// <summary>
/// The facts worth having in a bug report, as plain text somebody can read before they
/// send it.
///
/// Every report that arrives without these costs a round trip to ask which version, which
/// platform, which game install — and the answer usually arrives a day later, by which
/// point whatever was on screen has gone. Assembling it here rather than in the launcher
/// means the CLI can produce the same report, and that what it contains is one decision in
/// one place rather than whatever each front-end thought to include.
///
/// Two rules, both of which are the whole point of building it rather than uploading a
/// directory:
///
/// - **Nothing is transmitted.** This returns a string. The caller puts it on the
///   clipboard, and a person decides where it goes. Cairn has a session token and a
///   cairns.gg login on disk, and the safest way to never send them is to never have a
///   code path that sends anything.
/// - **No path carries a name.** Home directories are called after their owner, so every
///   absolute path here is a person's name in a public issue tracker. <see cref="Redact"/>
///   puts them back to <c>~</c>.
/// </summary>
public static class Diagnostics
{
    /// <summary>
    /// How much of the log to carry. Enough to hold a failed sync and the launch before it;
    /// short enough that somebody will actually read what they are about to paste.
    /// </summary>
    public const int LogLines = 60;

    /// <summary>
    /// Builds the report.
    /// </summary>
    /// <param name="pack">The pack being complained about, if there is one.</param>
    /// <param name="locked">Its lockfile, which is what is actually installed.</param>
    /// <param name="log">The launcher's log, newest last. The tail is taken.</param>
    /// <param name="library">
    /// Injected so this can be tested without a game on the machine; the caller normally
    /// has one already built.
    /// </param>
    /// <param name="modsDir">
    /// The pack's Mods directory. Given, every mod is described from its own zip as well
    /// as from the lock, which is the only way to see a file that has gone missing, been
    /// replaced, or disagrees with the version the lock claims for it.
    /// </param>
    public static string Report(
        PackManifest? pack = null,
        PackLock? locked = null,
        IEnumerable<string>? log = null,
        GameLibrary? library = null,
        string? modsDir = null)
    {
        var text = new StringBuilder();

        text.AppendLine("Cairn diagnostics");
        text.AppendLine("=================");
        text.AppendLine();
        text.AppendLine($"cairn      {CairnVersion.Current}");
        text.AppendLine($"platform   {Platform()}");
        text.AppendLine($"os         {RuntimeInformation.OSDescription.Trim()}");
        text.AppendLine($"runtime    {RuntimeInformation.FrameworkDescription}");
        text.AppendLine($"home       {Redact(CairnPaths.Root)}");
        text.AppendLine();

        AppendGames(text, library);

        if (pack is not null) AppendPack(text, pack, locked, modsDir);

        AppendLog(text, log);

        return text.ToString();
    }

    /// <summary>
    /// The same key the update manifest uses, so a report says which build was downloaded
    /// rather than what the machine could have run.
    /// </summary>
    private static string Platform() =>
        $"{Updates.UpdateChecker.ThisPlatform} ({RuntimeInformation.ProcessArchitecture})";

    private static void AppendGames(StringBuilder text, GameLibrary? library)
    {
        text.AppendLine("Game installs");

        if (library is null)
        {
            text.AppendLine("  (not inspected)");
            text.AppendLine();
            return;
        }

        var managed = Safely(() => library.Managed, []);

        foreach (var install in managed)
            text.AppendLine($"  managed  {install.Version,-10} {install.Architecture}, "
                            + $"needs .NET {install.RequiredFramework}  {Redact(install.Directory)}");

        var system = Safely(() => library.System, null);

        text.AppendLine(system is null
            ? "  system   none found"
            : $"  system   {system.Version,-10} {system.Architecture}  {Redact(system.Directory)}");

        if (managed.Count == 0 && system is null)
            text.AppendLine("  (nothing installed — this is why a pack would not launch)");

        text.AppendLine();
    }

    private static void AppendPack(
        StringBuilder text, PackManifest pack, PackLock? locked, string? modsDir)
    {
        text.AppendLine($"Pack '{pack.Id}'{(pack.Name is null ? "" : $" — {pack.Name}")}");
        text.AppendLine($"  game       {pack.GameVersion}");

        // Whether there is a server address, never the address. It is the one thing in a
        // manifest somebody may not want quoted in public, and publishing already treats
        // it that way.
        text.AppendLine($"  connect    {(string.IsNullOrWhiteSpace(pack.Connect) ? "none" : "set")}");

        if (locked is null)
        {
            text.AppendLine($"  mods       {pack.Mods.Count} declared, never synced");
            text.AppendLine();
            return;
        }

        text.AppendLine($"  lock       game {locked.GameVersion}, {locked.Mods.Count} installed");
        text.AppendLine($"  mods       {pack.Mods.Count} declared");
        if (modsDir is not null) text.AppendLine($"  mods dir   {Redact(modsDir)}");
        text.AppendLine();

        var declared = pack.Mods.ToDictionary(
            m => m.ModId, m => m.Version, StringComparer.OrdinalIgnoreCase);

        foreach (var mod in locked.Mods.OrderBy(m => m.ModId, StringComparer.OrdinalIgnoreCase))
        {
            var why = mod.RequiredBy is { Count: > 0 } wanters
                ? $"required by {string.Join(", ", wanters)}"
                : declared.TryGetValue(mod.ModId, out var pin) && pin is not null
                    ? $"pinned to {pin}"
                    : "asked for by this pack";

            text.AppendLine($"    {mod.ModId} {mod.Version} — {why}");
            AppendMod(text, mod, modsDir);
        }

        // A mod the manifest asks for and the lock does not have is the single most useful
        // line in the whole report, because it is what stops a pack launching or publishing.
        var missing = pack.Mods
            .Where(m => !locked.Mods.Any(
                l => string.Equals(l.ModId, m.ModId, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (missing.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"  NOT INSTALLED: {string.Join(", ", missing.Select(m => m.ModId))}");
        }

        text.AppendLine();
    }

    /// <summary>
    /// Everything knowable about one installed mod: what the lock claims, what is on disk,
    /// and what the zip says about itself.
    ///
    /// The three disagreeing is the interesting case and the one nobody thinks to check.
    /// A file that is absent, a checksum that has moved, or a modinfo declaring a different
    /// version from the lock all produce a mod that behaves like a different mod than the
    /// pack thinks it installed — which is exactly the report that otherwise reads "it just
    /// stopped working" and takes three exchanges to get anywhere.
    /// </summary>
    private static void AppendMod(StringBuilder text, LockedMod mod, string? modsDir)
    {
        if (!string.IsNullOrWhiteSpace(mod.Side) &&
            !string.Equals(mod.Side, "both", StringComparison.OrdinalIgnoreCase))
            text.AppendLine($"      side       {mod.Side}");

        // Host only. The full URL adds nothing a reader can act on, and the point of
        // recording it is whether the mod came from somewhere ModDB actually serves.
        var host = Uri.TryCreate(mod.Url, UriKind.Absolute, out var uri) ? uri.Host : mod.Url;

        if (!string.IsNullOrWhiteSpace(host))
            text.AppendLine($"      source     {host}"
                            + (mod.ReleaseId > 0 ? $"  release {mod.ReleaseId}" : "")
                            + (mod.FileId > 0 ? $", file {mod.FileId}" : ""));

        if (modsDir is null)
        {
            text.AppendLine($"      file       {mod.FileName} (not inspected)");
            return;
        }

        var path = Path.Combine(modsDir, mod.FileName);

        if (!Safely(() => File.Exists(path), false))
        {
            // The lock says this is installed and it is not there. Nothing else in the
            // report matters as much for this mod.
            text.AppendLine($"      file       {mod.FileName} — MISSING FROM DISK");
            return;
        }

        var size = Safely(() => new FileInfo(path).Length, -1);
        var actual = Safely(() => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), null);

        var checksum = mod.Sha256.Length == 0
            ? "none recorded"
            : actual is null
                ? "unreadable"
                : string.Equals(actual, mod.Sha256, StringComparison.OrdinalIgnoreCase)
                    ? "matches the lock"
                    : $"DIFFERS from the lock (disk {actual[..12]}…, lock {mod.Sha256[..12]}…)";

        text.AppendLine($"      file       {mod.FileName}"
                        + (size >= 0 ? $", {size:n0} bytes" : "")
                        + $", sha256 {checksum}");

        var info = ModDependencies.Describe(path);

        text.AppendLine($"      modinfo    {Redact(info.Describe())}");

        // A zip whose declared version is not the one the lock recorded is a mod that was
        // repackaged under the same release, or a file swapped by hand.
        if (info.Problem is null && !string.IsNullOrWhiteSpace(info.Version)
            && !string.Equals(info.Version, mod.Version, StringComparison.OrdinalIgnoreCase))
            text.AppendLine($"                 version disagrees with the lock ({mod.Version})");

        if (info.Problem is null && info.Requires.Count > 0)
            text.AppendLine($"      requires   {info.DescribeRequires()}");
    }

    private static void AppendLog(StringBuilder text, IEnumerable<string>? log)
    {
        var lines = Safely(() => log?.ToList(), null);
        if (lines is null || lines.Count == 0) return;

        var tail = lines.Count > LogLines ? lines[^LogLines..] : lines;

        text.AppendLine($"Log (last {tail.Count} of {lines.Count})");
        foreach (var line in tail) text.AppendLine($"  {Redact(line)}");
        text.AppendLine();
    }

    /// <summary>
    /// Replaces the home directory with <c>~</c>, wherever it appears in a line.
    ///
    /// Not cosmetic: <c>/Users/dizzyd/.cairn</c> puts a real name in a public issue, and
    /// log lines quote paths as readily as the headings do. Longest match first so a home
    /// directory nested inside another does not leave half a path behind.
    /// </summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";

        var homes = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetEnvironmentVariable("HOME"),
                Environment.GetEnvironmentVariable("USERPROFILE"),
            }
            .Where(h => !string.IsNullOrWhiteSpace(h) && h!.Length > 1)
            .Distinct()
            .OrderByDescending(h => h!.Length);

        foreach (var home in homes)
            text = text!.Replace(home!, "~", StringComparison.OrdinalIgnoreCase);

        return text!;
    }

    /// <summary>
    /// Runs one of the lookups, falling back rather than throwing.
    ///
    /// A diagnostics report is asked for when something is already wrong, which is exactly
    /// when a game directory is half-deleted or a path is unreadable. A report that crashes
    /// while describing a crash is worse than one with a gap in it.
    /// </summary>
    private static T Safely<T>(Func<T> get, T fallback)
    {
        try
        {
            return get();
        }
        catch (Exception)
        {
            return fallback;
        }
    }
}
