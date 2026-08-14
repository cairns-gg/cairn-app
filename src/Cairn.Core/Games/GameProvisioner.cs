using Cairn.Core.Runtime;

namespace Cairn.Core.Games;

public sealed record ProvisionStep(string Phase, string Detail, double? Fraction = null);

/// <summary>What a pack needs before it can launch, and whether it is already there.</summary>
public sealed record ProvisionPlan(string GameVersion, bool NeedsGame, bool NeedsRuntime)
{
    public bool AnythingToDo => NeedsGame || NeedsRuntime;

    public string Describe() => (NeedsGame, NeedsRuntime) switch
    {
        (true, true) => Lang.Get("provision-needs-both", GameVersion),
            (true, false) => Lang.Get("provision-needs-game", GameVersion),
            (false, true) => Lang.Get("provision-needs-runtime", GameVersion),
            _ => Lang.Get("provision-ready", GameVersion),
    };
}

/// <summary>
/// Gets a game version to a launchable state: downloads the game if absent, then a
/// matching private .NET runtime if the machine has none.
///
/// Exists so neither front-end has to teach the user that a pack's game version and its
/// .NET major are separate things they must each go and satisfy by hand.
/// </summary>
public sealed class GameProvisioner(HttpClient http, GameStore games, RuntimeStore runtimes)
{
    /// <summary>
    /// What would need doing to make <paramref name="gameVersion"/> launchable, without
    /// doing it. Cheap — no network.
    ///
    /// Answers for the stock install of that version. A pack that runs something else —
    /// a client built for this machine — must ask <see cref="PlanFor"/> about the install
    /// it will actually launch; see there for why the two can disagree.
    /// </summary>
    public ProvisionPlan Plan(string gameVersion, GameInstall? systemInstall = null)
    {
        var install = games.Find(gameVersion)
                      ?? (systemInstall?.Version == gameVersion ? systemInstall : null);

        if (install is null) return new ProvisionPlan(gameVersion, NeedsGame: true, NeedsRuntime: true);

        return new ProvisionPlan(gameVersion, NeedsGame: false, NeedsRuntime: !HasRuntime(install));
    }

    /// <summary>
    /// What one particular install still needs before it can start.
    ///
    /// Exists because "is there a .NET this can run on" is a question about an install, not
    /// about a version, and two installs of the same version routinely answer it
    /// differently:
    ///
    /// - on Apple Silicon the native client needs an arm64 runtime while the stock x64
    ///   download needs an x64 one, and a machine commonly has exactly one of the two;
    /// - an install that brings its own .NET — a Flatpak — answers yes for itself and for
    ///   nothing else on the machine.
    ///
    /// Asking the stock install and then launching a different one is how a pack refuses to
    /// start having just been told the version was ready. The game is never part of this
    /// answer: the install is in front of us, so only its runtime can be missing.
    /// </summary>
    public ProvisionPlan PlanFor(GameInstall install) =>
        new(install.Version, NeedsGame: false, NeedsRuntime: !HasRuntime(install));

    private bool HasRuntime(GameInstall install)
    {
        var options = new Launch.LaunchOptions { PreferredDotnetRoot = runtimes.RootFor(install) };
        return new Launch.GameLauncher(install).ResolveRuntime(options).Resolved;
    }

    /// <summary>
    /// Ensures a server for <paramref name="gameVersion"/> can start, downloading whatever
    /// is missing, and returns it pointed at its server binary.
    ///
    /// The dedicated download where one exists — 51 MB against the client's 600, on a box
    /// that will never draw a frame — but a client install already present is used as it
    /// stands, because every client ships VintagestoryServer beside its own binary.
    /// Fetching a second copy of a server that is already on the disk is the mistake the
    /// Flatpak work existed to stop making.
    /// </summary>
    public async Task<GameInstall> EnsureServerAsync(
        string gameVersion,
        IProgress<ProvisionStep>? progress = null,
        CancellationToken ct = default)
    {
        var server = games.FindServer(gameVersion);

        if (server is null)
        {
            await FetchAsync(gameVersion, GameCatalog.ServerPlatformKeys, progress, ct)
                .ConfigureAwait(false);

            server = games.FindServer(gameVersion)
                     ?? throw new GameInstallException(Lang.Get("install-no-server", gameVersion));
        }

        await EnsureRuntimeAsync(server, progress, ct).ConfigureAwait(false);

        progress?.Report(new ProvisionStep("ready", $"Vintage Story {server.Version} server", 1));
        return server;
    }

    /// <summary>
    /// Ensures <paramref name="gameVersion"/> can launch, downloading whatever is missing.
    /// </summary>
    public async Task<GameInstall> EnsureAsync(
        string gameVersion,
        GameInstall? systemInstall = null,
        IProgress<ProvisionStep>? progress = null,
        CancellationToken ct = default)
    {
        var install = games.Find(gameVersion)
                      ?? (systemInstall?.Version == gameVersion ? systemInstall : null);

        install ??= await FetchAsync(gameVersion, GameCatalog.PlatformKeys, progress, ct)
            .ConfigureAwait(false);

        await EnsureRuntimeAsync(install, progress, ct).ConfigureAwait(false);

        progress?.Report(new ProvisionStep("ready", $"Vintage Story {install.Version}", 1));
        return install;
    }

    /// <summary>Downloads and unpacks one version's artifact for the given platform keys.</summary>
    private async Task<GameInstall> FetchAsync(
        string gameVersion,
        IReadOnlyList<string> platformKeys,
        IProgress<ProvisionStep>? progress,
        CancellationToken ct)
    {
        progress?.Report(new ProvisionStep("resolving", $"looking up Vintage Story {gameVersion}"));

        var catalog = new GameCatalog(http);
        var releases = await catalog
            .ListReleasesAsync(includePreReleases: true, platformKeys: platformKeys, ct: ct)
            .ConfigureAwait(false);

        var release = releases.FirstOrDefault(r => r.Version == gameVersion)
                      ?? throw new GameInstallException(Lang.Get(
                             "install-no-download", string.Join(" or ", platformKeys), gameVersion));

        if (!release.CanInstall)
            throw new GameInstallException(Lang.Get(
                "install-cannot-install", gameVersion, release.Artifact.FileName));

        var installer = new GameInstaller(http, games);
        var relay = new Progress<InstallProgress>(p => progress?.Report(
            new ProvisionStep(p.Phase.ToString().ToLowerInvariant(),
                p.Phase == InstallPhase.Downloading
                    ? $"Vintage Story {gameVersion} — {p.Done / 1024 / 1024} MB"
                    : p.Detail,
                p.Fraction)));

        return await installer.InstallAsync(release, relay, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads a private .NET for <paramref name="install"/> if it cannot find one.
    ///
    /// Takes the install rather than a version because both halves of the answer come from
    /// it: which .NET major it asks for, and — the part that a version cannot supply — which
    /// architecture it has to be. Fetching the runtime the stock download needs and handing
    /// it to a client built for this machine leaves both installed and neither launchable.
    /// </summary>
    public async Task EnsureRuntimeAsync(
        GameInstall install,
        IProgress<ProvisionStep>? progress = null,
        CancellationToken ct = default)
    {
        if (HasRuntime(install)) return;

        var major = install.RequiredFramework.Major;
        progress?.Report(new ProvisionStep("resolving", $"looking up .NET {major}"));

        var rid = DotnetRuntimeInstaller.RidFor(install.Architecture);
        var runtimeInstaller = new DotnetRuntimeInstaller(http, runtimes);
        var release = await runtimeInstaller.ResolveAsync(major, rid, ct).ConfigureAwait(false);

        var relay = new Progress<InstallProgressReport>(p => progress?.Report(
            new ProvisionStep(p.Phase,
                p.Phase == "downloading"
                    ? $".NET {release.Version} ({rid}) — {p.Done / 1024 / 1024} MB"
                    : p.Phase,
                p.Fraction)));

        await runtimeInstaller.InstallAsync(release, relay, ct).ConfigureAwait(false);
    }
}
