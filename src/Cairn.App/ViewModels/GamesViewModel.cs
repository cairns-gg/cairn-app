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

/// <summary>A game version Cairn has installed.</summary>
public class InstalledGameViewModel(GameInstall install, RuntimeResolution runtime) : ViewModelBase
{
    public GameInstall Install { get; } = install;

    public string Version => Install.Version;
    public string Directory => Install.Directory;
    public string Needs => $"needs .NET {Install.RequiredFramework}";
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
        ? IsInstalled ? "installed" : ""
        : "not downloadable by Cairn";

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

    public GamesViewModel(
        HttpClient http, GameStore store, RuntimeStore runtimes,
        Action<string> log, Action onLibraryChanged)
    {
        _http = http;
        _store = store;
        _runtimes = runtimes;
        _catalog = new GameCatalog(http);
        _log = log;
        _onLibraryChanged = onLibraryChanged;

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
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        InstallRuntimeCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedAvailableChanged(AvailableGameViewModel? value)
        => InstallSelectedCommand.NotifyCanExecuteChanged();

    partial void OnSelectedInstalledChanged(InstalledGameViewModel? value)
    {
        RemoveSelectedCommand.NotifyCanExecuteChanged();
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
        Progress = $"resolving .NET {major}…";

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
                    ? $"downloading .NET {release.Version} — {p.Done / 1024 / 1024} MB"
                    : p.Phase;
            });

            var installed = await installer.InstallAsync(release, progress);

            _log($"installed .NET {release.Version} -> {installed.Root}");
            Progress = $"installed .NET {release.Version}";

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

    public void RefreshInstalled()
    {
        Installed.Clear();

        foreach (var install in _store.ListInstalled())
        {
            // Resolve against a managed runtime too, otherwise a game we can already run
            // would be reported as unusable.
            var options = new LaunchOptions { PreferredDotnetRoot = _runtimes.RootFor(install) };
            var runtime = new GameLauncher(install).ResolveRuntime(options);
            Installed.Add(new InstalledGameViewModel(install, runtime));
        }

        ManagedRuntimes.Clear();
        foreach (var r in _runtimes.ListInstalled())
            ManagedRuntimes.Add($"{Path.GetFileName(r.Root)}  ({string.Join(", ", r.Frameworks)})");

        foreach (var a in Available) a.IsInstalled = _store.IsInstalled(a.Version);
    }

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
            _log($"catalog: {Available.Count} versions published for {GameCatalog.PlatformKey}");
        }
        catch (Exception e)
        {
            Error = $"Could not read the version catalog: {e.Message}";
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
        Progress = $"starting {chosen.Version}…";

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
                        $"downloading {chosen.Version} — {p.Done / 1024 / 1024} MB"
                        + (p.Fraction is { } f ? $" ({f * 100:F0}%)" : ""),
                    InstallPhase.Verifying => "verifying download",
                    // Carries "… — 412 MB written" once unpacking or the installer is
                    // under way, which is the only thing moving during the longest step.
                    InstallPhase.Extracting => p.Detail,
                    InstallPhase.Finishing => "arranging files",
                    _ => $"installed {p.Detail}",
                };
            });

            var install = await installer.InstallAsync(chosen.Release, progress);

            _log($"installed game {install.Version} -> {install.Directory}");
            Progress = $"installed {install.Version}";
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

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void RemoveSelected()
    {
        var chosen = SelectedInstalled!;

        try
        {
            _store.Remove(chosen.Version);
            _log($"removed game {chosen.Version}");

            RefreshInstalled();
            _onLibraryChanged();
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
    }

    private bool CanRemove => !IsBusy && SelectedInstalled is not null;
}
