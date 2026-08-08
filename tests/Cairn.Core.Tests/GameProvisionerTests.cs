using System.Buffers.Binary;
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

    private static GameInstall System(
        string version,
        Version framework,
        ExecutableArch arch = ExecutableArch.X64,
        string? dotnetRoot = null) => new()
    {
        Directory = "/games/system",
        Executable = "/games/system/Vintagestory",
        Version = version,
        Architecture = arch,
        RequiredFramework = framework,
        DotnetRoot = dotnetRoot,
    };

    private void FakeRuntime(
        string version, string rid, string framework, ExecutableArch arch = ExecutableArch.Unknown)
    {
        var root = _runtimes.InstallDir(version, rid);
        Directory.CreateDirectory(Path.Combine(root, "shared", "Microsoft.NETCore.App", framework));

        if (arch == ExecutableArch.Unknown) return;

        // A root with no host binary reports an unknown architecture, which matches
        // anything — fine for the cases where architecture is not the point, useless for
        // the ones where it is. Eight bytes of Mach-O header is a whole answer to
        // ExecutableImage, and it parses the same on every host.
        var host = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var header = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0xFEEDFACF);
        BinaryPrimitives.WriteInt32LittleEndian(
            header.AsSpan(4), arch == ExecutableArch.Arm64 ? 0x0100_000C : 0x0100_0007);
        File.WriteAllBytes(host, header);
    }

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
    public void An_install_in_front_of_us_is_asked_only_about_its_runtime()
    {
        FakeRuntime("42.0.1", "osx-arm64", "42.0.1", ExecutableArch.Arm64);

        var plan = _provisioner.PlanFor(System("9.9.9", new Version(42, 0, 0), ExecutableArch.Arm64));

        Assert.False(plan.NeedsGame);
        Assert.False(plan.NeedsRuntime);
        Assert.False(plan.AnythingToDo);
    }

    [Fact]
    public void A_runtime_the_stock_install_can_use_does_not_answer_for_a_client_of_another_architecture()
    {
        // The machine this was written for: an x64 stock install hosted by an x64 .NET,
        // beside an Optimum build made for the machine itself, which is arm64. Asking the
        // version reports everything ready — and the client that will actually launch has
        // nothing on the machine that can host it.
        FakeRuntime("42.0.1", "osx-x64", "42.0.1", ExecutableArch.X64);

        var stock = System("9.9.9", new Version(42, 0, 0));
        var native = System("9.9.9", new Version(42, 0, 0), ExecutableArch.Arm64);

        Assert.False(_provisioner.Plan("9.9.9", stock).NeedsRuntime);

        Assert.True(_provisioner.PlanFor(native).NeedsRuntime);
        Assert.Contains("runtime is missing", _provisioner.PlanFor(native).Describe());
    }

    [Fact]
    public void A_runtime_an_install_brings_with_it_answers_for_that_install_alone()
    {
        // The other way the two questions come apart: a Flatpak carries the .NET the game
        // runs on, and the immutable hosts it is popular on may have no other. It satisfies
        // the install that brought it and nothing else on the machine.
        var bundled = Path.Combine(_root, "deploy", "files", "lib", "dotnet");
        Directory.CreateDirectory(Path.Combine(bundled, "shared", "Microsoft.NETCore.App", "42.0.1"));

        var flatpak = System("9.9.9", new Version(42, 0, 0), dotnetRoot: bundled);
        var built = System("9.9.9", new Version(42, 0, 0));

        Assert.False(_provisioner.Plan("9.9.9", flatpak).NeedsRuntime);
        Assert.True(_provisioner.PlanFor(built).NeedsRuntime);
    }

    [Fact]
    public void Planning_touches_no_network_and_is_safe_to_call_repeatedly()
    {
        // Called on every selection change in the UI, so it must stay cheap.
        for (var i = 0; i < 50; i++) _provisioner.Plan("1.21.5");
        Assert.True(_provisioner.Plan("1.21.5").AnythingToDo);
    }
}
