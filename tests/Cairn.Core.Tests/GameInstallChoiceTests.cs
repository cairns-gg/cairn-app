using Cairn.Core;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Which Vintage Story Cairn uses, and how a person says so.
///
/// Finding it is not cosmetic. The version an import targets comes from the install, and so
/// does whether a mod marked for nothing like that version may be brought in on the strength
/// of somebody already running it — so a machine Cairn cannot find the game on quietly
/// imports a different set of mods and says only that it could not find an install.
///
/// The search itself cannot be tested against a real machine, so the rules take their two
/// configured answers as arguments, exactly as CairnHome.Resolve does.
/// </summary>
public class GameInstallChoiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-install-" + Guid.NewGuid().ToString("n")[..8]);

    public GameInstallChoiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>An install real enough for GameInstall.TryAt.</summary>
    private string Install(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(
            Path.Combine(dir, OperatingSystem.IsWindows() ? "Vintagestory.exe" : "Vintagestory"),
            new byte[64]);
        File.WriteAllText(Path.Combine(dir, "VintagestoryAPI.dll"), "");
        return dir;
    }

    // ---- the order the three answers come in ----

    [Fact]
    public void The_environment_wins()
    {
        var candidates = GameInstall.CandidateDirectories("/from/env", "/from/settings").ToList();

        Assert.Equal("/from/env", candidates[0]);
    }

    /// <summary>
    /// A systemd unit and a CI job set the variable, and ServerUnit writes it into the unit
    /// file — a setting that outranked it would quietly redirect a running server. The same
    /// precedence CAIRN_HOME has, for the same reason.
    /// </summary>
    [Fact]
    public void And_a_chosen_directory_comes_second_rather_than_first()
    {
        var candidates = GameInstall.CandidateDirectories("/from/env", "/from/settings").ToList();

        Assert.Equal("/from/settings", candidates[1]);
    }

    [Fact]
    public void A_chosen_directory_is_ahead_of_everywhere_the_game_installs_itself()
    {
        var candidates = GameInstall.CandidateDirectories(null, "/from/settings").ToList();

        Assert.Equal("/from/settings", candidates[0]);
        Assert.True(candidates.Count > 1, "the search should still be tried after it");
    }

    /// <summary>
    /// Set-but-blank is unset. Taken literally it would put an empty string at the head of
    /// the list, which Path.Combine and Directory.Exists both answer for the process's
    /// working directory — the same trap CAIRN_HOME fell into.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_answer_is_no_answer(string blank)
    {
        Assert.DoesNotContain(blank, GameInstall.CandidateDirectories(blank, blank));
    }

    [Fact]
    public void With_nothing_configured_it_is_the_search_alone()
    {
        var candidates = GameInstall.CandidateDirectories(null, null).ToList();

        Assert.NotEmpty(candidates);
        Assert.All(candidates, c => Assert.False(string.IsNullOrWhiteSpace(c)));
    }

    // ---- what a person is allowed to pick ----

    [Fact]
    public void Choosing_the_install_itself_works()
    {
        var dir = Install("Vintagestory");

        Assert.Equal(dir, GameInstall.Choose(dir)?.Directory);
    }

    /// <summary>
    /// The macOS case, and the commonest mistake everywhere else. There the install *is*
    /// Vintagestory.app and a folder picker will not go inside a bundle, so the only thing
    /// that can be selected is the folder holding it — refusing that would leave the button
    /// unable to do the one job it exists for on that platform.
    /// </summary>
    [Fact]
    public void So_does_choosing_the_folder_that_holds_it()
    {
        var dir = Install("Vintagestory.app");

        Assert.Equal(dir, GameInstall.Choose(_root)?.Directory);
    }

    [Fact]
    public void A_folder_with_no_install_in_it_is_refused()
    {
        Assert.Null(GameInstall.Choose(_root));
    }

    /// <summary>
    /// Refused rather than stored. A path that is not an install would otherwise sit in
    /// settings.json being silently skipped by the search, which looks exactly like the bug
    /// this setting exists to fix.
    /// </summary>
    [Fact]
    public void And_so_is_a_folder_that_is_not_there_at_all()
    {
        Assert.Null(GameInstall.Choose(Path.Combine(_root, "nothing-here")));
    }

    /// <summary>
    /// One level, not a walk. A recursive search from whatever somebody picked could be
    /// handed a home directory or a drive root, and would sit there reading a disk while a
    /// dialog waited on it.
    /// </summary>
    [Fact]
    public void The_search_below_a_chosen_folder_goes_one_level_only()
    {
        Install(Path.Combine("games", "Vintagestory"));

        Assert.Null(GameInstall.Choose(_root));
    }
}
