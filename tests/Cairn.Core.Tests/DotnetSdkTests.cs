using Cairn.Core.Runtime;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Finding a toolchain that can compile, as opposed to one that can run the game.
///
/// The distinction is the whole reason this exists: Cairn's private .NET is a runtime, and
/// building Optimum needs an SDK. Getting the two confused produces a build that fails at
/// "dotnet build" after everything expensive has already happened.
/// </summary>
public class DotnetSdkTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-sdk-" + Guid.NewGuid().ToString("n")[..8]);

    public DotnetSdkTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>A .NET root carrying the given SDK versions.</summary>
    private string Plant(string name, params string[] sdkVersions)
    {
        var root = Path.Combine(_root, name);

        foreach (var v in sdkVersions)
            Directory.CreateDirectory(Path.Combine(root, "sdk", v));

        return root;
    }

    [Fact]
    public void A_root_with_an_sdk_reports_its_versions()
    {
        var root = Plant("with-sdk", "10.0.100", "9.0.304");

        var sdk = DotnetSdkLocator.Inspect(root);

        Assert.NotNull(sdk);
        Assert.Contains(new Version(10, 0, 100), sdk.Versions);
        Assert.Contains(new Version(9, 0, 304), sdk.Versions);
    }

    [Fact]
    public void A_runtime_only_root_is_not_an_sdk()
    {
        // The case that matters: Cairn's own private .NET looks exactly like this, and
        // treating it as a toolchain is the mistake this type exists to prevent.
        var root = Path.Combine(_root, "runtime-only");
        Directory.CreateDirectory(Path.Combine(root, "shared", "Microsoft.NETCore.App", "10.0.0"));

        Assert.Null(DotnetSdkLocator.Inspect(root));
    }

    [Fact]
    public void A_missing_root_is_null_rather_than_a_throw()
    {
        Assert.Null(DotnetSdkLocator.Inspect(Path.Combine(_root, "nothing-here")));
        Assert.Null(DotnetSdkLocator.Inspect(""));
    }

    [Fact]
    public void A_pre_release_suffix_is_still_a_version()
    {
        var root = Plant("preview", "10.0.100-rc.1");

        Assert.Equal(new Version(10, 0, 100), DotnetSdkLocator.Inspect(root)!.Versions.Single());
    }

    [Fact]
    public void A_later_feature_band_satisfies_the_pin()
    {
        // rollForward: latestFeature — 10.0.203 is what a machine updated since the pin was
        // written actually has, and refusing it would download a second SDK for nothing.
        var sdk = DotnetSdkLocator.Inspect(Plant("newer", "10.0.203"));

        Assert.True(sdk!.Satisfies(DotnetSdkLocator.RequiredForOptimum));
    }

    [Fact]
    public void An_earlier_feature_band_does_not()
    {
        var sdk = DotnetSdkLocator.Inspect(Plant("older", "10.0.100"));

        Assert.True(sdk!.Satisfies(new Version(10, 0, 100)));
        Assert.False(sdk.Satisfies(new Version(10, 0, 200)));
    }

    [Fact]
    public void A_newer_major_does_not_satisfy_a_pin_it_would_reject()
    {
        // The trap in treating this as ">= required": .NET 11 is newer in every ordinary
        // sense, and global.json's latestFeature rolls forward only within one major.minor,
        // so handing the build an 11 SDK fails at the pin — after the clone and decompile.
        var sdk = DotnetSdkLocator.Inspect(Plant("next-major", "11.0.100"));

        Assert.False(sdk!.Satisfies(DotnetSdkLocator.RequiredForOptimum));
    }

    [Fact]
    public void A_different_minor_does_not_satisfy_it_either()
    {
        var sdk = DotnetSdkLocator.Inspect(Plant("next-minor", "10.1.100"));

        Assert.False(sdk!.Satisfies(new Version(10, 0, 100)));
    }

    [Fact]
    public void Find_takes_a_preferred_root_over_whatever_the_machine_has()
    {
        var root = Plant("preferred", "10.0.100");

        var found = DotnetSdkLocator.Find(DotnetSdkLocator.RequiredForOptimum, root);

        Assert.NotNull(found);
        Assert.Equal(Path.GetFullPath(root), Path.GetFullPath(found.Root));
    }

    [Fact]
    public void A_preferred_root_that_cannot_serve_does_not_stop_the_search()
    {
        // A stale or half-deleted private SDK must not mask a working system one, or a
        // machine that could build would report that it cannot.
        var useless = Plant("too-old", "9.0.100");

        var found = DotnetSdkLocator.Find(new Version(9, 0, 100), useless);

        Assert.NotNull(found);
    }

    [Fact]
    public void The_executable_sits_at_the_root()
    {
        var sdk = DotnetSdkLocator.Inspect(Plant("exe", "10.0.100"))!;

        Assert.Equal(
            Path.Combine(sdk.Root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"),
            sdk.Executable);
    }
}
