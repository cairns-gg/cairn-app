using Cairn.Core;
using Cairn.Core.Games;
using Cairn.Core.Runtime;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// The decisions that come out differently on each platform, asked about all three from
/// whichever one this is running on.
///
/// The reason these exist as a group: a platform fork is a branch only one machine can
/// reach, and a branch nothing reaches is a branch nothing checks. ArchiveExtractor's zip
/// half was dead code on macOS and Linux and broken on Windows for as long as anyone can
/// tell — not because the code was hard, but because executing it required being Windows.
/// A Windows CI job helps and is not enough on its own: it tells you *that* something broke
/// there, half an hour later, while these say which rule was got wrong, in milliseconds, on
/// the machine the change was written on.
///
/// So the pattern being followed is the one <see cref="Cairn.Core.Games.Optimum.OptimumProvisioner"/>
/// arrived at first — take the platform as a parameter, default it to <see cref="Host.This"/>,
/// and let a test name any of them.
/// </summary>
public class PlatformForkTests
{
    // ---- which download is the right one for a platform ----

    [Theory]
    [InlineData(HostOs.Windows, ExecutableArch.X64, "windows")]
    [InlineData(HostOs.Linux, ExecutableArch.X64, "linux")]
    [InlineData(HostOs.MacOs, ExecutableArch.X64, "mac-x64")]
    public void Each_platform_asks_for_its_own_client(HostOs os, ExecutableArch arch, string first)
    {
        Assert.Equal(first, GameCatalog.KeysFor(os, arch)[0]);
    }

    [Fact]
    public void Apple_silicon_prefers_its_own_client_and_still_accepts_the_x64_one()
    {
        // Both halves matter. Preferring arm64 is most of the reason to build for the
        // machine at all; keeping x64 as a fallback is what stops every pre-1.22 version —
        // which publishes no arm64 artifact — dropping out of the installable list.
        Assert.Equal(["mac-arm64", "mac-x64"], GameCatalog.KeysFor(HostOs.MacOs, ExecutableArch.Arm64));
        Assert.Equal(["mac-x64"], GameCatalog.KeysFor(HostOs.MacOs, ExecutableArch.X64));
    }

    [Fact]
    public void Only_two_platforms_have_a_server_download_and_macos_borrows_the_client()
    {
        // A server is published for Linux and Windows and nowhere else; a mac runs one out
        // of a client install, which ships VintagestoryServer beside its own binary.
        Assert.Equal(["windowsserver", "server"], GameCatalog.ServerKeysFor(HostOs.Windows, ExecutableArch.X64));
        Assert.Equal(["linuxserver", "server"], GameCatalog.ServerKeysFor(HostOs.Linux, ExecutableArch.X64));

        Assert.Equal(
            GameCatalog.KeysFor(HostOs.MacOs, ExecutableArch.Arm64),
            GameCatalog.ServerKeysFor(HostOs.MacOs, ExecutableArch.Arm64));
    }

    [Fact]
    public void The_generic_server_key_is_always_the_fallback()
    {
        // It is what every version before 1.18.15 published instead of the platform ones,
        // so filtering on "linuxserver" alone reports those versions as undownloadable.
        foreach (var os in new[] { HostOs.Windows, HostOs.Linux })
            Assert.Contains("server", GameCatalog.ServerKeysFor(os, ExecutableArch.X64));
    }

    // ---- how a command on PATH is spelled ----

    [Fact]
    public void Windows_looks_for_a_bare_name_under_every_pathext_extension()
    {
        // git is on PATH as git.exe and never as "git". Checking the bare name alone
        // reports every prerequisite missing on the one platform whose list is shortest —
        // which is the whole of what stands between a Windows user and an impossible
        // message about tools they have installed.
        var tried = ExecutableLookup.Candidates("git", HostOs.Windows, null).ToList();

        Assert.Contains("git.EXE", tried);
        Assert.Contains("git.CMD", tried);
        Assert.DoesNotContain("git", tried);
    }

    [Fact]
    public void A_machine_with_its_own_pathext_is_followed_rather_than_the_default()
    {
        var tried = ExecutableLookup.Candidates("git", HostOs.Windows, ".EXE;.PS1").ToList();

        Assert.Equal(["git.EXE", "git.PS1"], tried);
    }

    [Fact]
    public void An_extension_that_is_already_there_is_left_alone()
    {
        Assert.Equal(["reg.exe"], ExecutableLookup.Candidates("reg.exe", HostOs.Windows, null).ToList());
    }

    [Theory]
    [InlineData(HostOs.Linux)]
    [InlineData(HostOs.MacOs)]
    public void Everywhere_else_a_command_is_spelled_as_it_is(HostOs os)
    {
        Assert.Equal(["git"], ExecutableLookup.Candidates("git", os, ".EXE;.CMD").ToList());
    }

    // ---- where a .NET runtime lives ----

    [Fact]
    public void Windows_looks_for_dotnet_under_program_files_and_nowhere_unix()
    {
        var roots = DotnetRuntimeLocator.CandidateRoots(os: HostOs.Windows).ToList();

        // The two lists share nothing below the environment variables, so a mistake in
        // either is invisible to a run on the other.
        Assert.DoesNotContain(roots, r => r.StartsWith("/usr/"));
        Assert.DoesNotContain(roots, r => r.StartsWith("/etc/"));
    }

    [Theory]
    [InlineData(HostOs.Linux)]
    [InlineData(HostOs.MacOs)]
    public void Unix_looks_where_microsofts_installer_puts_it(HostOs os)
    {
        var roots = DotnetRuntimeLocator.CandidateRoots(os: os).ToList();

        // The x64 root first: a default install on Apple Silicon is arm64 and cannot host
        // the game's x64 apphost.
        Assert.Contains("/usr/local/share/dotnet/x64", roots);
        Assert.Contains("/usr/local/share/dotnet", roots);
        Assert.True(
            roots.IndexOf("/usr/local/share/dotnet/x64") < roots.IndexOf("/usr/local/share/dotnet"),
            "the x64 root has to be tried before the default one");
    }

    [Theory]
    [InlineData(HostOs.Windows)]
    [InlineData(HostOs.Linux)]
    [InlineData(HostOs.MacOs)]
    public void A_preferred_root_outranks_everything_on_every_platform(HostOs os)
    {
        var roots = DotnetRuntimeLocator.CandidateRoots("/somewhere/chosen", os).ToList();

        Assert.Equal("/somewhere/chosen", roots[0]);
    }

    // ---- what an executable is called ----

    [Fact]
    public void Only_windows_puts_an_extension_on_a_program()
    {
        Assert.Equal("dotnet.exe", HostOs.Windows.Exe("dotnet"));
        Assert.Equal("dotnet", HostOs.Linux.Exe("dotnet"));
        Assert.Equal("dotnet", HostOs.MacOs.Exe("dotnet"));
    }

    [Fact]
    public void The_running_machine_is_one_of_the_three()
    {
        // Host.This is the single place Cairn asks the runtime what it is; everything else
        // takes the answer as a parameter. Worth one assertion that it answers at all.
        Assert.Contains(Host.This, new[] { HostOs.Windows, HostOs.MacOs, HostOs.Linux });

        Assert.Equal(
            OperatingSystem.IsWindows() ? ".exe" : "",
            Host.This.ExeSuffix());
    }
}
