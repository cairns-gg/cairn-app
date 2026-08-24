using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cairn.Core;
using Cairn.Core.Games;
using Cairn.Core.Launch;
using Cairn.Core.Runtime;

namespace Cairn.App.ViewModels;

/// <summary>A game version Cairn can launch: one it installed, or the machine's own.</summary>
public class InstalledGameViewModel(
    GameInstall install, RuntimeResolution runtime, bool managed = true, bool external = false)
    : ViewModelBase
{
    public GameInstall Install { get; } = install;

    /// <summary>
    /// False for an install Cairn merely found. Listing it matters because a pack will
    /// happily launch from it — leaving it out is what made removing a managed 1.22.5 look
    /// like the version had vanished while the pack kept working.
    /// </summary>
    public bool IsManaged { get; } = managed;

    /// <summary>
    /// A client somebody built and pointed Cairn at. Not managed — Cairn will not update it
    /// and must not delete it — but not merely found either: it is here because somebody
    /// said so, and the way back out is to say otherwise.
    /// </summary>
    public bool IsExternal { get; } = external;

    /// <summary>Cairn only deletes what Cairn installed. Forgetting one of theirs is not deleting.</summary>
    public bool CanRemove => IsManaged || IsExternal;

    /// <summary>Names the button for what pressing it does, which is not the same on both.</summary>
    public string RemoveLabel =>
        IsExternal ? Lang.Get("prefs-version-forget") : Lang.Get("prefs-version-remove");

    public string Origin => (IsExternal, IsManaged) switch
    {
        (true, _) => Lang.Get("games-you-pointed"),
        (_, true) => Lang.Get("games-installed-by-cairn"),
        _ => Lang.Get("games-found-here"),
    };

    public string Version => Install.Version;

    /// <summary>
    /// What the row says. Describe rather than Version, because a build made from source
    /// reports the version it was built from — so an Optimum 1.22.5 and the stock 1.22.5
    /// were two rows both reading "1.22.5", one of which cannot be replaced by a download.
    /// </summary>
    public string Display => Install.Describe;
    public string Directory => Install.Directory;
    public string Needs => Lang.Get("games-needs-dotnet", Install.RequiredFramework);
    public string RuntimeLine => runtime.Describe();
    public int RequiredDotnetMajor => Install.RequiredFramework.Major;

    /// <summary>
    /// Each game version pins its own .NET major — 1.21 wants .NET 8, 1.22 wants .NET 10 —
    /// so an install can be present and still unable to start.
    /// </summary>
    public bool RuntimeMissing => !runtime.Resolved;
}

/// <summary>A version published in the catalog.</summary>
public partial class AvailableGameViewModel(GameRelease release, bool installed) : ViewModelBase
{
    public GameRelease Release { get; } = release;

    public string Version => Release.Version;
    public string Size => Release.Artifact.FileSize;
    public bool CanInstall => Release.CanInstall;

    [ObservableProperty] public partial bool IsInstalled { get; set; } = installed;

    /// <summary>Derived from the property, not the constructor argument, so it stays
    /// correct after an install completes.</summary>
    public string Note => CanInstall
        ? IsInstalled ? Lang.Get("games-note-installed") : ""
        : Lang.Get("games-note-not-downloadable");

    partial void OnIsInstalledChanged(bool value) => OnPropertyChanged(nameof(Note));
}

/// <summary>Installing and removing game versions.</summary>
public partial class GamesViewModel : ViewModelBase
{
    private readonly HttpClient _http;
    private readonly GameStore _store;
    private readonly RuntimeStore _runtimes;
    private readonly GameCatalog _catalog;
    private readonly Action<string> _log;
    private readonly Action _onLibraryChanged;
    /// <summary>
    /// The install Cairn found rather than installed. Not readonly: Preferences can point
    /// Cairn at a different one, and a list still holding the old answer goes on offering an
    /// install that is no longer being used. See <see cref="SystemInstallChanged"/>.
    /// </summary>
    private GameInstall? _system;
    private readonly Func<string, IReadOnlyList<string>> _packsUsing;

    /// <summary>
    /// Packs pointed at a particular directory, as opposed to targeting a version.
    ///
    /// A separate question from <see cref="_packsUsing"/> and not answerable from it: two
    /// clients for the same game version can both be on the machine, and forgetting one of
    /// them costs the packs pointed at *it* rather than every pack on that version.
    /// </summary>
    private readonly Func<string, IReadOnlyList<string>> _packsPointedAt;

    public GamesViewModel(
        HttpClient http, GameStore store, RuntimeStore runtimes,
        Action<string> log, Action onLibraryChanged,
        GameInstall? system = null,
        Func<string, IReadOnlyList<string>>? packsUsing = null,
        Func<string, IReadOnlyList<string>>? packsPointedAt = null)
    {
        _http = http;
        _store = store;
        _runtimes = runtimes;
        _catalog = new GameCatalog(http);
        _log = log;
        _onLibraryChanged = onLibraryChanged;
        _system = system;
        _packsUsing = packsUsing ?? (_ => []);
        _packsPointedAt = packsPointedAt ?? (_ => []);

        RefreshInstalled();
    }

    public ObservableCollection<InstalledGameViewModel> Installed { get; } = [];
    public ObservableCollection<string> ManagedRuntimes { get; } = [];
    public ObservableCollection<AvailableGameViewModel> Available { get; } = [];

    [ObservableProperty] public partial InstalledGameViewModel? SelectedInstalled { get; set; }
    [ObservableProperty] public partial AvailableGameViewModel? SelectedAvailable { get; set; }
    [ObservableProperty] public partial bool IncludePreReleases { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Progress { get; set; } = "";
    [ObservableProperty] public partial double ProgressFraction { get; set; }

    /// <summary>
    /// True while the running step cannot report how far along it is. Without this the bar
    /// sits at empty through unpacking and through the Windows installer, which is the
    /// slowest step of all and the one most likely to be mistaken for a hang.
    /// </summary>
    [ObservableProperty] public partial bool ProgressIndeterminate { get; set; } = true;
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial bool CatalogLoaded { get; set; }

    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool NotBusy => !IsBusy;
    public string StoreRoot => _store.Root;

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(NotBusy));
        RefreshCatalogCommand.NotifyCanExecuteChanged();
        InstallSelectedCommand.NotifyCanExecuteChanged();
        RequestRemoveCommand.NotifyCanExecuteChanged();
        InstallRuntimeCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedAvailableChanged(AvailableGameViewModel? value)
        => InstallSelectedCommand.NotifyCanExecuteChanged();

    partial void OnSelectedInstalledChanged(InstalledGameViewModel? value)
    {
        ConfirmingRemove = false;
        RequestRemoveCommand.NotifyCanExecuteChanged();
        InstallRuntimeCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanInstallRuntime))]
    private async Task InstallRuntime()
    {
        var major = SelectedInstalled!.RequiredDotnetMajor;

        IsBusy = true;
        Error = null;
        ProgressFraction = 0;
        ProgressIndeterminate = true;
        Progress = Lang.Get("games-resolving-dotnet", major);

        try
        {
            var installer = new DotnetRuntimeInstaller(_http, _runtimes);
            var rid = DotnetRuntimeInstaller.RidFor(SelectedInstalled.Install.Architecture);
            var release = await installer.ResolveAsync(major, rid);

            var progress = new Progress<InstallProgressReport>(p =>
            {
                ProgressIndeterminate = p.Fraction is null;
                ProgressFraction = p.Fraction ?? 0;
                Progress = p.Phase == "downloading"
                    ? Lang.Get("games-downloading-dotnet", release.Version, p.Done / 1024 / 1024)
                    : p.Phase;
            });

            var installed = await installer.InstallAsync(release, progress);

            _log($"installed .NET {release.Version} -> {installed.Root}");
            Progress = Lang.Get("games-installed-dotnet", release.Version);

            RefreshInstalled();
            _onLibraryChanged();
        }
        catch (Exception e)
        {
            Error = e.Message;
            Progress = "";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanInstallRuntime => !IsBusy && SelectedInstalled is { RuntimeMissing: true };

    /// <summary>
    /// Preferences pointed Cairn at a different install, or at none. Refreshes here rather
    /// than leaving the caller to remember, because the list is what the change is visible in.
    /// </summary>
    public void SystemInstallChanged(GameInstall? system)
    {
        _system = system;
        RefreshInstalled();
    }

    public void RefreshInstalled()
    {
        Installed.Clear();

        foreach (var install in _store.ListInstalled())
            Installed.Add(Describe(install, managed: true));

        // Clients somebody built and pointed Cairn at. Listed for the same reason the
        // machine's own install is: a pack runs from one, so a list that left it out would
        // disagree with what actually starts — and this is the only place it can be
        // un-pointed-at.
        foreach (var external in _store.ListExternal())
            if (!Installed.Any(i => SamePath(i.Directory, external.Directory)))
                Installed.Add(Describe(external, managed: false, external: true));

        // The machine's own install, if it is not the same directory as a managed one. A
        // pack launches from it whenever its version matches (GameLibrary.ForVersion), so
        // this list would otherwise disagree with what actually runs.
        if (_system is not null && !Installed.Any(i => SamePath(i.Directory, _system.Directory)))
            Installed.Add(Describe(_system, managed: false));

        ManagedRuntimes.Clear();
        foreach (var r in _runtimes.ListInstalled())
            ManagedRuntimes.Add($"{Path.GetFileName(r.Root)}  ({string.Join(", ", r.Frameworks)})");

        foreach (var a in Available) a.IsInstalled = _store.IsInstalled(a.Version);
    }

    private InstalledGameViewModel Describe(GameInstall install, bool managed, bool external = false)
    {
        // Resolve against a managed runtime too, otherwise a game we can already run
        // would be reported as unusable.
        var options = new LaunchOptions { PreferredDotnetRoot = _runtimes.RootFor(install) };
        return new InstalledGameViewModel(
            install, new GameLauncher(install).ResolveRuntime(options), managed, external);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>Preselects a version, so "install the version this pack needs" can jump here.</summary>
    public void Preselect(string version)
        => SelectedAvailable = Available.FirstOrDefault(a => a.Version == version);

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RefreshCatalog()
    {
        IsBusy = true;
        Error = null;

        try
        {
            var releases = await _catalog.ListReleasesAsync(IncludePreReleases);

            Available.Clear();
            foreach (var r in releases)
                Available.Add(new AvailableGameViewModel(r, _store.IsInstalled(r.Version)));

            CatalogLoaded = true;
            _log($"catalog: {Available.Count} versions published for {GameCatalog.PlatformDescription}");
        }
        catch (Exception e)
        {
            Error = Lang.Get("games-catalog-failed", e.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallSelected()
    {
        var chosen = SelectedAvailable!;

        IsBusy = true;
        Error = null;
        ProgressFraction = 0;
        ProgressIndeterminate = true;
        Progress = Lang.Get("games-starting", chosen.Version);

        try
        {
            var installer = new GameInstaller(_http, _store);

            var progress = new Progress<InstallProgress>(p =>
            {
                ProgressIndeterminate = p.Fraction is null;
                ProgressFraction = p.Fraction ?? 0;
                Progress = p.Phase switch
                {
                    InstallPhase.Downloading =>
                        Lang.Get("games-downloading", chosen.Version, p.Done / 1024 / 1024)
                        + (p.Fraction is { } f ? Lang.Get("games-percent", f * 100) : ""),
                    InstallPhase.Verifying => Lang.Get("games-verifying"),
                    // Carries "… — 412 MB written" once unpacking or the installer is
                    // under way, which is the only thing moving during the longest step.
                    InstallPhase.Extracting => p.Detail,
                    InstallPhase.Finishing => Lang.Get("games-arranging"),
                    _ => Lang.Get("games-installed-detail", p.Detail),
                };
            });

            var install = await installer.InstallAsync(chosen.Release, progress);

            _log($"installed game {install.Version} -> {install.Directory}");
            Progress = Lang.Get("games-installed-version", install.Version);
            chosen.IsInstalled = true;

            RefreshInstalled();
            _onLibraryChanged();
        }
        catch (Exception e)
        {
            Error = e.Message;
            Progress = "";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanInstall => !IsBusy && SelectedAvailable is { CanInstall: true, IsInstalled: false };

    /// <summary>
    /// Armed by Remove, so a version in use is not deleted on one click. Mirrors the pack
    /// delete flow rather than inventing a second confirmation style.
    /// </summary>
    [ObservableProperty] public partial bool ConfirmingRemove { get; set; }
    [ObservableProperty] public partial string RemoveConsequence { get; set; } = "";

    /// <summary>
    /// Names the yes button for what it does. "Remove" over a client somebody built reads
    /// as an offer to delete their build, which is the one thing this will never do.
    /// </summary>
    [ObservableProperty] public partial string ConfirmRemoveLabel { get; set; } =
        Lang.Get("prefs-version-remove-confirm");

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void RequestRemove()
    {
        var chosen = SelectedInstalled!;

        // Two different consequences, so two different sentences. Removing a version costs
        // a download; forgetting a client of theirs costs nothing at all on disk and drops
        // the packs pointed at it back to the stock game — which is the part worth saying,
        // since "forget" sounds like it might delete the twenty minutes they spent.
        if (chosen.IsExternal)
        {
            var pointed = _packsPointedAt(chosen.Directory);

            ConfirmRemoveLabel = Lang.Get("prefs-version-forget-confirm");
            RemoveConsequence = pointed.Count == 0
                ? Lang.Get("games-forget-unused", chosen.Display)
                : Lang.Plural("games-forget-used", pointed.Count, Listed(pointed), chosen.Display);

            ConfirmingRemove = true;
            return;
        }

        var packs = _packsUsing(chosen.Version);

        ConfirmRemoveLabel = Lang.Get("prefs-version-remove-confirm");
        RemoveConsequence = packs.Count == 0
            ? Lang.Get("games-remove-unused", chosen.Version)
            : Lang.Plural("games-remove-used", packs.Count, Listed(packs), chosen.Version);

        ConfirmingRemove = true;
    }

    [RelayCommand]
    private void CancelRemove() => ConfirmingRemove = false;

    /// <summary>Names them rather than counting them: which packs is the actual question.</summary>
    private static string Listed(IReadOnlyList<string> names) => names.Count switch
    {
        1 => Lang.Get("names-single", names[0]),
        2 => Lang.Get("names-pair", names[0], names[1]),
        _ => Lang.Get("names-overflow", names[0], names[1], names.Count - 2),
    };

    [RelayCommand]
    private void RemoveSelected()
    {
        ConfirmingRemove = false;

        if (SelectedInstalled is not { CanRemove: true } chosen) return;

        try
        {
            // Forgotten, never deleted. It is not Cairn's directory, and the twenty minutes
            // in it are not Cairn's to spend again.
            if (chosen.IsExternal)
            {
                _store.External.Forget(chosen.Directory);
                _log($"forgot client at {chosen.Directory}");

                RefreshInstalled();
                _onLibraryChanged();
                return;
            }

            _store.Remove(chosen.Install);
            _log($"removed game {chosen.Version}");

            RefreshInstalled();
            _onLibraryChanged();
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
    }

    // An install Cairn merely found on the machine is not Cairn's to delete.
    private bool CanRemove => !IsBusy && SelectedInstalled is { CanRemove: true };
}
