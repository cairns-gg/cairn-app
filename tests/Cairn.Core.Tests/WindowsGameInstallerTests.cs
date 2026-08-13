using Cairn.Core.Games;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// The switches handed to the Windows client installer. These run on any platform because
/// the command line is built separately from running it — the alternative is discovering a
/// wrong switch only on a Windows machine, at the end of a 570 MB download.
/// </summary>
public class WindowsGameInstallerTests
{
    private static string Line(string dir) =>
        string.Join(' ', WindowsGameInstaller.BuildArguments(dir));

    [Fact]
    public void The_install_runs_without_any_ui()
    {
        var args = WindowsGameInstaller.BuildArguments(@"C:\Cairn\games\1.22.5");

        // Cairn shows its own progress, and nobody is watching for a dialog to click.
        Assert.Contains("/VERYSILENT", args);
        Assert.Contains("/SUPPRESSMSGBOXES", args);
        Assert.Contains("/NORESTART", args);
    }

    [Fact]
    public void It_installs_where_it_is_told_rather_than_the_default_location()
    {
        // The whole point: the wizard's default is one %APPDATA%\Vintagestory for every
        // version, which makes side-by-side versions impossible.
        Assert.Contains(@"/DIR=C:\Cairn\games\1.22.5", WindowsGameInstaller.BuildArguments(@"C:\Cairn\games\1.22.5"));
    }

    [Fact]
    public void A_trailing_separator_is_dropped()
    {
        // "C:\dir\" quotes as "/DIR=C:\dir\" — the backslash escapes the closing quote and
        // the argument arrives mangled.
        Assert.Contains(@"/DIR=C:\Cairn\games\1.22.5", WindowsGameInstaller.BuildArguments(@"C:\Cairn\games\1.22.5\"));
        Assert.DoesNotContain(@"\\", Line(@"C:\Cairn\games\1.22.5\"));
    }

    [Fact]
    public void Managed_versions_stay_out_of_the_start_menu()
    {
        // One entry per installed version, none of which should be started directly.
        Assert.Contains("/NOICONS", WindowsGameInstaller.BuildArguments(@"C:\Cairn\games\1.22.5"));
    }

    [Fact]
    public void The_desktop_shortcut_task_is_deselected()
    {
        // /NOICONS does not cover this: it only suppresses Start menu entries that have no
        // Task attached, and the desktop icon is a Task. Without this the installer
        // rewrites the player's one shortcut to point at whichever version went in last —
        // so installing 1.22 then 1.21 leaves it aimed at the older one.
        Assert.Contains("/MERGETASKS=!desktopicon",
            WindowsGameInstaller.BuildArguments(@"C:\Cairn\games\1.22.5"));
    }

    [Fact]
    public void A_log_path_is_passed_only_when_asked_for()
    {
        Assert.DoesNotContain(WindowsGameInstaller.BuildArguments(@"C:\x"), a => a.StartsWith("/LOG"));
    }

    // ---- who signed the thing being run ----

    /// <summary>
    /// The signer pin, which is the half of the Authenticode check that is a policy rather
    /// than a call into Windows. Asserted here because the rest of WindowsCodeSignature
    /// needs Windows to run at all, and this is the part that decides whether a validly
    /// signed installer from somebody else is accepted — which it must not be.
    ///
    /// Observed on the shipped installer: an SSL.com EV certificate issued to
    /// "Anego Studios SIA".
    /// </summary>
    [Theory]
    [InlineData("Anego Studios SIA", true)]
    [InlineData("anego studios sia", true)]
    [InlineData("  Anego Studios SIA  ", true)]
    [InlineData("Anego Studios", false)]
    [InlineData("Anego Studios SIA Ltd", false)]
    [InlineData("Microsoft Corporation", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_vendors_own_signature_is_accepted(string? signer, bool expected) =>
        Assert.Equal(expected, WindowsCodeSignature.IsExpectedSigner(signer));

    /// <summary>
    /// Off Windows there is nothing to verify and no path that reaches it — GameInstaller
    /// refuses a Windows installer on any other platform well before this — so Require
    /// must not throw on a file it cannot inspect.
    /// </summary>
    [Fact]
    public void Verification_is_a_no_op_off_Windows()
    {
        if (OperatingSystem.IsWindows()) return;

        var path = Path.Combine(Path.GetTempPath(), "cairn-sig-" + Guid.NewGuid().ToString("n")[..8]);
        File.WriteAllText(path, "not an executable");

        try
        {
            Assert.Null(WindowsCodeSignature.Problem(path));
        }
        finally
        {
            File.Delete(path);
        }
        Assert.Contains(@"/LOG=C:\x\install.log",
            WindowsGameInstaller.BuildArguments(@"C:\x", @"C:\x\install.log"));
    }
}
