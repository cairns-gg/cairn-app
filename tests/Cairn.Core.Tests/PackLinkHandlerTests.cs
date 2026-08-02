using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Claiming the cairn:// scheme on the platforms that have to be told about it.
///
/// The registration itself reaches the registry and the user's home directory, so what is
/// exercised here is what gets written rather than the writing: the entry a desktop
/// environment reads, the command Windows runs, and the fact that a second start does not
/// rewrite either of them.
/// </summary>
public class PackLinkHandlerTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "cairn-links-" + Guid.NewGuid().ToString("n")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void The_desktop_entry_claims_the_scheme_and_passes_the_url_on()
    {
        var entry = PackLinkHandler.DesktopEntry("/opt/cairn/cairn");

        // MimeType is what makes it a handler at all, and %u is the URL being handed over.
        Assert.Contains("MimeType=x-scheme-handler/cairn;", entry);
        Assert.Contains("Exec=\"/opt/cairn/cairn\" %u", entry);
        Assert.Contains("Type=Application", entry);

        // Out of the applications menu: this file is here to answer a link, and adding
        // itself to somebody's menu is not what they downloaded.
        Assert.Contains("NoDisplay=true", entry);
    }

    [Fact]
    public void An_executable_under_a_path_with_a_space_is_still_launchable()
    {
        // A tarball unpacks wherever it is dropped, which is quite often somewhere with a
        // space in it. Unquoted, the desktop file would try to run "/home/me/Vintage".
        var entry = PackLinkHandler.DesktopEntry("/home/me/Vintage Story/cairn");

        Assert.Contains("Exec=\"/home/me/Vintage Story/cairn\" %u", entry);
    }

    [Fact]
    public void The_windows_command_quotes_both_the_executable_and_the_url()
    {
        var command = PackLinkHandler.OpenCommand(@"C:\Program Files\Cairn\cairn.exe");

        // The URL is opaque text arriving from a web page; splitting it on a space would
        // hand the app half a link.
        Assert.Equal(@"""C:\Program Files\Cairn\cairn.exe"" ""%1""", command);
    }

    [Fact]
    public void The_entry_is_written_once_and_not_rewritten_on_every_start()
    {
        Assert.True(PackLinkHandler.WriteDesktopEntry(_dir, "/opt/cairn/cairn"));

        var path = Path.Combine(_dir, PackLinkHandler.DesktopFileName);
        Assert.True(File.Exists(path));

        // Rewriting an identical file would mean running update-desktop-database on every
        // launch to tell it nothing had changed.
        Assert.False(PackLinkHandler.WriteDesktopEntry(_dir, "/opt/cairn/cairn"));
    }

    [Fact]
    public void Moving_the_executable_rewrites_the_entry()
    {
        PackLinkHandler.WriteDesktopEntry(_dir, "/opt/cairn/cairn");

        // Both mechanisms record an absolute path, so a binary that moved would otherwise
        // leave the scheme pointing at where it used to be. This is why registration
        // happens on every start rather than once.
        Assert.True(PackLinkHandler.WriteDesktopEntry(_dir, "/home/me/cairn/cairn"));

        Assert.Contains(
            "Exec=\"/home/me/cairn/cairn\" %u",
            File.ReadAllText(Path.Combine(_dir, PackLinkHandler.DesktopFileName)));
    }

    [Fact]
    public void A_missing_applications_directory_is_created_rather_than_refused()
    {
        var nested = Path.Combine(_dir, ".local", "share", "applications");

        // A fresh account has never had one.
        Assert.True(PackLinkHandler.WriteDesktopEntry(nested, "/opt/cairn/cairn"));
        Assert.True(File.Exists(Path.Combine(nested, PackLinkHandler.DesktopFileName)));
    }

    [Fact]
    public void Registering_never_throws_whatever_platform_this_is()
    {
        // Called on every start before the window is up. Links not working is a
        // disappointment; a launcher that will not open is a broken download.
        PackLinkHandler.Register();
        PackLinkHandler.Register();
    }

    [Fact]
    public void The_scheme_registered_is_the_one_links_are_written_in()
    {
        // Two literals that must agree, in files that are edited for different reasons.
        Assert.Equal($"x-scheme-handler/{PackUri.Scheme}", PackLinkHandler.MimeType);

        Assert.True(PackUri.TryGetDocumentUrl($"{PackUri.Scheme}://cairns.gg/dizzyd/anego", out _));
    }
}
