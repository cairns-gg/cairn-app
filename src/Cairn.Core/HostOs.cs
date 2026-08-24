using System.Runtime.InteropServices;

namespace Cairn.Core;

/// <summary>
/// Which operating system a decision is being made for.
///
/// A parameter rather than an ambient question, so that all three answers can be checked
/// from one machine. Every platform fork in Cairn is a branch that only one platform's CI
/// job — or one developer's laptop — can reach, and a branch nothing reaches is a branch
/// nothing checks: the zip half of <see cref="ArchiveExtractor"/> was dead code on macOS
/// and Linux and broken on Windows for as long as anyone can tell, because the only way to
/// execute it was to be running Windows.
///
/// <see cref="OptimumProvisioner"/> arrived at this first, for the same reason and in the
/// same words — "a parameter so all three can be tested from one host". This is that idea
/// with one type instead of one per caller.
/// </summary>
public enum HostOs
{
    Windows,
    MacOs,
    Linux,
}

public static class Host
{
    /// <summary>
    /// The machine this is running on.
    ///
    /// Deliberately the only place in Cairn that asks. Everything that varies by platform
    /// takes a <see cref="HostOs"/> and defaults to this, so production code reads the same
    /// as it always did and a test can ask any of the three.
    /// </summary>
    public static HostOs This =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? HostOs.Windows
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? HostOs.MacOs
        : HostOs.Linux;

    /// <summary>
    /// The suffix an executable carries. ".exe" on Windows and nothing anywhere else —
    /// which is the whole of the difference, and is wrong often enough to be worth naming
    /// once rather than spelling out at each of the dozen places that need it.
    /// </summary>
    public static string ExeSuffix(this HostOs os) => os == HostOs.Windows ? ".exe" : "";

    /// <summary>A program's filename on this platform: "dotnet" or "dotnet.exe".</summary>
    public static string Exe(this HostOs os, string name) => name + os.ExeSuffix();
}
