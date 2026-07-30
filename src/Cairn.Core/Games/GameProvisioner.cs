using Cairn.Core.Runtime;

namespace Cairn.Core.Games;

public sealed record ProvisionStep(string Phase, string Detail, double? Fraction = null);

/// <summary>What a pack needs before it can launch, and whether it is already there.</summary>
public sealed record ProvisionPlan(string GameVersion, bool NeedsGame, bool NeedsRuntime)
{
    public bool AnythingToDo => NeedsGame || NeedsRuntime;

    public string Describe() => (NeedsGame, NeedsRuntime) switch
    {
        (true, true) => $"Vintage Story {GameVersion} and its .NET runtime need downloading.",
        (true, false) => $"Vintage Story {GameVersion} needs downloading.",
        (false, true) => $"Vintage Story {GameVersion} is installed but its .NET runtime is missing.",
        _ => $"Vintage Story {GameVersion} is ready.",
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
    /// <summary>What would need doing, without doing it. Cheap — no network.</summary>
    public ProvisionPlan Plan(string gameVersion, GameInstall? systemInstall = null)
    {
        var install = games.Find(gameVersion)
                      ?? (systemInstall?.Version == gameVersion ? systemInstall : null);

        if (install is null) return new ProvisionPlan(gameVersion, NeedsGame: true, NeedsRuntime: true);

        return new ProvisionPlan(gameVersion, NeedsGame: false, NeedsRuntime: !HasRuntime(install));
    }

    private bool HasRuntime(GameInstall install)
    {
        var options = new Launch.LaunchOptions { PreferredDotnetRoot = runtimes.RootFor(install) };
        return new Launch.GameLauncher(install).ResolveRuntime(options).Resolved;
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

        if (install is null)
        {
            progress?.Report(new ProvisionStep("resolving", $"looking up Vintage Story {gameVersion}"));

            var catalog = new GameCatalog(http);
            var releases = await catalog.ListReleasesAsync(includePreReleases: true, ct: ct)
                .ConfigureAwait(false);

            var release = releases.FirstOrDefault(r => r.Version == gameVersion)
                          ?? throw new GameInstallException(
                              $"No {GameCatalog.PlatformKey} download is published for {gameVersion}.");

            if (!release.CanInstall)
                throw new GameInstallException(
                    $"{gameVersion} ships as {release.Artifact.FileName} on this platform, which "
                    + "Cairn cannot install. Install it manually, then Cairn will detect it.");

            var installer = new GameInstaller(http, games);
            var relay = new Progress<InstallProgress>(p => progress?.Report(
                new ProvisionStep(p.Phase.ToString().ToLowerInvariant(),
                    p.Phase == InstallPhase.Downloading
                        ? $"Vintage Story {gameVersion} — {p.Done / 1024 / 1024} MB"
                        : p.Detail,
                    p.Fraction)));

            install = await installer.InstallAsync(release, relay, ct).ConfigureAwait(false);
        }

        if (!HasRuntime(install))
        {
            var major = install.RequiredFramework.Major;
            progress?.Report(new ProvisionStep("resolving", $"looking up .NET {major}"));

            var rid = DotnetRuntimeInstaller.RidFor(install.Architecture);
            var runtimeInstaller = new DotnetRuntimeInstaller(http, runtimes);
            var release = await runtimeInstaller.ResolveAsync(major, rid, ct).ConfigureAwait(false);

            var relay = new Progress<InstallProgressReport>(p => progress?.Report(
                new ProvisionStep(p.Phase,
                    p.Phase == "downloading"
                        ? $".NET {release.Version} — {p.Done / 1024 / 1024} MB"
                        : p.Phase,
                    p.Fraction)));

            await runtimeInstaller.InstallAsync(release, relay, ct).ConfigureAwait(false);
        }

        progress?.Report(new ProvisionStep("ready", $"Vintage Story {install.Version}", 1));
        return install;
    }
}
