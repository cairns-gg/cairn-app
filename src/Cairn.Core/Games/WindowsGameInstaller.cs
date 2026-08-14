using System.Diagnostics;

namespace Cairn.Core.Games;

/// <summary>
/// Installs the Windows client, which is published only as an installer executable —
/// there is no client archive for that platform, unlike macOS and Linux.
///
/// It is an Inno Setup 6 installer, so it takes a target directory and runs without any
/// UI. That is what makes side-by-side versions possible: the wizard would otherwise put
/// every version in the same %APPDATA%\Vintagestory, and a pack pinned to an older
/// version could never coexist with a newer one.
///
/// The install is per-user, so it normally raises no UAC prompt.
/// </summary>
public static class WindowsGameInstaller
{
    /// <summary>
    /// Every Vintage Story installer shares this Inno Setup AppId, so installing a second
    /// copy rewrites the Add/Remove Programs entry of the first — see <see cref="UninstallEntry"/>.
    /// </summary>
    public const string InnoAppId = "{70364653-036D-49B3-8B80-AF39665F29C1}_is1";

    /// <summary>
    /// Split out from running it so the switches can be asserted on from any platform.
    ///
    /// /VERYSILENT suppresses the wizard entirely (Cairn shows its own progress), and
    /// /SUPPRESSMSGBOXES stops it stopping on a dialog nobody is there to click.
    ///
    /// Icons take two switches because Inno splits them: /NOICONS covers Start menu
    /// entries that have no associated Task, while the desktop shortcut *is* a Task and is
    /// reached only through /MERGETASKS. Suppressing it matters more than tidiness — every
    /// installer rewrites the one desktop shortcut to point at itself, so installing 1.22
    /// and then 1.21 leaves the player's shortcut aimed at the older version.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(string targetDirectory, string? logPath = null)
    {
        // A trailing separator would land as \" in the quoted argument and swallow the
        // closing quote, so the directory arrives corrupted. Trimmed literally rather than
        // by Path.DirectorySeparatorChar: this builds a Windows command line whatever the
        // host separator happens to be.
        var dir = targetDirectory.TrimEnd('\\', '/');

        var args = new List<string>
        {
            "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
            "/NOICONS", "/MERGETASKS=!desktopicon",
            $"/DIR={dir}",
        };

        if (!string.IsNullOrWhiteSpace(logPath)) args.Add($"/LOG={logPath}");

        return args;
    }

    /// <summary>
    /// Runs the installer into <paramref name="targetDirectory"/> and waits for it.
    /// Throws <see cref="OperationCanceledException"/> if it was cancelled — including by
    /// declining a UAC prompt, which is a refusal rather than a failure.
    /// </summary>
    public static async Task RunAsync(
        string installerPath, string targetDirectory, string? logPath = null,
        CancellationToken ct = default)
    {
        // Immediately before the only Process.Start in this file, rather than back where
        // the download happened. The check belongs to the act of running the thing: a
        // second caller arriving later gets it without having to remember, which is the
        // failure mode that put the same guard in one branch of PackSyncer and nowhere
        // else. Everything upstream binds the file to the catalogue that named it; this is
        // the only step that binds it to the people who make the game.
        WindowsCodeSignature.Require(installerPath);

        var psi = new ProcessStartInfo(installerPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in BuildArguments(targetDirectory, logPath)) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
                            ?? throw new GameInstallException(Lang.Get("install-could-not-start", installerPath));

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Leaving a silent installer running after the user cancelled would keep
            // writing into a directory the caller is about to delete.
            try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
            throw;
        }

        Check(process.ExitCode, logPath);
    }

    /// <summary>Inno Setup's documented exit codes.</summary>
    private static void Check(int exitCode, string? logPath)
    {
        const int userCancelledBeforeInstalling = 2;
        const int userCancelledDuringInstall = 5;
        const int errorCancelled = 1223;   // UAC prompt declined

        if (exitCode == 0) return;

        if (exitCode is userCancelledBeforeInstalling or userCancelledDuringInstall or errorCancelled)
            throw new OperationCanceledException();

        var detail = exitCode switch
        {
            1 => Lang.Get("installer-could-not-start"),
                3 or 4 => Lang.Get("installer-preparing-failed", exitCode),
                6 => Lang.Get("installer-terminated"),
                7 or 8 => Lang.Get("installer-cannot-continue", exitCode),
                _ => Lang.Get("installer-exit-code", exitCode),
        };

        throw new GameInstallException(logPath is null
            ? Lang.Get("install-failed", detail)
            : Lang.Get("install-failed-log", detail, logPath));
    }

    /// <summary>
    /// Leaves the desktop shortcut as it was found.
    ///
    /// The installer rewrites one shared "Vintage Story" shortcut to point at whatever was
    /// installed last, so installing 1.22 and then 1.21 leaves the player double-clicking
    /// into the older version. /MERGETASKS should prevent it, but that depends on the task
    /// being named the conventional "desktopicon" — if it is not, the switch is silently a
    /// no-op, which is precisely the kind of failure worth having a second answer to.
    ///
    /// So the shortcut is captured beforehand and put back afterwards: a shortcut the
    /// installer created is removed, and one it overwrote is restored byte for byte.
    /// Everything here is best-effort — tidying the desktop must never fail an install.
    /// </summary>
    public sealed class DesktopShortcuts
    {
        private sealed record Captured(string Path, string Backup);

        private readonly IReadOnlyList<string> _folders;
        private readonly List<Captured> _before;
        private readonly string? _backupDir;

        private DesktopShortcuts(IReadOnlyList<string> folders, List<Captured> before, string? backupDir)
        {
            _folders = folders;
            _before = before;
            _backupDir = backupDir;
        }

        public static DesktopShortcuts Capture() => Capture(DesktopFolders());

        /// <summary>Takes the folders explicitly so the logic is testable off Windows.</summary>
        public static DesktopShortcuts Capture(IEnumerable<string> folders)
        {
            var dirs = folders.ToList();
            var existing = dirs.SelectMany(Shortcuts).ToList();

            if (existing.Count == 0) return new DesktopShortcuts(dirs, [], null);

            var backupDir = Path.Combine(
                Path.GetTempPath(), $"cairn-desktop-{Environment.ProcessId}-{dirs.Count}");

            var captured = new List<Captured>();
            try
            {
                Directory.CreateDirectory(backupDir);

                for (var i = 0; i < existing.Count; i++)
                {
                    // Numbered rather than named: the same shortcut name can exist in both
                    // the per-user and the all-users desktop.
                    var backup = Path.Combine(backupDir, $"{i}.lnk");
                    File.Copy(existing[i], backup, overwrite: true);
                    captured.Add(new Captured(existing[i], backup));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Partial capture is still worth having; whatever was copied can be restored.
            }

            return new DesktopShortcuts(dirs, captured, backupDir);
        }

        public void Restore()
        {
            try
            {
                foreach (var c in _before)
                {
                    if (!File.Exists(c.Backup)) continue;

                    // Compared by content rather than by timestamp: a shortcut is a
                    // kilobyte, and this owes nothing to filesystem timestamp resolution.
                    if (!SameContent(c.Backup, c.Path)) TryCopy(c.Backup, c.Path);
                }

                // Anything matching that was not there before was put there by the
                // installer, for a copy the player did not ask to have on their desktop.
                var known = _before.Select(c => c.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var path in _folders.SelectMany(Shortcuts))
                    if (!known.Contains(path))
                        TryDelete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Leave the desktop as it is rather than failing an otherwise good install.
            }
            finally
            {
                if (_backupDir is not null)
                    try { Directory.Delete(_backupDir, recursive: true); } catch (Exception) { }
            }
        }

        /// <summary>
        /// Only Vintage Story's own shortcuts are considered. A blanket snapshot of every
        /// desktop shortcut would risk reverting something the player changed during the
        /// several minutes an install takes.
        /// </summary>
        private static IEnumerable<string> Shortcuts(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return [];

                return Directory.EnumerateFiles(folder, "*.lnk")
                    .Where(p => Path.GetFileName(p).Contains("vintage", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }

        private static IEnumerable<string> DesktopFolders()
        {
            if (!OperatingSystem.IsWindows()) yield break;

            yield return Environment.GetFolderPath(
                Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolderOption.DoNotVerify);

            // The all-users desktop needs admin to write, so the installer is unlikely to
            // have touched it — included so a restore is not silently partial.
            yield return Environment.GetFolderPath(
                Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolderOption.DoNotVerify);
        }

        private static bool SameContent(string a, string b)
        {
            try
            {
                return File.Exists(b)
                       && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Unreadable: treat as changed, so the known-good copy is put back.
                return false;
            }
        }

        private static void TryCopy(string from, string to)
        {
            try { File.Copy(from, to, overwrite: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Preserves the shared Add/Remove Programs entry across an install.
    ///
    /// Because every version's installer carries the same AppId, installing a managed copy
    /// repoints the player's existing entry at Cairn's directory — so uninstalling
    /// "Vintage Story" from Settings would remove the wrong copy, and would be left
    /// dangling once Cairn removed that version. Capturing the entry beforehand and
    /// putting it back afterwards leaves the machine as it was found.
    ///
    /// Only HKCU is touched: the silent install is per-user and cannot write a
    /// machine-wide entry, so a system install's HKLM entry is never at risk. Everything
    /// here is best-effort — failing to tidy the registry must not fail the install.
    /// </summary>
    public sealed class UninstallEntry
    {
        private const string Key =
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\" + InnoAppId;

        private readonly string? _backup;
        private readonly bool _existedBefore;

        private UninstallEntry(string? backup, bool existedBefore)
        {
            _backup = backup;
            _existedBefore = existedBefore;
        }

        public static UninstallEntry Capture()
        {
            if (!OperatingSystem.IsWindows()) return new UninstallEntry(null, false);

            if (Reg("query", Key) != 0) return new UninstallEntry(null, existedBefore: false);

            var backup = Path.Combine(Path.GetTempPath(), $"Cairn-vs-uninstall-{Environment.ProcessId}.reg");
            return Reg("export", Key, backup, "/y") == 0 && File.Exists(backup)
                ? new UninstallEntry(backup, existedBefore: true)
                : new UninstallEntry(null, existedBefore: true);
        }

        public void Restore()
        {
            if (!OperatingSystem.IsWindows()) return;

            try
            {
                if (!_existedBefore)
                {
                    // Nothing was registered before, so the only entry now is the one this
                    // install just wrote for a copy the player did not ask to see listed.
                    Reg("delete", Key, "/f");
                    return;
                }

                if (_backup is null) return;   // it existed but could not be captured; leave it alone

                Reg("delete", Key, "/f");
                Reg("import", _backup);
            }
            finally
            {
                if (_backup is not null)
                    try { File.Delete(_backup); } catch (IOException) { }
            }
        }

        private static int Reg(params string[] args)
        {
            try
            {
                // By full path, never by name. CreateProcess searches the calling process's
                // current directory before the system directory, and Cairn does not choose
                // its own — see ExecutableLookup.SystemTool.
                var psi = new ProcessStartInfo(ExecutableLookup.SystemTool("reg.exe"))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                foreach (var arg in args) psi.ArgumentList.Add(arg);

                using var process = Process.Start(psi);
                if (process is null) return -1;

                process.WaitForExit();
                return process.ExitCode;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
            {
                return -1;
            }
        }
    }
}
