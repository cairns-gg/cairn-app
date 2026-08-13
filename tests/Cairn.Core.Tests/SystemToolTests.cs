using Cairn.Core;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Naming a Windows system tool outright rather than letting CreateProcess look for it.
///
/// The search puts the calling process's current directory ahead of the system directory,
/// and Cairn does not choose its own: a cairn:// launch inherits whatever the shell handed
/// it. Cairn runs reg while registering that very protocol handler, and again on each
/// Windows game install.
/// </summary>
public class SystemToolTests
{
    [Fact]
    public void On_Windows_a_system_tool_is_named_by_full_path()
    {
        if (!OperatingSystem.IsWindows()) return;

        var reg = ExecutableLookup.SystemTool("reg.exe");

        Assert.True(Path.IsPathRooted(reg), $"'{reg}' is not rooted, so it would be searched for");
        Assert.Equal(Environment.SystemDirectory, Path.GetDirectoryName(reg));
        Assert.True(File.Exists(reg), $"'{reg}' does not exist");
    }

    /// <summary>
    /// Off Windows there is no system directory and nothing calls this, so it returns the
    /// name unchanged rather than inventing a path — no worse than what it replaces.
    /// </summary>
    [Fact]
    public void Off_Windows_it_is_the_name_unchanged()
    {
        if (OperatingSystem.IsWindows()) return;

        Assert.Equal("reg.exe", ExecutableLookup.SystemTool("reg.exe"));
    }

    /// <summary>
    /// The distinction the fix rests on: a rooted path is used as given, while a bare name
    /// is something the operating system goes looking for.
    /// </summary>
    [Fact]
    public void A_bare_name_is_the_thing_being_avoided()
    {
        Assert.False(Path.IsPathRooted("reg"));
        Assert.False(Path.IsPathRooted("reg.exe"));
    }
}
