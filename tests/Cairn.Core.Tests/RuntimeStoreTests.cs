using Cairn.Core;
using Cairn.Core.Runtime;
using Xunit;

namespace Cairn.Core.Tests;

public class RuntimeStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "Cairn-rtstore-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly RuntimeStore _store;

    public RuntimeStoreTests() => _store = new RuntimeStore(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string FakeRuntime(string version, string rid, params string[] frameworks)
    {
        var dir = _store.InstallDir(version, rid);
        foreach (var f in frameworks)
            Directory.CreateDirectory(Path.Combine(dir, "shared", "Microsoft.NETCore.App", f));
        return dir;
    }

    private static GameInstall Game(Version required, ExecutableArch arch = ExecutableArch.X64) => new()
    {
        Directory = "/games/x",
        Executable = "/games/x/Vintagestory",
        Version = "1.21.5",
        Architecture = arch,
        RequiredFramework = required,
    };

    [Theory]
    [InlineData("8.0.29", "osx-x64")]
    [InlineData("10.0.10", "linux-x64")]
    public void Valid_runtime_directory_names_are_accepted(string version, string rid)
        => Assert.NotNull(_store.InstallDir(version, rid));

    [Theory]
    [InlineData("../escape", "osx-x64")]
    [InlineData("8.0.29", "../x")]
    [InlineData("", "osx-x64")]
    public void Names_that_could_escape_the_store_are_refused(string version, string rid)
        => Assert.Throws<ArgumentException>(() => _store.InstallDir(version, rid));

    [Fact]
    public void An_empty_store_finds_nothing()
    {
        Assert.Empty(_store.ListInstalled());
        Assert.Null(_store.Find(new Version(8, 0, 0), ExecutableArch.X64));
        Assert.Null(_store.RootFor(Game(new Version(8, 0, 0))));
    }

    [Fact]
    public void A_matching_runtime_is_offered_for_a_game()
    {
        var dir = FakeRuntime("8.0.29", "osx-x64", "8.0.29");

        Assert.Equal(dir, _store.RootFor(Game(new Version(8, 0, 0))));
    }

    [Fact]
    public void A_runtime_of_the_wrong_major_is_not_offered()
    {
        FakeRuntime("8.0.29", "osx-x64", "8.0.29");

        // A net10.0 game cannot run on .NET 8, so this must not resolve.
        Assert.Null(_store.RootFor(Game(new Version(10, 0, 0))));
    }

    [Fact]
    public void Several_runtimes_can_coexist_and_each_game_gets_its_own()
    {
        var eight = FakeRuntime("8.0.29", "osx-x64", "8.0.29");
        var ten = FakeRuntime("10.0.10", "osx-x64", "10.0.10");

        Assert.Equal(2, _store.ListInstalled().Count());
        Assert.Equal(eight, _store.RootFor(Game(new Version(8, 0, 0))));
        Assert.Equal(ten, _store.RootFor(Game(new Version(10, 0, 0))));
    }

    [Fact]
    public void Remove_deletes_only_the_named_runtime()
    {
        FakeRuntime("8.0.29", "osx-x64", "8.0.29");
        FakeRuntime("10.0.10", "osx-x64", "10.0.10");

        _store.Remove("8.0.29", "osx-x64");

        Assert.Single(_store.ListInstalled());
        Assert.Null(_store.RootFor(Game(new Version(8, 0, 0))));
        Assert.NotNull(_store.RootFor(Game(new Version(10, 0, 0))));
    }

    [Fact]
    public void A_directory_with_no_shared_framework_is_not_a_runtime()
    {
        Directory.CreateDirectory(_store.InstallDir("8.0.29", "osx-x64"));
        Assert.Empty(_store.ListInstalled());
    }

    [Theory]
    [InlineData(ExecutableArch.X64, "x64")]
    [InlineData(ExecutableArch.Arm64, "arm64")]
    public void Rid_matches_the_requested_architecture(ExecutableArch arch, string expectedCpu)
    {
        var rid = DotnetRuntimeInstaller.RidFor(arch);

        Assert.EndsWith(expectedCpu, rid);
        Assert.Contains(OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsWindows() ? "win" : "linux", rid);
    }
}
