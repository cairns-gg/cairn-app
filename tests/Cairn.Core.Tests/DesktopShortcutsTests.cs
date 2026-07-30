using Cairn.Core.Games;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Every Vintage Story installer rewrites the same desktop shortcut to point at itself, so
/// installing 1.22 and then 1.21 leaves the player's shortcut aimed at the older version.
/// These pin the "leave the desktop as we found it" behaviour, on any platform — the real
/// folders are only substituted on Windows.
/// </summary>
public class DesktopShortcutsTests : IDisposable
{
    private readonly string _desktop = Path.Combine(
        Path.GetTempPath(), "cairn-desktop-test-" + Guid.NewGuid().ToString("n")[..8]);

    public DesktopShortcutsTests() => Directory.CreateDirectory(_desktop);

    public void Dispose()
    {
        if (Directory.Exists(_desktop)) Directory.Delete(_desktop, recursive: true);
    }

    private string Shortcut(string name, string contents)
    {
        var path = Path.Combine(_desktop, name);
        File.WriteAllText(path, contents);
        return path;
    }

    private WindowsGameInstaller.DesktopShortcuts Capture() =>
        WindowsGameInstaller.DesktopShortcuts.Capture([_desktop]);

    [Fact]
    public void A_shortcut_the_installer_created_is_removed_again()
    {
        var guard = Capture();

        // What the installer does when the player had no shortcut at all.
        Shortcut("Vintage Story.lnk", "points at Cairn's 1.21.5");

        guard.Restore();

        Assert.False(File.Exists(Path.Combine(_desktop, "Vintage Story.lnk")));
    }

    [Fact]
    public void A_shortcut_the_installer_overwrote_is_put_back()
    {
        var path = Shortcut("Vintage Story.lnk", "points at the player's own install");

        var guard = Capture();

        // The installer repoints it at whichever version was installed last.
        File.WriteAllText(path, "points at Cairn's 1.21.5");
        guard.Restore();

        Assert.True(File.Exists(path));
        Assert.Equal("points at the player's own install", File.ReadAllText(path));
    }

    [Fact]
    public void A_shortcut_the_installer_left_alone_is_not_rewritten()
    {
        var path = Shortcut("Vintagestory.lnk", "untouched");
        var before = File.GetLastWriteTimeUtc(path);

        var guard = Capture();
        guard.Restore();

        Assert.Equal("untouched", File.ReadAllText(path));
        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Shortcuts_to_other_things_are_never_touched()
    {
        // The blast radius is deliberately narrow: an install takes minutes, and reverting
        // something the player changed meanwhile would be far worse than a stray shortcut.
        var other = Shortcut("Notepad.lnk", "nothing to do with the game");

        var guard = Capture();
        File.WriteAllText(other, "edited during the install");
        Shortcut("Steam.lnk", "created during the install");

        guard.Restore();

        Assert.Equal("edited during the install", File.ReadAllText(other));
        Assert.True(File.Exists(Path.Combine(_desktop, "Steam.lnk")));
    }

    [Fact]
    public void Only_lnk_files_are_considered()
    {
        var doc = Shortcut("vintage story notes.txt", "mine");

        var guard = Capture();
        File.WriteAllText(doc, "still mine");
        guard.Restore();

        Assert.Equal("still mine", File.ReadAllText(doc));
    }

    [Fact]
    public void The_name_match_is_case_insensitive()
    {
        var guard = Capture();
        Shortcut("VINTAGE STORY.lnk", "created by the installer");

        guard.Restore();

        Assert.False(File.Exists(Path.Combine(_desktop, "VINTAGE STORY.lnk")));
    }

    [Fact]
    public void A_missing_desktop_folder_is_not_an_error()
    {
        var guard = WindowsGameInstaller.DesktopShortcuts.Capture(
            [Path.Combine(_desktop, "does-not-exist")]);

        guard.Restore();   // must not throw
    }

    [Fact]
    public void Restoring_cleans_up_after_itself()
    {
        Shortcut("Vintage Story.lnk", "original");

        var guard = Capture();
        guard.Restore();

        // The backup copies live in the temp directory; leaving them behind would litter it
        // with a directory per install.
        var leftovers = Directory.EnumerateDirectories(
            Path.GetTempPath(), $"cairn-desktop-{Environment.ProcessId}-*").ToList();

        Assert.Empty(leftovers);
    }
}
