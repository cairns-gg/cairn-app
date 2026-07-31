using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Cairn.Core;
using Cairn.Core.Games;
using Cairn.Core.Launch;
using Cairn.Core.Runtime;
using Cairn.Core.ModDb;
using Cairn.Core.Packs;

namespace Cairn.App.ViewModels;

/// <summary>A ModDB search hit, offered for adding to the pack.</summary>
public class SearchHitViewModel(ModDbSearchEntry entry) : ViewModelBase
{
    public string ModId { get; } = entry.ModIdStrs.FirstOrDefault() ?? "";
    public string Name { get; } = entry.Name;
    public string Summary { get; } = entry.Summary ?? "";
    public string Side { get; } = entry.Side ?? "";
    public string Downloads { get; } = $"{entry.Downloads:N0} downloads";

    /// <summary>ModDB occasionally returns entries with no string id; those cannot be added.</summary>
    public bool CanAdd => !string.IsNullOrWhiteSpace(ModId);
}

/// <summary>
/// Everything you can do to one pack. Held by MainViewModel and rebuilt when the
/// selection changes.
/// </summary>
public partial class PackDetailViewModel : ViewModelBase
{
    private readonly PackStore _store;
    private readonly ModDbClient _moddb;
    private readonly HttpClient _http;
    private readonly GameLibrary _library;
    private readonly RuntimeStore _runtimes;
    private readonly Action<string> _log;
    private readonly Action _onChanged;
    private readonly Func<string, Task> _provision;
    private readonly Action<object?> _requestDelete;
    private readonly Func<string, bool> _isProvisioning;

    public PackDetailViewModel(
        PackManifest manifest,
        PackStore store,
        ModDbClient moddb,
        HttpClient http,
        GameLibrary library,
        RuntimeStore runtimes,
        ObservableCollection<string> log,
        Action<string> note,
        Action onChanged,
        Func<string, Task> provision,
        Func<string, bool> isProvisioning,
        Action<object?> requestDelete)
    {
        Manifest = manifest;
        _store = store;
        _moddb = moddb;
        _http = http;
        _library = library;
        _runtimes = runtimes;
        Log = log;
        _log = note;
        _onChanged = onChanged;
        _provision = provision;
        _requestDelete = requestDelete;
        _isProvisioning = isProvisioning;

        EditName = manifest.Name ?? manifest.Id;
        EditGameVersion = manifest.GameVersion;
        EditConnect = manifest.Connect ?? "";

        ReloadMods();
    }

    public PackManifest Manifest { get; }

    public string Id => Manifest.Id;

    /// <summary>
    /// This pack's log. Owned by MainViewModel so it survives being deselected — this
    /// view model is rebuilt every time the selection changes.
    /// </summary>
    public ObservableCollection<string> Log { get; }

    [RelayCommand]
    private void ClearLog() => Log.Clear();

    public ObservableCollection<ModRowViewModel> Mods { get; } = [];

    public ObservableCollection<SearchHitViewModel> SearchHits { get; } = [];

    public ObservableCollection<string> ReleaseChoices { get; } = [];

    [ObservableProperty] public partial string EditName { get; set; }
    [ObservableProperty] public partial string EditGameVersion { get; set; }
    [ObservableProperty] public partial string EditConnect { get; set; }

    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial SearchHitViewModel? SelectedHit { get; set; }
    [ObservableProperty] public partial ModRowViewModel? SelectedMod { get; set; }
    [ObservableProperty] public partial string? SelectedRelease { get; set; }
    [ObservableProperty] public partial bool LoadingReleases { get; set; }

    [ObservableProperty] public partial bool IsBusy { get; set; }

    /// <summary>
    /// Set from the moment Play is pressed until the game exits. Syncing and process
    /// start take several seconds, and without this the window looked inert — and Play
    /// could be pressed again, starting a second copy.
    /// </summary>
    [ObservableProperty] public partial bool IsLaunching { get; set; }

    [ObservableProperty] public partial string LaunchStage { get; set; } = "";
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial string? ExportedPath { get; set; }
    [ObservableProperty] public partial string ExportedJson { get; set; } = "";
    [ObservableProperty] public partial bool ExportIncludesLock { get; set; } = true;

    public bool HasError => !string.IsNullOrEmpty(Error);

    public bool IsShowingLaunchStage => !string.IsNullOrEmpty(LaunchStage);

    public string PlayLabel => IsLaunching ? "Working…" : "Play";

    partial void OnLaunchStageChanged(string value) => OnPropertyChanged(nameof(IsShowingLaunchStage));

    partial void OnIsLaunchingChanged(bool value)
    {
        OnPropertyChanged(nameof(PlayLabel));
        OnPropertyChanged(nameof(CanLaunch));
        PlayCommand.NotifyCanExecuteChanged();
    }

    public bool HasExported => !string.IsNullOrEmpty(ExportedPath);

    partial void OnExportedPathChanged(string? value) => OnPropertyChanged(nameof(HasExported));

    public string Title => Manifest.Name ?? Manifest.Id;

    public string Subtitle =>
        $"game {Manifest.GameVersion}  ·  {Manifest.Mods.Count} mod{(Manifest.Mods.Count == 1 ? "" : "s")}";

    /// <summary>See PackListItemViewModel.HasServer — blank is "opens at the main menu",
    /// not "singleplayer only".</summary>
    public bool HasServer => !string.IsNullOrWhiteSpace(Manifest.Connect);

    public string ServerLine => HasServer ? $"auto-joins {Manifest.Connect}" : "";

    public string ModsDirectory => _store.ModsDir(Id);

    /// <summary>The install this pack will actually launch, or null when its version is absent.</summary>
    public GameInstall? ResolvedInstall => _library.ForVersion(Manifest.GameVersion);

    /// <summary>
    /// True while this pack's game version is being downloaded. The provisioning pane
    /// hides this one anyway; this keeps the view model's own answers consistent.
    /// </summary>
    public bool IsProvisioning => _isProvisioning(Manifest.GameVersion);

    // Play is available even when the game is missing: it provisions, then launches.
    // Gating it taught the user to go and solve the problem themselves. It is not
    // available while a launch is already in flight.
    public bool CanLaunch => !IsBusy && !IsLaunching;

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanLaunch));
        PlayCommand.NotifyCanExecuteChanged();
        SearchCommand.NotifyCanExecuteChanged();
        AddSelectedCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        SaveSettingsCommand.NotifyCanExecuteChanged();
        DeletePackCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedModChanged(ModRowViewModel? value)
    {
        RemoveSelectedCommand.NotifyCanExecuteChanged();

        if (value is null)
        {
            _settingReleaseProgrammatically = true;
            ReleaseChoices.Clear();
            SelectedRelease = null;
            _settingReleaseProgrammatically = false;
            return;
        }

        // Loading versions used to need a separate "Versions…" click.
        _ = LoadReleasesForAsync(value.ModId, value.Mod.Version);
    }

    partial void OnSelectedHitChanged(SearchHitViewModel? value)
        => AddSelectedCommand.NotifyCanExecuteChanged();

    partial void OnSelectedReleaseChanged(string? value)
    {
        if (_settingReleaseProgrammatically || value is null) return;
        if (SelectedMod is not { } row) return;

        ApplyPin(row.ModId, value == TrackNewest ? null : value);
    }

    private void ReloadMods()
    {
        // Clearing the collection drops the ListBox selection, so remember it and put it
        // back. Callers must not read SelectedMod across a reload.
        var previous = SelectedMod?.ModId;

        var locks = _store.LoadLock(Id);
        Mods.Clear();

        foreach (var mod in Manifest.Mods)
        {
            var locked = locks?.Mods.FirstOrDefault(
                m => string.Equals(m.ModId, mod.ModId, StringComparison.OrdinalIgnoreCase));
            Mods.Add(new ModRowViewModel(mod, locked));
        }

        if (previous is not null)
            SelectedMod = Mods.FirstOrDefault(
                m => string.Equals(m.ModId, previous, StringComparison.OrdinalIgnoreCase));

        OnPropertyChanged(nameof(Subtitle));
    }

    private void Persist()
    {
        try
        {
            _store.Save(Manifest);
            Error = null;
        }
        catch (Exception e)
        {
            Error = e.Message;
        }

        ReloadMods();

        // Every manifest edit goes through here, so this is the one place the sidebar
        // needs telling that the row it is showing has changed underneath it.
        _onChanged();
    }

    // ---- settings ----

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private void SaveSettings()
    {
        Manifest.Name = string.IsNullOrWhiteSpace(EditName) ? Id : EditName.Trim();
        Manifest.GameVersion = EditGameVersion.Trim();
        Manifest.Connect = string.IsNullOrWhiteSpace(EditConnect) ? null : EditConnect.Trim();

        _releaseCache.Clear();
        Persist();

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(HasServer));
        OnPropertyChanged(nameof(ServerLine));
        RefreshGameState();
        _log($"saved settings for '{Id}'");
    }

    public bool NotBusy => !IsBusy;

    /// <summary>Re-evaluates which install serves this pack, after a version edit or a new install.</summary>
    public void RefreshGameState()
    {
        OnPropertyChanged(nameof(ResolvedInstall));
        OnPropertyChanged(nameof(IsProvisioning));
        OnPropertyChanged(nameof(CanLaunch));
        PlayCommand.NotifyCanExecuteChanged();
    }

    // ---- mods ----

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task Search()
    {
        IsBusy = true;
        SearchHits.Clear();

        try
        {
            var hits = await _moddb.SearchRankedAsync(SearchText.Trim());
            foreach (var h in hits.Take(60)) SearchHits.Add(new SearchHitViewModel(h));

            _log(hits.Count > SearchHits.Count
                ? $"{hits.Count} result(s) for '{SearchText.Trim()}' — showing the closest {SearchHits.Count}"
                : $"{SearchHits.Count} result(s) for '{SearchText.Trim()}'");
            if (SearchHits.Count == 0) Error = "No mods matched that search.";
            else Error = null;
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSearch => !IsBusy && !string.IsNullOrWhiteSpace(SearchText);

    partial void OnSearchTextChanged(string value) => SearchCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanAddSelected))]
    private void AddSelected()
    {
        var hit = SelectedHit!;

        if (Manifest.Mods.Any(m => string.Equals(m.ModId, hit.ModId, StringComparison.OrdinalIgnoreCase)))
        {
            Error = $"'{hit.ModId}' is already in this pack.";
            return;
        }

        Manifest.Mods.Add(new PackMod { ModId = hit.ModId });
        Persist();
        _log($"added {hit.ModId}");
    }

    private bool CanAddSelected => !IsBusy && SelectedHit is { CanAdd: true };

    [RelayCommand(CanExecute = nameof(CanActOnMod))]
    private void RemoveSelected()
    {
        var row = SelectedMod!;
        Manifest.Mods.RemoveAll(m => string.Equals(m.ModId, row.ModId, StringComparison.OrdinalIgnoreCase));
        Persist();
        _log($"removed {row.ModId} (its zip goes away on the next sync)");
    }

    private bool CanActOnMod => !IsBusy && SelectedMod is not null;

    /// <summary>
    /// Compatible versions per mod, so re-selecting a mod — including the reload that
    /// follows pinning — costs nothing and cannot loop back into the network.
    /// </summary>
    private readonly Dictionary<string, List<string>> _releaseCache = new(StringComparer.OrdinalIgnoreCase);

    private int _releaseGeneration;
    private bool _settingReleaseProgrammatically;

    /// <summary>The choice that means "do not pin; take whatever is newest".</summary>
    public const string TrackNewest = "newest";

    /// <summary>Pre-populates the cache, e.g. from a test or a warm-up.</summary>
    public void CacheReleaseChoices(string modId, IEnumerable<string> versions) =>
        _releaseCache[CacheKey(modId)] = [TrackNewest, .. versions];

    private string CacheKey(string modId) => $"{modId}|{Manifest.GameVersion}";

    private void ShowReleaseChoices(IReadOnlyList<string> choices, string? pinned)
    {
        // Populate before selecting: a ComboBox bound to an empty collection discards a
        // selection it cannot match, and re-assigning the same value raises no change.
        _settingReleaseProgrammatically = true;

        ReleaseChoices.Clear();
        foreach (var c in choices) ReleaseChoices.Add(c);

        SelectedRelease = pinned is not null && ReleaseChoices.Contains(pinned)
            ? pinned
            : TrackNewest;

        _settingReleaseProgrammatically = false;
    }

    private async Task LoadReleasesForAsync(string modId, string? pinned)
    {
        if (_releaseCache.TryGetValue(CacheKey(modId), out var cached))
        {
            ShowReleaseChoices(cached, pinned);
            return;
        }

        var generation = ++_releaseGeneration;
        LoadingReleases = true;

        try
        {
            var releases = await _moddb.ListCompatibleReleasesAsync(modId, Manifest.GameVersion);

            // The selection may have moved on while this was in flight.
            if (generation != _releaseGeneration) return;

            var choices = new List<string> { TrackNewest };
            choices.AddRange(releases.Select(r => r.ModVersion));

            _releaseCache[CacheKey(modId)] = choices;
            ShowReleaseChoices(choices, pinned);

            if (releases.Count == 0)
                Error = $"No release of {modId} is marked for game {Manifest.GameVersion}.";
        }
        catch (Exception e)
        {
            if (generation == _releaseGeneration) Error = e.Message;
        }
        finally
        {
            if (generation == _releaseGeneration) LoadingReleases = false;
        }
    }

    /// <summary>Applies the pin as soon as a version is chosen — no separate button.</summary>
    private void ApplyPin(string modId, string? version)
    {
        var entry = Manifest.Mods.FirstOrDefault(m =>
            string.Equals(m.ModId, modId, StringComparison.OrdinalIgnoreCase));
        if (entry is null || entry.Version == version) return;

        entry.Version = version;
        Persist();

        _log(version is null
            ? $"unpinned {modId} — will track newest"
            : $"pinned {modId} to {version}");
    }

    // ---- sync / launch / delete ----

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task Play()
    {
        IsLaunching = true;
        LaunchStage = "Checking the game is ready…";

        try
        {
            await PlayCoreAsync();
        }
        finally
        {
            // Cleared here only if nothing was started; a running game clears it on exit.
            if (!_gameRunning) { IsLaunching = false; LaunchStage = ""; }
        }
    }

    private bool _gameRunning;

    private async Task PlayCoreAsync()
    {
        if (ResolvedInstall is null)
        {
            await _provision(Manifest.GameVersion);
            RefreshGameState();

            if (ResolvedInstall is null)
            {
                Error = $"Could not prepare Vintage Story {Manifest.GameVersion} — see the log.";
                return;
            }
        }

        LaunchStage = "Checking mods…";

        var report = await RunSyncAsync();
        if (report is null || report.Failed)
        {
            Error = "Not launching — sync did not complete cleanly.";
            return;
        }

        try
        {
            var install = ResolvedInstall!;
            var launcher = new GameLauncher(install);

            // Prefer a runtime Cairn manages: for an older game version it may be the
            // only .NET of the right major on the machine.
            var options = new LaunchOptions
            {
                DataPath = GameInstall.DefaultDataPath,
                ModPaths = { _store.ModsDir(Id) },
                Connect = Manifest.Connect,
                PreferredDotnetRoot = _runtimes.RootFor(install),
            };

            var runtime = launcher.ResolveRuntime(options);
            if (!runtime.Resolved)
            {
                await _provision(Manifest.GameVersion);

                options.PreferredDotnetRoot = _runtimes.RootFor(install);
                runtime = launcher.ResolveRuntime(options);

                if (!runtime.Resolved)
                {
                    Error = $"{install.Version} needs .NET {install.RequiredFramework}, "
                            + "which could not be installed — see the log.";
                    return;
                }
            }

            LaunchStage = "Starting Vintage Story…";
            _log($"launching: {string.Join(' ', launcher.BuildArguments(options))}");

            var proc = launcher.Launch(options);
            _log($"Vintage Story started (pid {proc.Id})");

            // The game takes a while to put a window up. Keep saying so, and keep Play
            // disabled, until it actually exits.
            _gameRunning = true;
            LaunchStage = $"Vintage Story is running (pid {proc.Id})";
            _ = WatchAsync(proc);
        }
        catch (Exception e)
        {
            Error = $"Launch failed: {e.Message}";
        }
    }

    /// <summary>
    /// Writes the pack as one shareable file. Including the lock is what makes the
    /// recipient reproduce this exact mod set rather than merely a similar one.
    /// </summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    private void Export()
    {
        try
        {
            ExportedJson = _store.Export(Id, ExportIncludesLock);

            Directory.CreateDirectory(CairnPaths.ExportsRoot);
            var path = Path.Combine(CairnPaths.ExportsRoot, $"{Id}.cairn.json");
            File.WriteAllText(path, ExportedJson);

            ExportedPath = path;
            Error = null;
            _log($"exported '{Id}' -> {path}");
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
    }

    /// <summary>Hands off to the shared confirmation rather than deleting outright.</summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    private void DeletePack() => _requestDelete(null);

    /// <summary>Re-enables Play when the game exits, and reports a non-zero exit.</summary>
    private async Task WatchAsync(System.Diagnostics.Process proc)
    {
        try
        {
            await proc.WaitForExitAsync();
        }
        catch (Exception e) when (e is InvalidOperationException or SystemException)
        {
            // Process already gone; fall through and re-enable.
        }

        var code = TryExitCode(proc);

        Dispatcher.UIThread.Post(() =>
        {
            _gameRunning = false;
            IsLaunching = false;
            LaunchStage = "";

            if (code is { } c && c != 0)
            {
                Error = $"Vintage Story exited with code {c}.";
                _log($"Vintage Story exited with code {c}");
            }
            else
            {
                _log("Vintage Story exited");
            }
        });
    }

    private static int? TryExitCode(System.Diagnostics.Process proc)
    {
        try { return proc.ExitCode; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Resolves the pack against ModDB, downloads what is missing, removes what is no
    /// longer wanted, and writes the lockfile.
    ///
    /// Play is its only caller now that the separate sync button is gone. It is not dead
    /// code — it is the first half of launching, and dropping it would leave Play
    /// starting the game with whatever happened to be on disk.
    /// </summary>
    private async Task<SyncReport?> RunSyncAsync()
    {
        IsBusy = true;
        Error = null;

        try
        {
            var syncer = new PackSyncer(_moddb, _http);
            var progress = new Progress<SyncStep>(s =>
            {
                _log(Format(s));
                if (IsLaunching) LaunchStage = $"Mods: {s.ModId} {s.Detail}";
            });

            var report = await syncer.SyncAsync(
                Manifest, _store.ModsDir(Id), _store.LockPath(Id), progress);

            if (report.Failed) Error = "Some mods could not be installed — see the log.";
            return report;
        }
        catch (Exception e)
        {
            Error = e.Message;
            return null;
        }
        finally
        {
            IsBusy = false;
            ReloadMods();
        }
    }

    private static string Format(SyncStep s)
    {
        var marker = s.Action switch
        {
            SyncAction.Downloaded => "added",
            SyncAction.Updated => "updated",
            SyncAction.Removed => "removed",
            SyncAction.Unchanged => "ok",
            SyncAction.Warned => "warning",
            _ => "failed",
        };

        return $"{marker,-8} {s.ModId,-24} {s.Detail}";
    }
}
