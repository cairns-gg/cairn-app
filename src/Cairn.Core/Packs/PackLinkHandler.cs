using System.Diagnostics;

namespace Cairn.Core.Packs;

/// <summary>
/// Tells the operating system that <c>cairn://</c> links belong to this build.
///
/// Handling a link has always worked everywhere — the URL arrives in <c>argv</c> on
/// Windows and Linux, and through an activation event on macOS. What was missing off
/// macOS is the other half: nothing had ever told the OS the scheme exists, so a click in
/// a browser found no handler and did nothing at all.
///
/// macOS gets this free from the bundle format. LaunchServices reads
/// <c>CFBundleURLTypes</c> out of Info.plist the first time it sees the .app, so shipping
/// a bundle is the registration. Windows and Linux have no equivalent: registering there
/// is an explicit act of installation, and Cairn deliberately ships as one binary in an
/// archive with no installer to perform one. So the app does it for itself on startup.
///
/// On every start rather than once, because both mechanisms record an absolute path to
/// the executable, and somebody who moves the binary would otherwise be left with a
/// scheme pointing at where it used to be. Writing only when the value has actually
/// changed keeps that cheap and keeps it out of the way.
///
/// Everything here is best effort. A launcher that would not open because it could not
/// write a registry value would be a much worse thing than one whose links do not work.
/// </summary>
public static class PackLinkHandler
{
    /// <summary>The MIME type a Linux desktop environment knows the scheme by.</summary>
    public const string MimeType = $"x-scheme-handler/{PackUri.Scheme}";

    /// <summary>Named for what it is, so it is obvious what it is for if anyone finds it.</summary>
    public const string DesktopFileName = "cairn-url-handler.desktop";

    /// <summary>
    /// Set to opt out. The headless test suite does, because it boots the real
    /// <c>App</c> class: without it, running the tests writes a desktop entry into the
    /// home directory of whoever ran them — which is the same reason the suite points
    /// <c>CAIRN_HOME</c> at a temporary directory rather than reading the developer's own.
    ///
    /// The destination cannot simply follow <c>CAIRN_HOME</c> instead: a desktop entry
    /// only counts where the XDG spec says to put it, so somewhere harmless is also
    /// somewhere inert.
    /// </summary>
    public const string OptOutVariable = "CAIRN_NO_URL_HANDLER";

    /// <summary>
    /// Registers the running executable as the handler, if this platform needs telling.
    ///
    /// Never throws, and on macOS does nothing at all. It does block — writing a file, and
    /// on Linux waiting on two helper processes — so callers give it a thread of its own
    /// rather than the pool.
    /// </summary>
    public static void Register()
    {
        try
        {
            if (Environment.GetEnvironmentVariable(OptOutVariable) is { Length: > 0 }) return;

            // Environment.ProcessPath is the apphost that was actually launched, which is
            // what has to be recorded — Assembly.Location is the managed dll and is empty
            // in a single-file build.
            if (Environment.ProcessPath is not { Length: > 0 } executable) return;

            if (OperatingSystem.IsLinux()) RegisterOnLinux(executable);
            else if (OperatingSystem.IsWindows()) RegisterOnWindows(executable);

            // macOS is deliberately absent: build-macos-app.sh writes CFBundleURLTypes
            // into the bundle, and a second registration from in here could only disagree
            // with it.
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or System.ComponentModel.Win32Exception)
        {
            // See the class summary: links not working is a disappointment, and a launcher
            // that will not start is a broken download.
        }
    }

    // ---- Linux ----

    /// <summary>
    /// The desktop entry that claims the scheme.
    ///
    /// <c>%u</c> is the single URL the handler is invoked with; the quotes around the
    /// executable are what let it live under a path with a space in it, which an
    /// unpacked-anywhere tarball very well might.
    ///
    /// <c>NoDisplay=true</c> keeps it out of the applications menu. This file exists to
    /// answer a link, and a launcher that silently added itself to somebody's menu would
    /// be doing something they did not ask for — a menu entry can come later, with an
    /// installed icon to go with it.
    /// </summary>
    public static string DesktopEntry(string executable) =>
        $"""
        [Desktop Entry]
        Type=Application
        Name=Cairn
        Comment=Open a Cairn pack link
        Exec="{executable}" %u
        Terminal=false
        NoDisplay=true
        MimeType={MimeType};
        """ + "\n";

    /// <summary>
    /// Writes the entry into an applications directory, and says whether it had to.
    /// Directory taken as a parameter so this is testable without a home directory.
    /// </summary>
    public static bool WriteDesktopEntry(string applicationsDir, string executable)
    {
        var path = Path.Combine(applicationsDir, DesktopFileName);
        var wanted = DesktopEntry(executable);

        // Unchanged is the common case — every start after the first — and rewriting it
        // would mean running update-desktop-database each time for nothing.
        if (File.Exists(path) && File.ReadAllText(path) == wanted) return false;

        Directory.CreateDirectory(applicationsDir);
        File.WriteAllText(path, wanted);
        return true;
    }

    private static void RegisterOnLinux(string executable)
    {
        var applications = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "applications");

        if (!WriteDesktopEntry(applications, executable)) return;

        // The database is what desktop environments actually consult; the file alone is
        // inert until this has run.
        Run("update-desktop-database", applications);

        // And this is what makes it the default rather than merely a candidate. Usually
        // redundant, since nothing else claims this scheme — but "usually" is not a
        // guarantee, and being the only handler is not the same as being the chosen one.
        Run("xdg-mime", "default", DesktopFileName, MimeType);
    }

    // ---- Windows ----

    private const string Key = @"HKCU\Software\Classes\" + PackUri.Scheme;

    /// <summary>
    /// What Windows runs for a link. <c>%1</c> is the URL, quoted because it is opaque
    /// text arriving from a web page and must not be split on spaces.
    /// </summary>
    public static string OpenCommand(string executable) => $"\"{executable}\" \"%1\"";

    private static void RegisterOnWindows(string executable)
    {
        var command = OpenCommand(executable);
        if (CurrentCommand() == command) return;

        // The default value is the description shown for the scheme; "URL Protocol" is the
        // empty-valued marker that tells Windows this key describes one at all, without
        // which the rest is ignored.
        Reg("add", Key, "/ve", "/d", $"URL:{PackUri.Scheme} pack link", "/f");
        Reg("add", Key, "/v", "URL Protocol", "/d", "", "/f");
        Reg("add", $@"{Key}\shell\open\command", "/ve", "/d", command, "/f");
    }

    private static string? CurrentCommand()
    {
        var (exit, output) = RegRead("query", $@"{Key}\shell\open\command", "/ve");
        if (exit != 0) return null;

        // reg.exe prints "    (Default)    REG_SZ    <value>", and the value can contain
        // runs of spaces of its own, so the split is on the type rather than on whitespace.
        var marker = output.IndexOf("REG_SZ", StringComparison.Ordinal);
        return marker < 0 ? null : output[(marker + "REG_SZ".Length)..].Trim();
    }

    private static void Reg(params string[] args) => RegRead(args);

    private static (int Exit, string Output) RegRead(params string[] args)
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
            if (process is null) return (-1, "");

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            return (-1, "");
        }
    }

    // ---- shared ----

    /// <summary>
    /// Runs a desktop-integration helper and does not care whether it worked. These are
    /// not present on every system — a minimal container has neither — and their absence
    /// is not a reason to report anything to anybody.
    /// </summary>
    private static void Run(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            foreach (var arg in args) psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            process?.WaitForExit(5_000);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
        }
    }
}
