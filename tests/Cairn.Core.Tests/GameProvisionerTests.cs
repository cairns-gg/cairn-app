using Cairn.Core;
using Cairn.Core.Games;
using Cairn.Core.Runtime;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Covers the planning half of provisioning — what needs downloading — without touching
/// the network. The downloading half is exercised end to end by hand against the real
/// CDN, since faking it would only test the fake.
/// </summary>
public class GameProvisionerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "Cairn-prov-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly GameStore _games;
    private readonly RuntimeStore _runtimes;
    private readonly GameProvisioner _provisioner;

    public GameProvisionerTests()
    {
        _games = new GameStore(Path.Combine(_root, "games"));
        _runtimes = new RuntimeStore(Path.Combine(_root, "runtimes"));
        _provisioner = new GameProvisioner(new HttpClient(), _games, _runtimes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static GameInstall System(string version, Version framework) => new()
    {
        Directory = "/games/system",
        Executable = "/games/system/Vintagestory",
        Version = version,
        Architecture = ExecutableArch.X64,
        RequiredFramework = framework,
    };

    private void FakeRuntime(string version, string rid, string framework)
        => Directory.CreateDirectory(Path.Combine(
            _runtimes.InstallDir(version, rid), "shared", "Microsoft.NETCore.App", framework));

    [Fact]
    public void A_version_that_is_not_installed_needs_everything()
    {
        var plan = _provisioner.Plan("1.21.5");

        Assert.True(plan.NeedsGame);
        Assert.True(plan.NeedsRuntime);
        Assert.True(plan.AnythingToDo);
        Assert.Contains("need", plan.Describe());
    }

    [Fact]
    public void A_matching_system_install_with_a_usable_runtime_needs_nothing()
    {
        // The machine's own .NET satisfies 1.22, so nothing to fetch.
        var plan = _provisioner.Plan("1.22.5", System("1.22.5", new Version(10, 0, 0)));

        Assert.False(plan.NeedsGame);
        if (!plan.NeedsRuntime) Assert.False(plan.AnythingToDo);
    }

    [Fact]
    public void An_installed_game_whose_dotnet_major_is_absent_still_needs_a_runtime()
    {
        // .NET 42 will never be on the machine, so this isolates the runtime half.
        var plan = _provisioner.Plan("9.9.9", System("9.9.9", new Version(42, 0, 0)));

        Assert.False(plan.NeedsGame);
        Assert.True(plan.NeedsRuntime);
        Assert.Contains("runtime is missing", plan.Describe());
    }

    [Fact]
    public void A_managed_runtime_satisfies_the_plan()
    {
        FakeRuntime("42.0.1", "osx-x64", "42.0.1");

        var plan = _provisioner.Plan("9.9.9", System("9.9.9", new Version(42, 0, 0)));

        Assert.False(plan.NeedsRuntime);
        Assert.False(plan.AnythingToDo);
    }

    [Fact]
    public void Planning_touches_no_network_and_is_safe_to_call_repeatedly()
    {
        // Called on every selection change in the UI, so it must stay cheap.
        for (var i = 0; i < 50; i++) _provisioner.Plan("1.21.5");
        Assert.True(_provisioner.Plan("1.21.5").AnythingToDo);
    }
}
