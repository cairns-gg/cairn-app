using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Cairn.Core;
using Cairn.Core.Games;
using Cairn.Core.Launch;
using Cairn.Core.Runtime;
using Cairn.Core.ModDb;
using Cairn.Core.Packs;

namespace Cairn.App.ViewModels;

/// <summary>A ModDB search hit, offered for adding to the pack.</summary>
public partial class SearchHitViewModel(
    ModSearchResult result,
    string versionRange,
    bool alreadyInPack = false,
    Action<SearchHitViewModel>? add = null,
    Action<SearchHitViewModel>? openPage = null) : ViewModelBase
{
    private static ModDbSearchEntry Entry(ModSearchResult r) => r.Mod;
    public string ModId { get; } = Entry(result).ModIdStrs.FirstOrDefault() ?? "";
    public string Name { get; } = Entry(result).Name;
    public string Summary { get; } = Entry(result).Summary ?? "";
    public string Side { get; } = Entry(result).Side ?? "";
    public string Downloads { get; } = $"{Entry(result).Downloads:N0} downloads";
    public string Author { get; } = string.IsNullOrWhiteSpace(Entry(result).Author) ? "" : $"by {Entry(result).Author}";
    public string Tags { get; } = string.Join(" · ", Entry(result).Tags);

    /// <summary>Where the icon lives on the CDN; null for the roughly one mod in ten with none.</summary>
    public string? LogoUrl { get; } = Entry(result).Logo;

    /// <summary>The mod's own page, for reading the description, screenshots and comments.</summary>
    public string? PageUrl { get; } = ModDbUrls.Page(Entry(result));

    public bool HasPage => PageUrl is not null;

    /// <summary>
    /// Filled in after the row appears, so a search renders immediately and the icons
    /// arrive as they are fetched rather than holding up the whole list.
    /// </summary>
    [ObservableProperty] public partial Bitmap? Icon { get; set; }

    partial void OnIconChanged(Bitmap? value) => OnPropertyChanged(nameof(HasIcon));

    public bool HasIcon => Icon is not null;

    /// <summary>
    /// Whether this mod has a release the pack's game version can use. Shown rather than
    /// hidden — knowing a mod exists but has not been updated yet is worth more than a
    /// shorter list, and it explains why it cannot be added.
    /// </summary>
    public bool Compatible { get; } = result.Compatible;

    public bool Incompatible => !Compatible;

    /// <summary>Says which version it is missing, not just that something is wrong.</summary>
    public string NoReleaseNote { get; } = $"no {versionRange} release";

    /// <summary>
    /// Already part of this pack. Shown on the row so a search never offers to add
    /// something twice, and so it is obvious what you already have.
    /// </summary>
    [ObservableProperty] public partial bool AlreadyInPack { get; set; } = alreadyInPack;

    partial void OnAlreadyInPackChanged(bool value)
    {
        OnPropertyChanged(nameof(CanAdd));
        AddCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Addable only if ModDB gave it a string id — some entries have none — it has a
    /// release that would actually install, and the pack does not already have it.
    /// </summary>
    public bool CanAdd => !string.IsNullOrWhiteSpace(ModId) && Compatible && !AlreadyInPack;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add() => add?.Invoke(this);

    [RelayCommand]
    private void OpenPage() => openPage?.Invoke(this);
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
    private readonly ModIconCache _icons;
    private readonly PackData _packData;
    private readonly ModInfoCache _modInfo;

    /// <summary>Bumped per pack reload, so icons for rows that are gone are dropped.</summary>
    private int _modIconGeneration;

    /// <summary>Bumped per search, so icons still arriving for an old one are dropped.</summary>
    private int _searchGeneration;

    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>>? _knownGameVersions;

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
        Action<object?> requestDelete,
        Func<CancellationToken, Task<IReadOnlyList<string>>>? knownGameVersions = null)
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
        _knownGameVersions = knownGameVersions;
        _icons = new ModIconCache(http);
        _modInfo = new ModInfoCache(moddb);
        _packData = new PackData(store);

        EditName = manifest.Name ?? manifest.Id;
        EditConnect = manifest.Connect ?? "";
        GameVersionChoices.Add(manifest.GameVersion);
        TargetGameVersion = manifest.GameVersion;

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

    // ---- the game's own logs ----

    private GameLogs GameLogs => new(_packData.DataPathFor(Id));

    public bool HasGameLogs => GameLogs.Exists;

    /// <summary>
    /// Pulls the game's own log into this pane. Cairn's log says what Cairn did, which is
    /// no help at all when the game closes on startup or a mod silently fails to load —
    /// that answer is in client-main.log, and nobody should have to know that.
    /// </summary>
    [RelayCommand]
    private void ShowGameLog()
    {
        var logs = GameLogs;

        if (!logs.Exists)
        {
            _log("no game logs yet — this pack has not been launched");
            return;
        }

        var tail = logs.Tail(GameLogs.ClientMain, lines: 200);
        if (tail.Count == 0)
        {
            _log($"no {GameLogs.ClientMain} under {logs.Directory}");
            return;
        }

        _log($"── {GameLogs.ClientMain} (last {tail.Count} lines) ──");
        foreach (var line in tail) _log(line);
        _log("── end of game log ──");
    }

    /// <summary>The errors and warnings only, which is what a failed launch is asked about.</summary>
    private void ShowGameProblems(string why)
    {
        var problems = GameLogs.Problems();
        if (problems.Count == 0) return;

        _log($"── {why}: what the game logged ──");
        foreach (var line in problems) _log(line);
        _log("── use Game log for the full file ──");
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        var logs = GameLogs;

        if (!Files.OpenFolder(logs.Directory))
            _log($"could not open {logs.Directory}");
    }

    public ObservableCollection<ModRowViewModel> Mods { get; } = [];

    public ObservableCollection<SearchHitViewModel> SearchHits { get; } = [];

    public ObservableCollection<string> ReleaseChoices { get; } = [];

    [ObservableProperty] public partial string EditName { get; set; }
    [ObservableProperty] public partial string EditConnect { get; set; }

    [ObservableProperty] public partial string SearchText { get; set; } = "";

    /// <summary>
    /// True once a search has run, until it is cleared. One list serves both purposes:
    /// the pack you are building, and the results you are building it from. Separate tabs
    /// made them look like peers and hid each from the other.
    /// </summary>
    [ObservableProperty] public partial bool ShowingSearch { get; set; }

    partial void OnShowingSearchChanged(bool value) => OnPropertyChanged(nameof(ListHeading));

    /// <summary>Says which of the two lists is on screen, and how big it is.</summary>
    public string ListHeading => ShowingSearch
        ? $"{SearchHits.Count} result{(SearchHits.Count == 1 ? "" : "s")} for “{_searchedFor}”"
        : $"{Mods.Count} mod{(Mods.Count == 1 ? "" : "s")} in this pack";

    private string _searchedFor = "";

    /// <summary>
    /// Puts a set of results on screen, as a completed search does. One entry point, so
    /// what a search leaves behind and what a test sets up cannot drift apart.
    /// </summary>
    public void ShowResults(string query, IEnumerable<SearchHitViewModel> hits)
    {
        _searchedFor = query;

        SearchHits.Clear();
        foreach (var h in hits) SearchHits.Add(h);

        ShowingSearch = true;
        OnPropertyChanged(nameof(ListHeading));
    }

    /// <summary>Puts the pack back, without discarding what was typed.</summary>
    [RelayCommand]
    private void ClearSearch()
    {
        SearchHits.Clear();
        ShowingSearch = false;
        SearchText = "";
        Error = null;
    }

    /// <summary>
    /// The versions a mod may be marked for and still install here, e.g. "1.22.x" — the
    /// whole minor, since that is what Cairn accepts when resolving a release.
    /// </summary>
    public string CompatibleVersionRange
    {
        get
        {
            var parts = Manifest.GameVersion.Split('.');
            return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}.x" : Manifest.GameVersion;
        }
    }

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
        CheckUpdatesCommand.NotifyCanExecuteChanged();
        UpdateAllCommand.NotifyCanExecuteChanged();
        SearchCommand.NotifyCanExecuteChanged();
        SaveSettingsCommand.NotifyCanExecuteChanged();
        DeletePackCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Rebuilds the pack's rows from the manifest and lockfile.
    ///
    /// The rows carry their own actions, so each is handed the callbacks it needs rather
    /// than the pane reaching back for "the selected one".
    /// </summary>
    private void ReloadMods()
    {
        var locks = _store.LoadLock(Id);
        Mods.Clear();

        foreach (var mod in Manifest.Mods)
        {
            var locked = locks?.Mods.FirstOrDefault(
                m => string.Equals(m.ModId, mod.ModId, StringComparison.OrdinalIgnoreCase));

            Mods.Add(new ModRowViewModel(
                mod, locked,
                loadReleases: LoadReleasesForRowAsync,
                pin: ApplyPin,
                remove: RemoveRow,
                openPage: OpenModPage,
                armed: DisarmOtherRows,
                update: UpdateOne));
        }

        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(ListHeading));

        // Not awaited: the pack list must draw immediately, with names and icons
        // following as ModDB answers.
        _ = LoadModDetailsAsync([.. Mods], ++_modIconGeneration);
    }

    /// <summary>
    /// Fills in each pack row's name and icon. Two layers of cache make this quiet after
    /// the first time: the mod's details, and the image itself.
    /// </summary>
    private async Task LoadModDetailsAsync(IReadOnlyList<ModRowViewModel> rows, int generation)
    {
        using var slots = new SemaphoreSlim(4);

        await Task.WhenAll(rows.Select(async row =>
        {
            await slots.WaitAsync().ConfigureAwait(false);
            try
            {
                if (generation != _modIconGeneration) return;

                var info = await _modInfo.GetAsync(row.ModId).ConfigureAwait(false);
                if (info is null || generation != _modIconGeneration) return;

                // The name is worth showing on its own, even for a mod with no icon.
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (generation == _modIconGeneration) row.Name = info.Name;
                });

                var path = await _icons.GetAsync(info.Logo).ConfigureAwait(false);
                if (path is null || generation != _modIconGeneration) return;

                await using var file = File.OpenRead(path);
                var bitmap = Bitmap.DecodeToWidth(file, 96);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (generation == _modIconGeneration) row.Icon = bitmap;
                });
            }
            // Deliberately everything: this is decoration, running on a background thread,
            // landing after the row may already be gone. Nothing it can hit is worth
            // surfacing, let alone failing over.
            catch (Exception)
            {
                // Not an image, or unreadable. The row is fine without one.
            }
            finally
            {
                slots.Release();
            }
        })).ConfigureAwait(false);
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

        // The game version deliberately does not come from here any more: changing it
        // re-resolves every mod, so it goes through Check → Apply instead.
        Manifest.Connect = string.IsNullOrWhiteSpace(EditConnect) ? null : EditConnect.Trim();

        _releaseCache.Clear();
        Persist();

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(HasServer));
        OnPropertyChanged(nameof(ServerLine));
        RefreshGameState();
        _log($"saved settings for '{Id}'");
    }

    // ---- changing the game version ----

    /// <summary>
    /// Versions offerable as a target: what ModDB's publisher lists, plus whatever the pack
    /// already targets so a pack pointed at something unpublished still shows its own value.
    /// </summary>
    public ObservableCollection<string> GameVersionChoices { get; } = [];

    [ObservableProperty] public partial string? TargetGameVersion { get; set; }
    [ObservableProperty] public partial bool IsCheckingVersion { get; set; }
    [ObservableProperty] public partial string CheckingMod { get; set; } = "";

    /// <summary>
    /// The last completed check. Nothing has been written while it is set, which is the
    /// entire purpose of the step.
    /// </summary>
    [ObservableProperty] public partial VersionChangeViewModel? VersionChange { get; set; }

    /// <summary>
    /// Shows the check and returns whether to go ahead. Supplied by the view; when absent
    /// — headless tests — the result simply stays on VersionChange for Apply or Cancel.
    /// </summary>
    public Func<VersionChangeViewModel, Task<bool>>? ConfirmVersionChange { get; set; }

    public bool CanCheckVersion =>
        !IsCheckingVersion
        && !string.IsNullOrWhiteSpace(TargetGameVersion)
        && !string.Equals(TargetGameVersion, Manifest.GameVersion, StringComparison.OrdinalIgnoreCase);

    partial void OnTargetGameVersionChanged(string? value)
    {
        // A different target invalidates the answer on screen, which was about the old one.
        VersionChange = null;
        OnPropertyChanged(nameof(CanCheckVersion));
        CheckVersionCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsCheckingVersionChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCheckVersion));
        CheckVersionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Fills the picker. Cheap and idempotent; called when the pane is shown.</summary>
    public async Task LoadGameVersionsAsync(CancellationToken ct = default)
    {
        if (_knownGameVersions is null) return;

        IReadOnlyList<string> versions;
        try
        {
            versions = await _knownGameVersions(ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException
                                       or System.Text.Json.JsonException)
        {
            return;   // the pack's own version is already in the list; offline still works
        }

        var chosen = TargetGameVersion;

        foreach (var v in versions.Where(v => !GameVersionChoices.Contains(v)))
            GameVersionChoices.Add(v);

        // Adding to the bound collection can clear the selection out from under us.
        TargetGameVersion = chosen;
    }

    [RelayCommand(CanExecute = nameof(CanCheckVersion))]
    private async Task CheckVersion(CancellationToken ct)
    {
        var target = TargetGameVersion!.Trim();

        IsCheckingVersion = true;
        VersionChange = null;
        Error = null;

        try
        {
            var plan = await GameVersionChange.PreviewAsync(
                _moddb, Manifest, _store.LoadLock(Id), target,
                worlds: _packData.Worlds(Id),
                progress: new Progress<string>(m => CheckingMod = m),
                ct: ct);

            var change = new VersionChangeViewModel(plan);
            VersionChange = change;
            _log($"checked {Manifest.GameVersion} -> {target}: {plan.Summary()}");

            if (ConfirmVersionChange is not null)
            {
                if (await ConfirmVersionChange(change)) ApplyVersionChange();
                else CancelVersionChange();
            }
        }
        catch (OperationCanceledException)
        {
            // Leaving the pane mid-check is not an error.
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
        finally
        {
            IsCheckingVersion = false;
            CheckingMod = "";
        }
    }

    /// <summary>
    /// Writes the new target. Deliberately does not download: Play is the one place that
    /// fetches a game version and syncs mods, and having two would mean two things to keep
    /// in step. The mods on disk stay as they are until then.
    /// </summary>
    [RelayCommand]
    public void ApplyVersionChange()
    {
        if (VersionChange is null) return;

        var target = VersionChange.Plan.To;
        Manifest.GameVersion = target;

        _releaseCache.Clear();
        Persist();

        VersionChange = null;
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(CanCheckVersion));
        RefreshGameState();
        ReloadMods();
        _onChanged();

        _log($"pack now targets game {target}; press Play to install it and update mods");
    }

    [RelayCommand]
    public void CancelVersionChange() => VersionChange = null;

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
            var generation = ++_searchGeneration;
            _searchedFor = SearchText.Trim();

            var hits = await _moddb.SearchRankedAsync(_searchedFor, Manifest.GameVersion);

            ShowResults(_searchedFor, hits.Take(60).Select(h => new SearchHitViewModel(
                h,
                CompatibleVersionRange,
                // Marked up front, so a search never offers to add what you have.
                alreadyInPack: Manifest.Mods.Any(m =>
                    string.Equals(m.ModId, h.Mod.ModIdStrs.FirstOrDefault(),
                        StringComparison.OrdinalIgnoreCase)),
                add: AddHit,
                openPage: OpenHitPage)));

            // Deliberately not awaited: the results should appear at once, with icons
            // filling in as they arrive rather than the list waiting on sixty downloads.
            _ = LoadIconsAsync([.. SearchHits], generation);

            _log(hits.Count > SearchHits.Count
                ? $"{hits.Count} result(s) for '{_searchedFor}' — showing the closest {SearchHits.Count}"
                : $"{SearchHits.Count} result(s) for '{_searchedFor}'");
            if (SearchHits.Count == 0)
                Error = "No mods matched that search.";
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

    /// <summary>
    /// Fetches the icons for a set of results and hands each one to its row as it lands.
    ///
    /// Bounded rather than unbounded: sixty simultaneous requests to the ModDB CDN is
    /// impolite, and sequential would make the last icon appear a minute late. Anything
    /// that fails simply leaves that row without one.
    /// </summary>
    private async Task LoadIconsAsync(IReadOnlyList<SearchHitViewModel> hits, int generation)
    {
        using var slots = new SemaphoreSlim(4);

        await Task.WhenAll(hits.Select(async hit =>
        {
            if (hit.LogoUrl is null) return;

            await slots.WaitAsync().ConfigureAwait(false);
            try
            {
                // A newer search has replaced these rows; its icons are the ones wanted.
                if (generation != _searchGeneration) return;

                var path = await _icons.GetAsync(hit.LogoUrl).ConfigureAwait(false);
                if (path is null || generation != _searchGeneration) return;

                await using var file = File.OpenRead(path);

                // Decoded to roughly the size it is drawn at, off the UI thread. Sixty
                // full-resolution icons held at once is a lot of memory for decoration.
                var bitmap = Bitmap.DecodeToWidth(file, 96);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (generation == _searchGeneration) hit.Icon = bitmap;
                });
            }
            // Deliberately everything: this is decoration, running on a background thread,
            // landing after the row may already be gone. Nothing it can hit is worth
            // surfacing, let alone failing over.
            catch (Exception)
            {
                // Not an image, or unreadable. The row is fine without one.
            }
            finally
            {
                slots.Release();
            }
        })).ConfigureAwait(false);
    }

    // ---- row actions ----
    //
    // Invoked by the rows themselves rather than acting on a selection. A pane-level
    // "Add selected" meant picking a row, moving to a button, and hoping it still meant
    // what you thought.

    private void AddHit(SearchHitViewModel hit)
    {
        if (Manifest.Mods.Any(m => string.Equals(m.ModId, hit.ModId, StringComparison.OrdinalIgnoreCase)))
        {
            hit.AlreadyInPack = true;
            return;
        }

        Manifest.Mods.Add(new PackMod { ModId = hit.ModId });
        Persist();

        // The row stays on screen, so it has to stop offering to add it again.
        hit.AlreadyInPack = true;
        _log($"added {hit.ModId}");
    }

    // ---- updates ----

    /// <summary>Set while a check is running, so the button can say so.</summary>
    [ObservableProperty] public partial bool CheckingUpdates { get; set; }

    [ObservableProperty] public partial string? UpdateSummary { get; set; }

    public bool HasUpdateSummary => !string.IsNullOrWhiteSpace(UpdateSummary);

    partial void OnUpdateSummaryChanged(string? value) => OnPropertyChanged(nameof(HasUpdateSummary));

    public bool AnyUpdates => Mods.Any(m => m.HasUpdate);

    /// <summary>
    /// Asks ModDB what each followed mod would move to. Reports only — nothing is
    /// installed until it is asked for, one mod or all.
    /// </summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task CheckUpdates()
    {
        CheckingUpdates = true;
        Error = null;

        try
        {
            var syncer = new PackSyncer(_moddb, _http);
            var updates = await syncer.CheckUpdatesAsync(Manifest, _store.LockPath(Id));

            foreach (var row in Mods)
                row.UpdateAvailable = updates
                    .FirstOrDefault(u => string.Equals(u.ModId, row.ModId, StringComparison.OrdinalIgnoreCase))
                    ?.To;

            UpdateSummary = updates.Count == 0
                ? "Everything is up to date."
                : $"{updates.Count} update{(updates.Count == 1 ? "" : "s")} available.";

            foreach (var u in updates) _log($"update available: {u.Describe()}");
            OnPropertyChanged(nameof(AnyUpdates));
            UpdateAllCommand.NotifyCanExecuteChanged();
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
        finally
        {
            CheckingUpdates = false;
        }
    }

    private void UpdateOne(ModRowViewModel row) => _ = ApplyUpdatesAsync([row.ModId]);

    [RelayCommand(CanExecute = nameof(CanUpdateAll))]
    private async Task UpdateAll() =>
        await ApplyUpdatesAsync([.. Mods.Where(m => m.HasUpdate).Select(m => m.ModId)]);

    private bool CanUpdateAll => !IsBusy && AnyUpdates;

    /// <summary>Installs the named mods' newest releases and records them in the lock.</summary>
    private async Task ApplyUpdatesAsync(IReadOnlyCollection<string> modIds)
    {
        if (modIds.Count == 0) return;

        IsBusy = true;
        Error = null;

        try
        {
            var syncer = new PackSyncer(_moddb, _http);
            var progress = new Progress<SyncStep>(s => _log(Format(s)));

            var report = await syncer.SyncAsync(
                Manifest, _store.ModsDir(Id), _store.LockPath(Id), progress,
                allowUpdates: new HashSet<string>(modIds, StringComparer.OrdinalIgnoreCase));

            if (report.Failed) Error = "Some mods could not be updated — see the log.";
            UpdateSummary = null;
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
        finally
        {
            IsBusy = false;
            ReloadMods();
            OnPropertyChanged(nameof(AnyUpdates));
        }
    }

    /// <summary>Only one row may be asking at a time, so a stray Enter cannot hit two.</summary>
    private void DisarmOtherRows(ModRowViewModel armed)
    {
        foreach (var row in Mods.Where(r => !ReferenceEquals(r, armed)))
            row.ConfirmingRemove = false;
    }

    private void RemoveRow(ModRowViewModel row)
    {
        Manifest.Mods.RemoveAll(m => string.Equals(m.ModId, row.ModId, StringComparison.OrdinalIgnoreCase));
        Persist();
        _log($"removed {row.ModId} (its zip goes away on the next sync)");

        // If it is also on screen as a search result, offer it again.
        foreach (var hit in SearchHits.Where(
                     h => string.Equals(h.ModId, row.ModId, StringComparison.OrdinalIgnoreCase)))
            hit.AlreadyInPack = false;
    }

    private void OpenHitPage(SearchHitViewModel hit)
    {
        if (!Browser.Open(hit.PageUrl))
            Error = "Could not open a browser for that link.";
    }

    /// <summary>
    /// Opens the page of a mod already in the pack. The manifest holds only the mod id,
    /// and a page is addressed by asset id, so this costs one lookup — done on click
    /// rather than for every row up front.
    /// </summary>
    private async void OpenModPage(ModRowViewModel row)
    {
        // Usually already known, since drawing the row's icon asked the same question.
        var info = await _modInfo.GetAsync(row.ModId);

        if (info is null)
        {
            Error = $"Could not find {row.ModId} on ModDB.";
            return;
        }

        if (!Browser.Open(ModDbUrls.Page(info.AssetId, info.UrlAlias)))
            Error = $"Could not open the ModDB page for {row.ModId}.";
    }

    /// <summary>
    /// Compatible versions per mod, so opening a dropdown twice — including after the
    /// reload that follows pinning — costs nothing and cannot loop back into the network.
    /// </summary>
    private readonly Dictionary<string, List<string>> _releaseCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The choice that means "not pinned". It no longer means "silently move to whatever
    /// is newest on every launch" — following a mod means it is offered for update when
    /// you check, not that it changes underneath you.
    /// </summary>
    public const string TrackNewest = "latest";

    /// <summary>
    /// Builds a result row wired to this pack, exactly as a search does. Exists so tests
    /// exercise the real wiring rather than a hand-assembled imitation of it.
    /// </summary>
    public SearchHitViewModel MakeHitForTest(string modId, string name) =>
        new(new ModSearchResult(
                new ModDbSearchEntry { Name = name, ModIdStrs = [modId], Side = "client" }, true),
            CompatibleVersionRange,
            alreadyInPack: Manifest.Mods.Any(m =>
                string.Equals(m.ModId, modId, StringComparison.OrdinalIgnoreCase)),
            add: AddHit,
            openPage: OpenHitPage);

    /// <summary>Pre-populates the cache, e.g. from a test or a warm-up.</summary>
    public void CacheReleaseChoices(string modId, IEnumerable<string> versions) =>
        _releaseCache[CacheKey(modId)] = [TrackNewest, .. versions];

    private string CacheKey(string modId) => $"{modId}|{Manifest.GameVersion}";

    /// <summary>
    /// Fills one row's version dropdown. Called when that dropdown is opened rather than
    /// when the pack is shown, so a twenty-mod pack does not make twenty ModDB calls to
    /// answer a question nobody asked.
    /// </summary>
    private async Task LoadReleasesForRowAsync(ModRowViewModel row)
    {
        if (_releaseCache.TryGetValue(CacheKey(row.ModId), out var cached))
        {
            row.ShowChoices(cached);
            return;
        }

        row.LoadingReleases = true;

        try
        {
            var releases = await _moddb.ListCompatibleReleasesAsync(row.ModId, Manifest.GameVersion);

            var choices = new List<string> { TrackNewest };
            choices.AddRange(releases.Select(r => r.ModVersion));

            _releaseCache[CacheKey(row.ModId)] = choices;
            row.ShowChoices(choices);

            if (releases.Count == 0)
                Error = $"No release of {row.ModId} is marked for game {Manifest.GameVersion}.";
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
        finally
        {
            row.LoadingReleases = false;
        }
    }

    /// <summary>Applies the pin as soon as a version is chosen — no separate button.</summary>
    private void ApplyPin(ModRowViewModel row, string? version)
    {
        var modId = row.ModId;
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
            _packData.BeforeLaunch(Id);

            var options = new LaunchOptions
            {
                DataPath = _packData.DataPathFor(Id),
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
    /// <summary>
    /// Where the pack's worlds, mod configs and settings live. Always its own directory;
    /// it appears on first launch.
    /// </summary>
    public string DataDirectory => _packData.DataPathFor(Id);

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

        // A login made inside the pack, or a session the game rotated while playing,
        // becomes the one every other pack uses next.
        _packData.AfterExit(Id);

        Dispatcher.UIThread.Post(() =>
        {
            _gameRunning = false;
            IsLaunching = false;
            LaunchStage = "";

            if (code is { } c && c != 0)
            {
                Error = $"Vintage Story exited with code {c}. See the Log tab.";
                _log($"Vintage Story exited with code {c}");

                // The moment the game's log matters, so it is put in front of you rather
                // than left somewhere you would have to know to look.
                ShowGameProblems($"exit code {c}");
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
