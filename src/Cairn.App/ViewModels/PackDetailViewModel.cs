using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Cairn.Core;
using Cairn.Core.Games;
using Cairn.Core.Hotkeys;
using Cairn.Core.Games.Optimum;
using Cairn.Core.Launch;
using Cairn.Core.Runtime;
using Cairn.Core.ModDb;
using Cairn.Core.Packs;
using Cairn.Core.Cairns;

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
    public string Downloads { get; } = Lang.Get("mods-downloads", Entry(result).Downloads);
    public string Author { get; } = string.IsNullOrWhiteSpace(Entry(result).Author)
        ? ""
        : Lang.Get("mods-by", Entry(result).Author);
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

    /// <summary>
    /// What ModDB does mark this mod for, kept apart from the sentence that shows it.
    ///
    /// The confirmation that asks whether to add it anyway used to recover this by stripping
    /// "no " and " release" back off NoReleaseNote. That worked while the note was built here
    /// and read two hundred lines away, and it stopped working the moment the note became
    /// translatable — in any language whose sentence is not "no X release", the strip returns
    /// the whole sentence and the dialogue says the mod is marked for "keine X-Version".
    /// A displayed string is not a data structure.
    /// </summary>
    public string MarkedFor { get; } = versionRange;

    /// <summary>Says which version it is missing, not just that something is wrong.</summary>
    public string NoReleaseNote { get; } = Lang.Get("mods-no-release", versionRange);

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
    /// Addable only if ModDB gave it a string id — some entries have none — and the pack
    /// does not already have it.
    ///
    /// A mod with no release for this version is addable too, which it did not used to be.
    /// Refusing it was right while there was nothing to say about it, but a mod that has
    /// simply not been rebuilt often still runs, and the person who has tried it is the
    /// only one who can say so. What that costs is a question rather than a click: see
    /// <see cref="NeedsAcceptance"/>.
    /// </summary>
    public bool CanAdd => !string.IsNullOrWhiteSpace(ModId) && !AlreadyInPack;

    /// <summary>Adding this one is a decision, so it asks before writing anything.</summary>
    public bool NeedsAcceptance => Incompatible;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add() => add?.Invoke(this);

    [RelayCommand]
    private void OpenPage() => openPage?.Invoke(this);
}

/// <summary>
/// Everything you can do to one pack. Held by MainViewModel and rebuilt when the
/// selection changes.
/// </summary>
public partial class PackDetailViewModel : ViewModelBase, IDisposable
{
    private readonly PackStore _store;
    private readonly ModDbClient _moddb;
    private readonly HttpClient _http;
    private readonly GameLibrary _library;
    private readonly RuntimeStore _runtimes;
    private readonly Action<string> _log;
    private readonly Action _onChanged;
    private readonly Func<string, Task> _provision;
    private readonly Func<GameInstall, Task>? _provisionRuntime;
    private readonly Action<object?> _requestDelete;
    private readonly Func<string, bool> _isProvisioning;
    private readonly ModIconCache _icons;
    private readonly PackData _packData;
    private readonly ModInfoCache _modInfo;
    private readonly RunningGames _runs;

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
        RunningGames runs,
        ObservableCollection<string> log,
        Action<string> note,
        Action onChanged,
        Func<string, Task> provision,
        Func<string, bool> isProvisioning,
        Action<object?> requestDelete,
        Func<CancellationToken, Task<IReadOnlyList<string>>>? knownGameVersions = null,
        Func<GameInstall, Task>? provisionRuntime = null)
    {
        Manifest = manifest;
        _store = store;
        _moddb = moddb;
        _http = http;
        _library = library;
        _runtimes = runtimes;
        _runs = runs;
        Log = log;
        _log = note;
        _onChanged = onChanged;
        _provision = provision;
        _provisionRuntime = provisionRuntime;
        _requestDelete = requestDelete;
        _isProvisioning = isProvisioning;
        _knownGameVersions = knownGameVersions;
        _icons = new ModIconCache(http);
        _modInfo = new ModInfoCache(moddb);
        _packData = new PackData(store);

        EditName = manifest.Name ?? manifest.Id;
        EditDescription = manifest.Description ?? "";
        EditConnect = manifest.Connect ?? "";
        GameVersionChoices.Add(manifest.GameVersion);
        TargetGameVersion = manifest.GameVersion;

        ReloadMods();

        // A pane built for a pack whose game is already up adopts that launch rather than
        // starting out claiming there is nothing running.
        RefreshLaunchState();
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

    /// <summary>
    /// Puts text on the clipboard. Supplied by the window, because a view model has no
    /// TopLevel to ask; left null in tests, which read the report instead.
    /// </summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>
    /// Assembles what a bug report needs and puts it on the clipboard, so the person can
    /// read it before deciding to send it.
    ///
    /// Deliberately not an upload. Cairn holds a cairns.gg token and a Vintage Story
    /// session on disk, and the way to be sure neither is ever transmitted is to have no
    /// code that transmits anything — the report goes as far as the clipboard and stops.
    /// It lives beside the log because that is where somebody already is when the thing
    /// they want to report has just happened.
    /// </summary>
    [RelayCommand]
    private async Task CopyDiagnostics()
    {
        if (CopyToClipboard is null)
        {
            _log(Lang.Get("log-no-clipboard"));
            return;
        }

        // Off the UI thread: this hashes every mod zip in the pack, which for a large one
        // is tens of megabytes and would otherwise freeze the window mid-click.
        var report = await Task.Run(() => Diagnostics.Report(
            Manifest, _store.LoadLock(Id), Log.ToList(), _library, _store.ModsDir(Id),
            ResolvedInstall));

        try
        {
            await CopyToClipboard(report);
            _log(Lang.Get("log-diagnostics-copied"));
        }
        catch (Exception e)
        {
            // A clipboard that refuses is not worth failing over, but silence would leave
            // somebody pasting whatever was there before and wondering why it made no sense.
            _log(Lang.Get("log-diagnostics-failed", e.Message));
        }
    }

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
            _log(Lang.Get("log-no-game-logs"));
            return;
        }

        var tail = logs.Tail(GameLogs.ClientMain, lines: 200);
        if (tail.Count == 0)
        {
            _log(Lang.Get("log-no-client-log", GameLogs.ClientMain, logs.Directory));
            return;
        }

        _log(Lang.Get("log-game-log-head", GameLogs.ClientMain, tail.Count));
        foreach (var line in tail) _log(line);
        _log(Lang.Get("log-game-log-end"));
    }

    // Reporting the problems from a bad exit lives in RunningGames, which is what is still
    // there to notice one: the game can outlive this pane by hours.

    [RelayCommand]
    private void OpenLogsFolder()
    {
        var logs = GameLogs;

        if (!Files.OpenFolder(logs.Directory))
            _log(Lang.Get("log-open-failed", logs.Directory));
    }

    public ObservableCollection<ModRowViewModel> Mods { get; } = [];

    public ObservableCollection<SearchHitViewModel> SearchHits { get; } = [];

    public ObservableCollection<string> ReleaseChoices { get; } = [];

    [ObservableProperty] public partial string EditName { get; set; }
    [ObservableProperty] public partial string EditDescription { get; set; }
    [ObservableProperty] public partial string EditConnect { get; set; }

    /// <summary>
    /// Counts down rather than up, and only near the end. A limit nobody is close to is
    /// noise; one you are about to hit is the only time it is worth saying.
    /// </summary>
    public string DescriptionRoom
    {
        get
        {
            var left = PackManifest.MaxDescription - (EditDescription?.Length ?? 0);
            return left <= 40 ? Lang.Get("packsettings-chars-left", left) : "";
        }
    }

    public bool DescriptionTooLong => (EditDescription?.Length ?? 0) > PackManifest.MaxDescription;

    partial void OnEditDescriptionChanged(string value)
    {
        OnPropertyChanged(nameof(DescriptionRoom));
        OnPropertyChanged(nameof(DescriptionTooLong));
        SettingsEdited();
    }

    partial void OnEditNameChanged(string value) => SettingsEdited();

    partial void OnEditConnectChanged(string value) => SettingsEdited();

    private void SettingsEdited()
    {
        OnPropertyChanged(nameof(HasPendingSettings));
        SaveSettingsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>What a blank name means: the id, which is what the pack is called anyway.</summary>
    private string NameEdit => string.IsNullOrWhiteSpace(EditName) ? Id : EditName.Trim();

    /// <summary>
    /// Null rather than "", so an emptied field is omitted from the JSON entirely instead
    /// of publishing a blank one for a page to render a gap for.
    /// </summary>
    private static string? Emptied(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// An edit that is not on disk yet. Normally false — fields commit as focus leaves
    /// them — so it is true only while one is being typed, and stays true for a
    /// description too long to commit.
    /// </summary>
    public bool HasPendingSettings =>
        NameEdit != (Manifest.Name ?? Id)
        || Emptied(EditDescription) != Manifest.Description
        || Emptied(EditConnect) != Manifest.Connect;

    [ObservableProperty] public partial string SearchText { get; set; } = "";

    /// <summary>
    /// True once a search has run, until it is cleared. One list serves both purposes:
    /// the pack you are building, and the results you are building it from. Separate tabs
    /// made them look like peers and hid each from the other.
    /// </summary>
    [ObservableProperty] public partial bool ShowingSearch { get; set; }

    /// <summary>
    /// Which tab this pack's pane is showing. Zero — Mods — for a pack just opened.
    ///
    /// Held here rather than by the TabControl so that choosing another pack starts on
    /// Mods: the pane is rebuilt per pack, so a fresh one is on the first tab by
    /// construction and nothing has to remember to reset anything. Left to itself the
    /// control keeps the last pack's tab, which is how you end up reading one pack's Log
    /// while believing it belongs to another.
    /// </summary>
    [ObservableProperty] public partial int SelectedTab { get; set; }

    /// <summary>
    /// Where the Hotkeys tab sits, so opening it can start the scan. Reading seventy mod
    /// archives on every pack selection would be a second of disk nobody asked for, and
    /// most visits to a pack are to press Play.
    ///
    /// An index because that is what a TabControl selects by; held here with a test that
    /// the tab at this position is the one this names.
    /// </summary>
    public const int HotkeysTab = 2;

    /// <summary>
    /// Where the Mod config tab sits, so opening it re-reads the files. It has to be a read
    /// on every visit rather than once: the values it shows are ones somebody just changed
    /// in game, and the whole use of the tab is alt-tabbing out of a session to carry them.
    /// </summary>
    public const int ModConfigTab = 3;

    partial void OnSelectedTabChanged(int value)
    {
        // Leaving the tab abandons a capture in progress, rather than swallowing the next
        // keypress somewhere else entirely.
        if (value != HotkeysTab) CancelHotkeyCapture();

        if (value == HotkeysTab) _ = LoadHotkeysAsync();

        else if (value == ModConfigTab)
        {
            LoadModConfig();
            WatchModConfig();
        }

        // Only while the tab is showing. Nothing else in the window reads these files, and a
        // watcher per pack running for the life of the app would be watching a hundred-odd
        // files to update a list nobody is looking at.
        else StopWatchingModConfig();
    }

    partial void OnShowingSearchChanged(bool value)
    {
        OnPropertyChanged(nameof(ListHeading));
        OnPropertyChanged(nameof(ShowModUpdateCheck));
    }

    /// <summary>Says which of the two lists is on screen, and how big it is.</summary>
    public string ListHeading => ShowingSearch
        ? Lang.Plural("mods-results-for", SearchHits.Count, SearchHits.Count, _searchedFor)
            : Lang.Plural("mods-in-pack", Mods.Count, Mods.Count);

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
    /// True from the moment Play is pressed until the game exits. Syncing and process
    /// start take several seconds, and without this the window looked inert — and Play
    /// could be pressed again, starting a second copy.
    ///
    /// Read from <see cref="RunningGames"/> rather than held here, because this view model
    /// is rebuilt on every selection change: state of its own was lost the moment another
    /// pack was clicked, taking the running notification and the Play guard with it.
    /// </summary>
    public bool IsLaunching => _runs.IsLaunching(Id);

    /// <summary>
    /// The pane's progress line. A launch's stage comes from the registry so it survives
    /// this view model; publishing's is held here, because publishing is a modal errand
    /// that cannot outlive the pane that started it.
    /// </summary>
    public string LaunchStage => IsLaunching ? _runs.StageFor(Id) : PublishStage;

    [ObservableProperty] public partial string PublishStage { get; set; } = "";

    partial void OnPublishStageChanged(string value)
    {
        OnPropertyChanged(nameof(LaunchStage));
        OnPropertyChanged(nameof(IsShowingLaunchStage));
    }

    /// <summary>
    /// Brings the pane back in line with what the pack is actually doing — on construction,
    /// and whenever the registry says this pack's launch moved on. Also picks up a bad exit
    /// that happened while this pack had no pane to raise it on.
    /// </summary>
    public void RefreshLaunchState()
    {
        OnPropertyChanged(nameof(IsLaunching));
        OnPropertyChanged(nameof(LaunchStage));
        OnPropertyChanged(nameof(IsShowingLaunchStage));
        OnPropertyChanged(nameof(PlayLabel));
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(CanForceQuit));
        OnPropertyChanged(nameof(ShowPlay));
        OnPropertyChanged(nameof(IsStarting));
        PlayCommand.NotifyCanExecuteChanged();
        ForceQuitCommand.NotifyCanExecuteChanged();

        if (_runs.TakeExitNotice(Id) is { } notice) Error = notice;
    }

    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial string? ExportedPath { get; set; }
    [ObservableProperty] public partial string ExportedJson { get; set; } = "";
    [ObservableProperty] public partial bool ExportIncludesLock { get; set; } = true;

    public bool HasError => !string.IsNullOrEmpty(Error);

    public bool IsShowingLaunchStage => !string.IsNullOrEmpty(LaunchStage);

    public string PlayLabel => IsLaunching ? Lang.Get("pack-working") : Lang.Get("pack-play");

    public bool HasExported => !string.IsNullOrEmpty(ExportedPath);

    partial void OnExportedPathChanged(string? value) => OnPropertyChanged(nameof(HasExported));

    public string Title => Manifest.Name ?? Manifest.Id;

    /// <summary>
    /// Shown under the name rather than only in Settings. On a pack somebody else wrote it
    /// is their account of what this is, and needing to open an editing tab to read it is
    /// the wrong way round.
    /// </summary>
    public string? Description => Manifest.Description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Manifest.Description);

    public string Subtitle =>
        Lang.Plural("pack-subtitle", Manifest.Mods.Count, Manifest.GameVersion, Manifest.Mods.Count);

    /// <summary>See PackListItemViewModel.HasServer — blank is "opens at the main menu",
    /// not "singleplayer only".</summary>
    public bool HasServer => !string.IsNullOrWhiteSpace(Manifest.Connect);

    public string ServerLine => HasServer ? Lang.Get("pack-auto-joins", Manifest.Connect) : "";

    // ---- sharing ----

    /// <summary>
    /// Recomputed rather than cached, because it depends on the manifest and lock: adding
    /// a mod or syncing one is exactly what turns "Shared" into "Publish changes", and a
    /// button that remembers is a button that lies about the pack.
    /// </summary>
    [ObservableProperty] public partial ShareState Share { get; set; } = ShareState.NotShared;

    partial void OnShareChanged(ShareState value)
    {
        OnPropertyChanged(nameof(ShareLabel));
        OnPropertyChanged(nameof(ShareOffered));
        OnPropertyChanged(nameof(ShareIsUrgent));
        OnPropertyChanged(nameof(HasShareUrl));
        OnPropertyChanged(nameof(ShareUrlLine));
        OnPropertyChanged(nameof(IsFollowing));
        OnPropertyChanged(nameof(FollowingLine));
        OnPropertyChanged(nameof(IsWithdrawn));
        OnPropertyChanged(nameof(WithdrawnLine));
        OnPropertyChanged(nameof(CanShareFile));
        OnPropertyChanged(nameof(IsUnlisted));
        ExportCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Whether the published pack is unlisted. Shown beside the URL because the two look
    /// identical from outside, and which one it is decides whether passing the link
    /// around is sharing it or publishing it.
    /// </summary>
    public bool IsUnlisted => Share.IsUnlisted && HasShareUrl;

    public string ShareLabel => Share.Label;

    public bool ShareOffered => Share.IsOffered;

    public bool ShareIsUrgent => Share.IsUrgent;

    /// <summary>
    /// A URL worth showing: one this pack is actually served at. A followed pack's belongs
    /// to its author, and a withdrawn one answers 410 — offering either to copy would be
    /// handing somebody a link that does not do what the row says it does.
    /// </summary>
    public bool HasShareUrl => Share.HasUrl && !IsFollowing && !IsWithdrawn;

    /// <summary>Shown without its scheme: this is a thing people read and retype.</summary>
    public string ShareUrlLine => Share.Url is null
        ? ""
        : Share.Url.Replace("https://", "").Replace("http://", "");

    public bool IsFollowing => Share.Status == ShareStatus.Following;

    /// <summary>
    /// Says whose pack this is, and stands in for the Share button that is not there. A
    /// missing button with no explanation reads as a bug; this is the explanation.
    /// </summary>
    public string FollowingLine =>
        IsFollowing ? Lang.Get("share-imported-from", ShareUrlLine) : "";

    // ---- a newer revision from the author ----

    /// <summary>
    /// The author's newer revision, once a check has found one. Held rather than acted on:
    /// an update to somebody else's pack lands on top of whatever this person has done to
    /// their copy, so it waits until they ask for it.
    /// </summary>
    [ObservableProperty] public partial PackUpdateAvailable? PackUpdate { get; set; }

    public bool HasPackUpdate => PackUpdate is not null;

    public string PackUpdateLine => PackUpdate is null
        ? ""
        : Lang.Get("packupdate-revision-available", PackUpdate.To, PackUpdate.From);

    /// <summary>
    /// Offered for any followed pack, not only one with a revision waiting.
    ///
    /// Being on the author's latest is not the same as matching it: somebody who has edited
    /// their copy and wants it back needs a way in, and gating this on an update meant the
    /// only route to a reset was an author happening to publish.
    /// </summary>
    public bool CanReviewUpstream => IsFollowing;

    /// <summary>
    /// One label, always. It sits where Share sits for a pack you own — same slot, same
    /// relationship with cairns.gg, opposite direction — and on a followed pack it is the
    /// only "check for updates" on screen, because the mod-version controls are the
    /// author's until somebody unlocks them. Two buttons a player had to tell apart was
    /// the problem; naming them more carefully would only have made it survivable.
    /// </summary>
    public string ReviewUpstreamLabel => Lang.Get("pack-check-updates");

    // ---- editing somebody else's pack ----

    /// <summary>
    /// Whether the mod list is somebody else's to change.
    ///
    /// A followed pack is a curation, and the controls that alter it — search, remove, the
    /// version dropdown, mod updates — are hidden until asked for. Not a rule: Core does not
    /// consult this and neither does the CLI, because editing your own copy has always been
    /// allowed and still is. It is only about which of two things is the default.
    /// </summary>
    public bool IsLocked => IsFollowing && !_store.LoadLocalState(Id).Unlocked;

    /// <summary>True when the mod list may be changed here — the inverse, for binding.</summary>
    public bool CanEditMods => !IsLocked;

    /// <summary>
    /// Whether this copy still matches the author's, and so whether there is anything a
    /// reset would take away.
    ///
    /// Read from the recorded base, so it costs nothing and stays true as the pack is
    /// edited under it.
    /// </summary>
    public bool MatchesUpstream => _store.MatchesUpstream(Id);

    /// <summary>
    /// Offered only while there is provably nothing to undo. Once this copy has diverged,
    /// the way back is a reset — which says what it removes and what that costs a world.
    /// A relock that quietly left the changes in place would be a safeguard in name only.
    /// </summary>
    public bool CanRelock => IsFollowing && !IsLocked && MatchesUpstream;

    /// <summary>
    /// Shown only for a followed pack that has been unlocked. A pack of your own is not
    /// unlocked, it is simply yours, and telling somebody their own changes are kept would
    /// be noise on every pack they made.
    /// </summary>
    public bool ShowUnlockedNote => IsFollowing && !IsLocked;

    /// <summary>
    /// Hidden while search results are showing, as it always was — and while a check is
    /// running, where the progress line beside it says what is happening instead.
    ///
    /// The command keeps its own guard regardless. A hidden button is a courtesy, not a
    /// rule: nothing stops a keyboard, a script or a later view invoking the command, and
    /// running a second check would double the requests for the same answer.
    /// </summary>
    public bool ShowModUpdateCheck => CanEditMods && !ShowingSearch && !CheckingUpdates;

    public string LockedNote =>
        Lang.Get("pack-locked-note");

    public string UnlockedNote =>
        Lang.Get("pack-unlocked-note");

    [RelayCommand]
    private void UnlockMods()
    {
        var state = _store.LoadLocalState(Id);
        state.Unlocked = true;
        _store.SaveLocalState(Id, state);

        ReloadMods();
        _log(Lang.Get("pack-unlocked-log"));
    }

    [RelayCommand(CanExecute = nameof(CanRelock))]
    private void LockMods()
    {
        var state = _store.LoadLocalState(Id);
        state.Unlocked = false;
        _store.SaveLocalState(Id, state);

        ReloadMods();
    }

    public void RefreshLock()
    {
        OnPropertyChanged(nameof(IsLocked));
        OnPropertyChanged(nameof(CanEditMods));
        OnPropertyChanged(nameof(ShowUnlockedNote));
        OnPropertyChanged(nameof(ShowModUpdateCheck));

        // Pushed rather than read, so a row built before the share state was known does
        // not keep believing it is editable.
        foreach (var row in Mods) row.Editable = CanEditMods;
        OnPropertyChanged(nameof(MatchesUpstream));
        OnPropertyChanged(nameof(CanRelock));
        LockModsCommand.NotifyCanExecuteChanged();
    }

    partial void OnPackUpdateChanged(PackUpdateAvailable? value)
    {
        OnPropertyChanged(nameof(HasPackUpdate));
        OnPropertyChanged(nameof(PackUpdateLine));
    }

    /// <summary>Set by the view; see <see cref="ConfirmVersionChange"/>.</summary>
    public Func<PackUpdateViewModel, Task<bool>>? ConfirmPackUpdate { get; set; }

    /// <summary>
    /// Asks the author's URL whether they have published since, without saying anything if
    /// they have not.
    ///
    /// Quiet by design: this runs when a pack is opened, against a server that may be down,
    /// for a pack the person may not have opened in order to update. A failure here is not
    /// news, and the check costs one request against an address the pack already carries.
    /// </summary>
    public async Task CheckForPackUpdateAsync(CancellationToken ct = default)
    {
        if (!PackUpdateCheck.CanCheck(_store.LoadLink(Id))) return;

        var state = _store.LoadLocalState(Id);
        if (!PackUpdateCheck.IsDue(state)) return;

        // Recorded before the answer arrives, not after: a server that is slow or down
        // would otherwise leave the interval unstarted, and every reselect would try it
        // again. Being asked once every two hours is the promise; being answered is not
        // something this end controls.
        state.RecordCheck(DateTimeOffset.UtcNow);
        _store.SaveLocalState(Id, state);

        var found = await PackUpdateCheck
            .CheckAsync(_store.LoadLink(Id), _http, ct).ConfigureAwait(true);

        if (found is not null) PackUpdate = found;
    }

    /// <summary>
    /// Shows what the author's revision would do, and applies it if that is wanted.
    ///
    /// The plan is rebuilt from a fresh fetch rather than the one the check happened to
    /// find: a pack open on screen for an hour may be two revisions behind by now, and
    /// merging against a stale document would apply changes nobody was shown.
    /// </summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task ApplyPackUpdate()
    {
        IsBusy = true;
        Error = null;

        try
        {
            // Fetched rather than checked. Being on the author's latest revision is not the
            // same as matching it — somebody who has edited their copy and wants it back is
            // asking about their own divergence, and a check would answer "nothing newer"
            // and leave them with no way in.
            var bundle = await PackUpdateCheck.FetchAsync(_store.LoadLink(Id), _http);

            if (bundle is null)
            {
                PackUpdate = null;
                _log(Lang.Get("pack-upstream-unreachable"));
                return;
            }

            var link = _store.LoadLink(Id);
            var available = (bundle.Revision ?? 0) > (link?.Revision ?? 0);

            PackUpdate = available
                ? new PackUpdateAvailable(link!.Revision, bundle.Revision ?? 0, bundle)
                : null;

            var plan = PackUpdatePlan.Between(
                Manifest, bundle.Pack!, _store.LoadUpstream(Id),
                link?.Revision ?? 0, bundle.Revision ?? 0, _store.LoadLocalState(Id),
                _store.LoadLock(Id), bundle.Lock);

            // Nothing of theirs to take and nothing of yours that differs. Opening an empty
            // dialog to say so would be worse than saying so.
            if (!plan.AnyChange && !plan.Changes.Any())
            {
                _log(Lang.Get("pack-matches-revision", bundle.Revision ?? 0));
                return;
            }

            if (ConfirmPackUpdate is null) return;
            if (!await ConfirmPackUpdate(
                    new PackUpdateViewModel(plan, Title, _packData.Worlds(Id)))) return;

            var merged = _store.ApplyUpdate(Id, plan, bundle);

            // Copied into the instance the pane is bound to rather than swapped for the
            // new one: every row, header and command already points at this object.
            //
            // Every field, for the reason PackUpdatePlan.Merge gives about its own list. One
            // left out here is worse than one left out there: the merge reached disk, so this
            // object is the only thing still holding the old value — and Persist writes it
            // whole on the next ordinary edit. Reorder a mod after taking an update and the
            // author's keybinds and mod settings were quietly replaced by the ones they had
            // just been updated away from.
            Manifest.Name = merged.Name;
            Manifest.Description = merged.Description;
            Manifest.GameVersion = merged.GameVersion;
            Manifest.Connect = merged.Connect;
            Manifest.Keybinds = merged.Keybinds;
            Manifest.ModConfig = merged.ModConfig;
            Manifest.Mods.Clear();
            Manifest.Mods.AddRange(merged.Mods);

            PackUpdate = null;
            _releaseCache.Clear();      // the game version may have moved under every mod

            // A reset leaves this copy matching the author's, so the guard goes back up on
            // its own: the thing it exists to protect is no longer at stake.
            if (plan.Reset)
            {
                var state = _store.LoadLocalState(Id);
                state.Unlocked = false;
                _store.SaveLocalState(Id, state);
            }

            _log(plan.Reset
                ? Lang.Get("pack-reset-to-revision", bundle.Revision ?? 0)
                : Lang.Get("pack-updated-to-revision", bundle.Revision ?? 0));

            ReloadMods();
            ReloadShare();
            RefreshGameState();
            RefreshLock();

            // Both tabs read the manifest that just changed under them, and a tick showing
            // the pack's old answer is one somebody would correct — writing the old answer
            // back as though they had chosen it. Hotkeys only if that tab has already paid
            // for its scan; forced, because the rows are stale rather than absent.
            LoadModConfig(adopting: true);
            if (_hotkeysLoaded) _ = LoadHotkeysAsync(force: true);

            OnPropertyChanged(nameof(Title));
            _onChanged();

            // The manifest changed, so what is installed no longer matches it. Quiet,
            // because taking the update worked and Play will report properly if this
            // cannot.
            await RunSyncAsync(quiet: true);
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

    public bool IsWithdrawn => Share.Status == ShareStatus.Withdrawn;

    /// <summary>
    /// Stands in for the URL line, which is not shown while the address serves a
    /// tombstone. Says the address is still the pack's, because the button beside it
    /// offers to publish again and the question that raises is where it would land.
    /// </summary>
    public string WithdrawnLine =>
        IsWithdrawn ? Lang.Get("share-withdrawn-note", ShareUrlLine) : "";

    /// <summary>Whether the export controls are offered at all. See <see cref="Export"/>.</summary>
    public bool CanShareFile => !IsFollowing;

    private void ReloadShare()
    {
        Share = _store.ShareStateFor(Id);

        // Whether this pack is somebody else's is what decides the lock, and it is only
        // known once the share state is. The rows are built before that, so refreshing
        // here is what stops every row of a followed pack believing it is editable.
        RefreshLock();
    }

    public string ModsDirectory => _store.ModsDir(Id);

    /// <summary>
    /// The install this pack will actually launch, or null when its version is absent.
    ///
    /// A chosen install wins, and only a chosen one: <see cref="GameLibrary.ForVersion"/>
    /// never returns a modified build, so running something other than the stock game is
    /// always something this pack was told to do.
    ///
    /// A choice that has gone — the directory deleted, the build replaced by something that
    /// is not an install — falls back rather than failing. Somebody who removes a build will
    /// not connect it to a pack refusing to start, and <see cref="ChosenInstallMissing"/> is
    /// what says so instead.
    /// </summary>
    public GameInstall? ResolvedInstall => Resolution.Install;

    /// <summary>
    /// What this pack runs and why, answered by Core so the CLI answers it the same way.
    /// </summary>
    private GameLibrary.InstallResolution Resolution => _library.ResolveFor(
        Manifest.GameVersion, _store.LoadLocalState(Id).InstallDirectory);

    /// <summary>The install this pack was told to use, if it is still there and still fits.</summary>
    public GameInstall? ChosenInstall =>
        Resolution.State == GameLibrary.ChoiceState.Honoured ? Resolution.Chosen : null;

    /// <summary>A choice was made and the install behind it is no longer there.</summary>
    public bool ChosenInstallMissing => Resolution.State == GameLibrary.ChoiceState.Missing;

    public bool HasInstallNote => !string.IsNullOrEmpty(InstallChoiceLine);

    /// <summary>The Optimum build for this pack's version, if one has been made.</summary>
    private GameInstall? OptimumInstall =>
        OptimumSource.Pinned.Supports(Manifest.GameVersion)
            ? GameInstall.TryAt(_library.Store.InstallDir(OptimumSource.Pinned.InstallName))
            : null;

    /// <summary>Whether this pack is currently set to run something other than the stock game.</summary>
    public bool IsUsingVariant => ResolvedInstall is { IsVariant: true };

    /// <summary>
    /// A built client is available and this pack is not using it.
    ///
    /// The other half of <see cref="CanBuildOptimum"/>: between them the panel offers
    /// exactly one action at a time — make it, use it, or stop using it. It replaced a
    /// picker listing every install, which asked somebody to choose from a list that on
    /// almost every machine has two entries and one obvious answer.
    /// </summary>
    public bool CanUseOptimum => OptimumInstall is not null && !IsUsingVariant;

    /// <summary>Points this pack at the built client.</summary>
    [RelayCommand]
    private void UseOptimum()
    {
        if (OptimumInstall is { } install) ChooseInstall(install);
    }

    /// <summary>
    /// Puts this pack back on the stock game.
    ///
    /// Its own command rather than a value in a list, and it must exist: without it,
    /// choosing a modified client would be a decision nothing on screen could undo.
    /// </summary>
    [RelayCommand]
    private void UseStockGame() => ChooseInstall(null);

    public string InstallChoiceLine => Resolution switch
    {
        { State: GameLibrary.ChoiceState.Missing } =>
            Lang.Get("install-gone"),

        // Named on both sides, because the fix depends on knowing which is which: either
        // retarget the pack back, or build this version.
        { State: GameLibrary.ChoiceState.WrongVersion, Chosen: { } c } =>
            Lang.Get("install-wrong-version", c.Describe, c.Version, Manifest.GameVersion),

        { Install: { IsVariant: true } v } => Lang.Get("install-variant", v.Variant),

        _ => "",
    };

    /// <summary>
    /// Picks the install this pack launches with. Null clears it, which is not the same as
    /// choosing the stock one — a cleared pack follows whatever the stock install becomes.
    /// </summary>
    public void ChooseInstall(GameInstall? install)
    {
        var state = _store.LoadLocalState(Id);

        // The stock install is stored as no choice at all, so a pack does not end up
        // pinned to a directory that Cairn is about to replace on the next game update.
        state.InstallDirectory = install is { IsVariant: true } ? install.Directory : null;
        _store.SaveLocalState(Id, state);

        RefreshGameState();
    }

    // ---- building a variant ----

    /// <summary>Set by the view; the cost warning, before anything starts.</summary>
    public Func<ConfirmViewModel, Task<bool>>? Confirm { get; set; }

    /// <summary>Set by the view; shows the build happening and returns whether it worked.</summary>
    public Func<OptimumBuildViewModel, Task<bool>>? RunOptimumBuild { get; set; }

    /// <summary>
    /// Whether building Optimum is worth offering for this pack.
    ///
    /// Only where it would actually apply: Optimum targets exactly one Vintage Story
    /// version at a time, so offering it to a pack on any other one is an invitation to
    /// spend twenty minutes producing a client the pack cannot use. Withdrawn once it is
    /// built, because from then on it is an install to pick, not a thing to make.
    /// </summary>
    public bool CanBuildOptimum =>
        OptimumSource.Pinned.Supports(Manifest.GameVersion)
        && OptimumPrereqs.UnsupportedReason() is null
        // Asked of the install directory rather than of the choices offered for this
        // version, because the two differ exactly when the build is broken: a half-written
        // install reports no version, drops out of the picker, and would otherwise leave
        // this hidden with nothing on screen able to rebuild it.
        && GameInstall.TryAt(
            _library.Store.InstallDir(OptimumSource.Pinned.InstallName)) is null;

    public string BuildOptimumLabel => Lang.Get("optimum-build-label", OptimumSource.Pinned.Version);

    /// <summary>
    /// Whether it can be started right now, as opposed to whether it applies to this pack.
    ///
    /// Separate from <see cref="CanBuildOptimum"/> so a pending version change disables the
    /// button rather than hiding it: the panel vanishing the moment somebody touches the
    /// version picker reads as a bug, and it would come back looking identical whichever
    /// way the check went. Disabled with a reason says which version the build would be for
    /// — the one the pack still targets, not the one now showing in the picker.
    /// </summary>
    public bool CanBuildOptimumNow =>
        CanBuildOptimum && !HasPendingGameVersion && !IsCheckingVersion;

    /// <summary>Why the button is greyed, or empty when it is not.</summary>
    public string BuildOptimumBlockedNote =>
        CanBuildOptimum && HasPendingGameVersion
            ? Lang.Get("optimum-finish-change-first", TargetGameVersion, Manifest.GameVersion)
            : "";

    public bool HasBuildOptimumBlockedNote => !string.IsNullOrEmpty(BuildOptimumBlockedNote);

    /// <summary>
    /// Whether the optimised-client panel has anything to say at all.
    ///
    /// False on every pack targeting a version Optimum is not for, which is most of them —
    /// and the panel disappearing entirely is the point: it is an advanced option, not a
    /// setting everybody has to have an opinion about.
    /// </summary>
    public bool HasOptimumPanel => CanBuildOptimum || CanUseOptimum || IsUsingVariant;

    /// <summary>
    /// Builds Optimum, then points this pack at it.
    ///
    /// The confirmation comes first and states the cost in full, because this is unlike
    /// everything else Cairn installs: a twenty-minute compile rather than a download. The
    /// pack is only moved onto the result if it was really built — a cancelled or failed
    /// build leaves the pack exactly as it was.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanBuildOptimumNow))]
    private async Task BuildOptimumAsync()
    {
        if (Confirm is null || RunOptimumBuild is null) return;

        var provisioner = new OptimumProvisioner(_http, _library.Store, _runtimes);
        var source = OptimumSource.Pinned;
        var plan = provisioner.Plan(source.GameVersion, source);

        if (!plan.CanStart)
        {
            // Missing tools and a full disk are both things only the person at the machine
            // can fix, so this reports and stops rather than offering to press on.
            await Confirm(new ConfirmViewModel(
                Lang.Get("optimum-cannot-build"), plan.Describe(), Lang.Get("common-ok")));
            return;
        }

        if (!await Confirm(new ConfirmViewModel(
                Lang.Get("optimum-build-title"), plan.Describe(), Lang.Get("optimum-build-confirm"))))
            return;

        // The stock install of the same version, so the packager overlays the client
        // already on disk instead of downloading a second copy of it.
        var vanilla = _library.ForVersion(source.GameVersion);

        var build = new OptimumBuildViewModel(provisioner, source, vanilla);

        if (!await RunOptimumBuild(build) || build.Result is null) return;

        _log(Lang.Get("optimum-built", build.Result.Describe));

        // Records the choice and re-reads the whole game situation, which is what makes the
        // new install appear in the picker and this button go away.
        ChooseInstall(build.Result);
    }

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

        var rows = new List<ModRowViewModel>();

        foreach (var mod in Manifest.Mods)
        {
            var locked = locks?.Mods.FirstOrDefault(
                m => string.Equals(m.ModId, mod.ModId, StringComparison.OrdinalIgnoreCase));

            rows.Add(Row(mod, locked));
        }

        // Mods pulled in by other mods live in the lockfile, not the manifest — that is
        // where installed-but-not-asked-for belongs. Reading only the manifest left them
        // correctly installed and completely invisible.
        var named = Manifest.Mods.Select(m => m.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var locked in locks?.Mods ?? [])
        {
            if (named.Contains(locked.ModId) || locked.RequiredBy is not { Count: > 0 }) continue;
            rows.Add(Row(new PackMod { ModId = locked.ModId }, locked));
        }

        foreach (var row in Arrange(rows)) Mods.Add(row);

        ModRowViewModel Row(PackMod mod, LockedMod? locked) => new(
            mod, locked,
            loadReleases: ChoosePinForRowAsync,
            pin: ApplyPin,
            remove: RemoveRow,
            openPage: OpenModPage,
            armed: DisarmOtherRows,
            update: UpdateOne,
            editable: CanEditMods);

        RefreshLock();

        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(ListHeading));

        // Every path that changes what a pack contains ends here — adding, removing,
        // pinning, syncing — which makes it the one place the Share button has to be
        // recomputed from.
        ReloadShare();

        // And the one place the hotkeys can be, for somebody who is looking at them while
        // the mods move underneath. Cheap when nothing has changed: the stamp is a
        // directory listing, and the scan only runs when the files are not the ones the
        // rows were read from.
        if (SelectedTab == HotkeysTab) _ = LoadHotkeysAsync();

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

        // Once, at the end. Re-sorting as each name landed would walk the rows around
        // under the pointer for as long as ModDB took to answer.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation == _modIconGeneration) SortRows();
        });
    }

    /// <summary>
    /// Orders rows by what they display — the mod's name once ModDB has supplied one, its
    /// id until then — with each dependency following the mod that pulled it in.
    ///
    /// Sorting dependencies in among everything else would scatter them: `carryonlib` under
    /// C, three rows away from the `carryon` that is the only reason it is installed.
    /// </summary>
    private static List<ModRowViewModel> Arrange(IReadOnlyList<ModRowViewModel> rows)
    {
        var byName = StringComparer.OrdinalIgnoreCase;
        var dependencies = rows.Where(r => r.IsDependency).ToList();
        var arranged = new List<ModRowViewModel>();

        foreach (var direct in rows.Where(r => r.IsDirect).OrderBy(r => r.Title, byName))
        {
            arranged.Add(direct);
            arranged.AddRange(dependencies
                .Where(d => byName.Equals(d.RequiredByFirst, direct.ModId))
                .OrderBy(d => d.Title, byName));
        }

        // A dependency whose requirer is not itself a row — possible only from a hand-edited
        // lockfile, but it should still be visible rather than silently dropped.
        arranged.AddRange(dependencies.Except(arranged).OrderBy(d => d.Title, byName));

        return arranged;
    }

    /// <summary>
    /// Reapplies <see cref="Arrange"/> in place, once names have arrived. Moving rows
    /// rather than rebuilding them keeps each row's own state — an armed remove, a release
    /// list already fetched.
    /// </summary>
    private void SortRows()
    {
        var sorted = Arrange([.. Mods]);

        for (var target = 0; target < sorted.Count; target++)
        {
            var from = Mods.IndexOf(sorted[target]);
            if (from != target) Mods.Move(from, target);
        }
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

    /// <summary>
    /// Commits the settings fields, as focus leaves one of them.
    ///
    /// These used to be held until Save. The detail pane is rebuilt whenever the selected
    /// pack changes, so typing a description and then clicking another pack threw the
    /// words away without saying anything — and there is nothing here that wants a
    /// confirmation step: a name is as reversible as retyping it.
    ///
    /// Does nothing when the fields already match the manifest, so merely tabbing through
    /// the form does not write a file or add a line to the log.
    /// </summary>
    public void CommitSettings()
    {
        if (!HasPendingSettings) return;
        WriteSettings();
    }

    [RelayCommand(CanExecute = nameof(CanSaveSettings))]
    private void SaveSettings() => WriteSettings();

    private void WriteSettings()
    {
        Manifest.Name = NameEdit;

        // Left alone while it is too long. That keeps the words in the box with the
        // counter under them, rather than committing a sentence cut in half — and it lets
        // the other two fields commit around a description still being worked on.
        if (!DescriptionTooLong)
            Manifest.Description = Emptied(EditDescription);

        // The game version deliberately does not come from here any more: changing it
        // re-resolves every mod, so it goes through Check → Apply instead.
        Manifest.Connect = Emptied(EditConnect);

        Persist();

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(HasDescription));
        OnPropertyChanged(nameof(HasServer));
        OnPropertyChanged(nameof(ServerLine));
        SettingsEdited();
        RefreshGameState();
        _log(Lang.Get("packsettings-saved", Id));
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

    /// <summary>
    /// A version has been picked that the pack does not yet target.
    ///
    /// The gap between the two is the whole point of the check step: nothing is written
    /// until it has run, so while this is true the picker says one thing and the pack still
    /// is another. Anything derived from the game version has to say which of the two it
    /// means, or act on neither.
    /// </summary>
    public bool HasPendingGameVersion =>
        !string.IsNullOrWhiteSpace(TargetGameVersion)
        && !string.Equals(TargetGameVersion, Manifest.GameVersion, StringComparison.OrdinalIgnoreCase);

    public bool CanCheckVersion => !IsCheckingVersion && HasPendingGameVersion;

    partial void OnTargetGameVersionChanged(string? value)
    {
        // A different target invalidates the answer on screen, which was about the old one.
        VersionChange = null;
        OnPropertyChanged(nameof(CanCheckVersion));
        CheckVersionCommand.NotifyCanExecuteChanged();
        NotifyPendingVersionChanged();

        if (!HasPendingGameVersion) return;

        // Picking a version starts its check. Nothing is written until the result is
        // confirmed — that is still the point of the step — but leaving it to a separate
        // button meant the picker could sit showing a version the pack was not on and had
        // no intention of moving to, which reads as a setting that did not take.
        //
        // Any check already running is for a version nobody is looking at now.
        CheckVersionCommand.Cancel();
        CheckVersionCommand.Execute(null);
    }

    partial void OnIsCheckingVersionChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCheckVersion));
        CheckVersionCommand.NotifyCanExecuteChanged();
        NotifyPendingVersionChanged();
    }

    private void NotifyPendingVersionChanged()
    {
        OnPropertyChanged(nameof(HasPendingGameVersion));
        OnPropertyChanged(nameof(CanBuildOptimumNow));
        OnPropertyChanged(nameof(BuildOptimumBlockedNote));
        BuildOptimumCommand.NotifyCanExecuteChanged();
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

    /// <summary>
    /// Which check is current. Picking a third version while the second is still resolving
    /// leaves two in flight, and the older one must not report its answer or clear the
    /// busy flag out from under the newer.
    /// </summary>
    private int _versionCheckGeneration;

    [RelayCommand(CanExecute = nameof(CanCheckVersion))]
    private async Task CheckVersion(CancellationToken ct)
    {
        var target = TargetGameVersion!.Trim();
        var generation = ++_versionCheckGeneration;

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

            if (generation != _versionCheckGeneration) return;

            var change = new VersionChangeViewModel(plan);
            VersionChange = change;
            _log(Lang.Get("versionchange-checked", Manifest.GameVersion, target, plan.Summary()));

            if (ConfirmVersionChange is not null)
            {
                if (await ConfirmVersionChange(change)) ApplyVersionChange();
                else CancelVersionChange();
            }
        }
        catch (OperationCanceledException)
        {
            // Leaving the pane mid-check, or picking another version, is not an error.
        }
        catch (Exception e)
        {
            if (generation != _versionCheckGeneration) return;

            Error = e.Message;

            // Put back, because the change did not happen. Not the offline case — failing
            // to reach ModDB is a verdict on the mods, not an exception — but the
            // unforeseen one: a lockfile that will not parse, a path that will not read.
            // With the check started by the picker there is no button left to press again,
            // and picking the same entry twice raises no change to retry from, so a
            // failure that left the target showing would strand the pane on a version the
            // pack is not on and cannot be moved to.
            TargetGameVersion = Manifest.GameVersion;
        }
        finally
        {
            if (generation == _versionCheckGeneration)
            {
                IsCheckingVersion = false;
                CheckingMod = "";
            }
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

        _log(Lang.Get("versionchange-applied", target));
    }

    /// <summary>
    /// Abandons a checked change and puts the picker back where the pack actually is.
    ///
    /// The picker matters as much as the plan. Saying no used to leave the other version
    /// showing, so the control claimed a version the pack had just declined to move to —
    /// and every later glance at the pane read as a setting that had not taken.
    /// </summary>
    [RelayCommand]
    public void CancelVersionChange()
    {
        VersionChange = null;

        // Bumped so the check this abandons cannot come back and reopen its dialog.
        _versionCheckGeneration++;

        if (HasPendingGameVersion) TargetGameVersion = Manifest.GameVersion;
    }

    public bool NotBusy => !IsBusy;

    /// <summary>
    /// Blocked on an over-long description rather than silently truncating one: a
    /// description cut off mid-sentence on somebody else's screen is worse than being told
    /// now, while the words are still in front of you.
    ///
    /// Also blocked when there is nothing outstanding. Fields commit as focus leaves them,
    /// so a Save that is always available would mostly be a button that does nothing;
    /// enabled means there is genuinely something in the box that is not on disk.
    /// </summary>
    private bool CanSaveSettings => NotBusy && !DescriptionTooLong && HasPendingSettings;

    /// <summary>See <see cref="Export"/> — a file made from a followed pack loses its
    /// author on the way out.</summary>
    private bool CanExport => NotBusy && !IsFollowing;

    /// <summary>
    /// Re-evaluates everything that depends on the game situation: which install serves this
    /// pack, what else it could run, and whether a client can be built for it.
    ///
    /// One method for all of it because they all answer to the same three events — the
    /// pack's version changed, an install appeared or went away, or a choice was made — and
    /// every caller that knows about one of those knows about all three. Split up, each new
    /// derived property had to be added to each call site, and the ones that were missed
    /// simply went stale on screen until the pack was reselected.
    /// </summary>
    public void RefreshGameState()
    {
        OnPropertyChanged(nameof(ResolvedInstall));
        OnPropertyChanged(nameof(IsProvisioning));
        OnPropertyChanged(nameof(CanLaunch));
        PlayCommand.NotifyCanExecuteChanged();

        OnPropertyChanged(nameof(ChosenInstall));
        OnPropertyChanged(nameof(ChosenInstallMissing));
        OnPropertyChanged(nameof(InstallChoiceLine));
        OnPropertyChanged(nameof(HasInstallNote));

        OnPropertyChanged(nameof(CanBuildOptimum));
        OnPropertyChanged(nameof(CanUseOptimum));
        OnPropertyChanged(nameof(IsUsingVariant));
        OnPropertyChanged(nameof(HasOptimumPanel));
        NotifyPendingVersionChanged();
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
                ? Lang.Get("mods-search-trimmed", hits.Count, _searchedFor, SearchHits.Count)
                    : Lang.Get("mods-search-count", SearchHits.Count, _searchedFor));
            if (SearchHits.Count == 0)
                Error = Lang.Get("mods-no-matches");
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

    private async void AddHit(SearchHitViewModel hit)
    {
        // A ModDB page that publishes no mod id is not something a pack can name. Optimum
        // is one — a modified client rather than a mod — and adding it wrote an empty
        // modid into the manifest, which used to stop the whole pack syncing.
        if (string.IsNullOrWhiteSpace(hit.ModId))
        {
            _log(Lang.Get("mods-no-modid", hit.Name));
            return;
        }

        if (Manifest.Mods.Any(m => string.Equals(m.ModId, hit.ModId, StringComparison.OrdinalIgnoreCase)))
        {
            hit.AlreadyInPack = true;
            return;
        }

        // Asked before anything is written, and stated plainly: what the mod is marked for,
        // what the pack targets, and that the pack is what breaks if this was optimistic.
        // A mod nobody has rebuilt often works fine — but "often" is the whole reason this
        // is a question rather than a silent allowance.
        if (hit.NeedsAcceptance && Confirm is not null)
        {
            var marked = hit.MarkedFor;

            var agreed = await Confirm(new ConfirmViewModel(
                Lang.Get("mods-add-anyway-title", hit.Name),
                Lang.Get("mods-add-anyway-body", hit.Name, Manifest.GameVersion, marked),
                Lang.Get("mods-add-anyway-confirm")));

            if (!agreed) return;
        }

        Manifest.Mods.Add(new PackMod
        {
            ModId = hit.ModId,

            // Recorded against the version the pack targets now, so retargeting a minor
            // asks again rather than carrying somebody's answer to a different question.
            AcceptedFor = hit.NeedsAcceptance ? Manifest.GameVersion : null,
        });

        Persist();

        // The row stays on screen, so it has to stop offering to add it again.
        hit.AlreadyInPack = true;
        _log(Lang.Get("mods-added", hit.ModId));

        ReloadMods();

        var added = Mods.FirstOrDefault(
            m => string.Equals(m.ModId, hit.ModId, StringComparison.OrdinalIgnoreCase));
        if (added is not null) added.Downloading = true;

        _ = SyncAfterEditAsync();
    }

    /// <summary>Whether the sync currently running is one of these, rather than a launch.</summary>
    private bool _quietSync;

    /// <summary>
    /// Set when the pack is edited again while a quiet sync is running. See
    /// <see cref="SyncAfterEditAsync"/>.
    /// </summary>
    private bool _syncAgain;

    /// <summary>
    /// Settles the pack against the manifest that was just edited, without being asked.
    ///
    /// Adding a mod only writes a line to the manifest, so before this the row sat there
    /// with no version and nothing happened until the next Play. It also matters for
    /// dependencies: they are declared inside the zip, so a mod's requirements cannot be
    /// known — or shown — until it has actually been downloaded.
    ///
    /// Removing one needs it just as much, and for the mirror-image reason. The rows come
    /// from the lock, and the lock is only rebuilt by a sync — so removing a mod that had
    /// pulled in seven others left those seven on screen, still installed, now requiring
    /// nothing, and wearing no Remove button because a dependency row does not have one.
    /// The pack looked stuck with mods nobody could get rid of until the next Play. The
    /// closure is Core's to compute and not something to reproduce here: sync drops them
    /// from the lock and deletes the zips, exactly as it always did.
    /// </summary>
    private async Task SyncAfterEditAsync()
    {
        // Coalesced rather than dropped. Removing three mods in a row used to run one sync
        // and skip the other two, settling the pack against the manifest as it stood
        // partway through — the same stale rows, arrived at less obviously. The run in
        // flight goes round again instead, against the manifest as it now is.
        if (_quietSync)
        {
            _syncAgain = true;
            return;
        }

        // A launch or an update is already going to sync and rebuild these rows, and two at
        // once would race for the same directory and lockfile.
        if (IsBusy || IsLaunching) return;

        _quietSync = true;

        try
        {
            do
            {
                _syncAgain = false;
                await RunSyncAsync(quiet: true);
            }
            while (_syncAgain && !IsLaunching);
        }
        finally
        {
            _quietSync = false;
            _syncAgain = false;
        }
    }

    // ---- updates ----

    /// <summary>Set while a check is running, so the button can say so.</summary>
    [ObservableProperty] public partial bool CheckingUpdates { get; set; }

    /// <summary>
    /// Which mod is being asked about, and how far through.
    ///
    /// The check is one ModDB request per unpinned mod, so a thirty-mod pack sits there for
    /// some seconds. It used to sit there showing nothing at all — the busy flag was set and
    /// bound to nothing — which is indistinguishable from a button that did not work, and
    /// invites pressing it again.
    ///
    /// A count as well as a name, because a name alone moves without saying whether it is
    /// nearly done.
    /// </summary>
    [ObservableProperty] public partial string CheckingUpdatesLine { get; set; } = "";

    partial void OnCheckingUpdatesChanged(bool value)
    {
        if (!value) CheckingUpdatesLine = "";
        CheckUpdatesCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsWorking));
        OnPropertyChanged(nameof(ShowModUpdateCheck));
    }

    /// <summary>
    /// Anything the status bar should show a bar for.
    ///
    /// Checking for mod updates does not set IsBusy — it leaves the pane usable — but it is
    /// still several seconds of network, and the status bar is where a second pair of eyes
    /// looks to see whether the app is doing something.
    /// </summary>
    public bool IsWorking => IsBusy || CheckingUpdates;

    /// <summary>Not while one is already running: pressing again would double the requests.</summary>
    private bool CanCheckUpdates => NotBusy && !CheckingUpdates;

    [ObservableProperty] public partial string? UpdateSummary { get; set; }

    public bool HasUpdateSummary => !string.IsNullOrWhiteSpace(UpdateSummary);

    partial void OnUpdateSummaryChanged(string? value) => OnPropertyChanged(nameof(HasUpdateSummary));

    public bool AnyUpdates => Mods.Any(m => m.HasUpdate);

    /// <summary>
    /// Asks ModDB what each followed mod would move to. Reports only — nothing is
    /// installed until it is asked for, one mod or all.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCheckUpdates))]
    private async Task CheckUpdates()
    {
        CheckingUpdates = true;
        Error = null;

        // Only the unpinned ones are asked about; see PackSyncer.CheckUpdatesAsync.
        var total = Manifest.Mods.Count(m => m.Version is null);
        var done = 0;

        var progress = new Progress<string>(modId =>
        {
            done++;
            CheckingUpdatesLine = total > 1
                ? Lang.Get("mods-checking-of", modId, done, total)
                    : Lang.Get("mods-checking", modId);
        });

        try
        {
            var syncer = new PackSyncer(_moddb, _http);
            // Remembered briefly, so pressing this twice does not spend one ModDB request
            // per mod to be told the same thing. Cleared from Preferences → Storage when
            // somebody knows a release has just landed.
            var updates = await syncer.CheckUpdatesAsync(
                Manifest, _store.LockPath(Id), progress, cache: new ModUpdateCache());

            foreach (var row in Mods)
                row.UpdateAvailable = updates
                    .FirstOrDefault(u => string.Equals(u.ModId, row.ModId, StringComparison.OrdinalIgnoreCase))
                    ?.To;

            UpdateSummary = updates.Count == 0
                ? Lang.Get("mods-up-to-date")
                    : Lang.Plural("mods-updates-available", updates.Count, updates.Count);

            foreach (var u in updates) _log(Lang.Get("mods-update-available", u.Describe()));
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

            if (report.Failed) Error = Lang.Get("mods-update-failed");
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
        _log(Lang.Get("mods-removed", row.ModId));

        // If it is also on screen as a search result, offer it again.
        foreach (var hit in SearchHits.Where(
                     h => string.Equals(h.ModId, row.ModId, StringComparison.OrdinalIgnoreCase)))
            hit.AlreadyInPack = false;

        // Whatever this mod was the only reason for goes with it — see SyncAfterEditAsync.
        _ = SyncAfterEditAsync();
    }

    private void OpenHitPage(SearchHitViewModel hit)
    {
        if (!Browser.Open(hit.PageUrl))
            Error = Lang.Get("mods-no-browser");
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
            Error = Lang.Get("mods-not-found", row.ModId);
            return;
        }

        if (!Browser.Open(ModDbUrls.Page(info.AssetId, info.UrlAlias)))
            Error = Lang.Get("mods-page-failed", row.ModId);
    }

    // ---- sharing ----

    /// <summary>
    /// Opens the pack's page on cairns.gg. Only reachable once a pack has been published,
    /// since that is the only time there is a page.
    /// </summary>
    [RelayCommand]
    private void OpenSharePage()
    {
        if (Share.Url is not null && !Browser.Open(Share.Url))
            Error = Lang.Get("share-open-failed", ShareUrlLine);
    }

    /// <summary>
    /// The last prepared publish. Nothing has been sent while it is set, which is the
    /// point of the step — the same arrangement as VersionChange.
    /// </summary>
    [ObservableProperty] public partial ShareViewModel? Publish { get; set; }

    /// <summary>
    /// Shows the plan and returns whether to send it. Supplied by the view; when absent —
    /// headless tests — the result stays on <see cref="Publish"/> to be inspected.
    /// </summary>
    public Func<ShareViewModel, Task<bool>>? ConfirmPublish { get; set; }

    /// <summary>
    /// Works out what publishing would send, shows it, and sends it if that is confirmed.
    ///
    /// The document that goes up is the one the window fingerprinted, not one rebuilt
    /// afterwards — a document assembled a second time is a document that can differ from
    /// the one somebody agreed to.
    /// </summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task PublishPack()
    {
        // The button is hidden while following, so this is unreachable from the UI. It is
        // here because "the button is not drawn" is not the same as "the rule holds" —
        // the command is bindable, and this is the rule.
        if (IsFollowing)
        {
            Error = Lang.Get("share-cannot-publish-follower", ShareUrlLine);
            return;
        }

        IsBusy = true;
        Error = null;

        try
        {
            var session = await SignInAsync();
            if (session is null) return;

            var progress = new Progress<string>(id => PublishStage = Lang.Get("share-checking", id));

            var plan = await PublishPlan.PrepareAsync(
                Manifest, _store.LoadLock(Id), _moddb, progress);

            // Only when it would otherwise refuse. Publishing used to answer a lock that
            // does not cover the manifest by saying "sync the pack first", which meant
            // pressing Play — starting the game — in order to share. Syncing here removes
            // that, but only in the case that was already broken: a settled pack must not
            // have its lock rewritten by the act of sharing it, and an unreachable ModDB
            // must not be able to turn sharing into a change to what is installed.
            if (!plan.LockCovers)
            {
                PublishStage = Lang.Get("share-syncing");
                var sync = await RunSyncAsync(quiet: true);

                // RunSyncAsync clears IsBusy in its own finally, and publishing continues.
                IsBusy = true;

                plan = await PublishPlan.PrepareAsync(
                    Manifest, _store.LoadLock(Id), _moddb, progress, syncFailures: sync?.Steps);
            }

            // Before the window is built, because the window is what would refuse. A
            // withdrawal made on the site never reaches this machine, so the publish
            // record it compares against can be describing a pack that is no longer at
            // that address — and republishing it unchanged is how it comes back.
            //
            // Here rather than when the pack is opened: share state is a local projection
            // by design, and this is the one moment the server's answer changes anything.
            await ReconcileWithdrawalAsync(session);

            Publish = ShareViewModel.From(
                plan, Title, session.Username, _store.LoadLink(Id),
                strip => _store.PublishedDocument(Id, strip));

            if (ConfirmPublish is null || !await ConfirmPublish(Publish)) return;

            PublishStage = Lang.Get("share-publishing");

            var document = _store.PublishedDocument(Id, Publish.StripConnect);
            var client = new CairnsClient(_http, session.Server);

            var result = await client.PublishAsync(
                session, document, Publish.Slug, Publish.IsPublic);

            // Recorded so the pack knows where it lives, and so the button can tell
            // "Shared" from "Publish changes" without asking the server.
            _store.SaveLink(Id, new PackLink
            {
                Role = PackRole.Author,
                Url = result.Url,
                Revision = result.Revision,
                Published = new PublishRecord
                {
                    Fingerprint = PackLink.Fingerprint(document),
                    Visibility = result.Visibility,
                    Connect = Publish.StripConnect ? "stripped" : "included",
                },
            });

            _log(Lang.Get("share-published", result.Url, result.Revision, result.Visibility));
            ReloadShare();
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
        finally
        {
            PublishStage = "";
            IsBusy = false;
        }
    }

    /// <summary>
    /// Asks whether the pack is still served where it was published, and records it if not.
    ///
    /// Only for a pack this machine believes is published: a pack with no publish record
    /// has nothing to reconcile, and one that has changed will be published anyway.
    /// </summary>
    private async Task ReconcileWithdrawalAsync(CairnsSession session)
    {
        if (_store.LoadLink(Id) is not { Published: not null, Url.Length: > 0 } link) return;

        var slug = link.Url[(link.Url.LastIndexOf('/') + 1)..];

        if (!await new CairnsClient(_http, session.Server)
                .IsWithdrawnAsync(session.Username, slug))
            return;

        _store.MarkWithdrawn(Id);
        ReloadShare();
        _log(Lang.Get("share-was-withdrawn", link.Url));
    }

    /// <summary>
    /// The stored session, signing in first if there is not one.
    ///
    /// The code goes in the same banner a launch uses, rather than in a window of its own:
    /// it is a short wait with one instruction, and a second modal in front of the one the
    /// user actually asked for is a poor trade. Returns null if the sign-in did not
    /// complete, having already said why.
    /// </summary>
    private async Task<CairnsSession?> SignInAsync()
    {
        if (CairnsSession.Load() is { } existing) return existing;

        var client = new CairnsClient(_http);
        var flow = await client.StartSignInAsync();

        PublishStage = Lang.Get("share-enter-code", flow.UserCode, flow.VerificationUri);
        _log(Lang.Get("share-sign-in-at", flow.VerificationUri, flow.UserCode));

        Browser.Open($"{flow.VerificationUri}?code={flow.UserCode}");

        try
        {
            var session = await client.AwaitSignInAsync(
                flow, new Progress<string>(s => PublishStage = Lang.Get("share-code-stage", flow.UserCode, s)));

            session.Save();
            _log(Lang.Get("share-signed-in", session.Server, session.Username));

            return session;
        }
        catch (CairnsException e)
        {
            Error = e.Message;
            return null;
        }
    }

    /// <summary>
    /// Compatible versions per mod, so opening a dropdown twice — including after the
    /// reload that follows pinning — costs nothing and cannot loop back into the network.
    /// </summary>
    private readonly Dictionary<string, List<ResolvedRelease>> _releaseCache =
        new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Pre-populates the cache with real releases, tags and dates and all.</summary>
    public void CacheReleases(string modId, IEnumerable<ResolvedRelease> releases) =>
        _releaseCache[CacheKey(modId)] = [.. releases];

    /// <summary>Pre-populates the cache, e.g. from a test or a warm-up.</summary>
    public void CacheReleaseChoices(string modId, IEnumerable<string> versions) =>
        _releaseCache[CacheKey(modId)] =
        [
            .. versions.Select(v => new ResolvedRelease(
                modId, v, $"{modId}_{v}.zip", "", 0, 0, MatchQuality.Exact, null)),
        ];

    /// <summary>Set by the view; asks which version to pin and returns whether one was chosen.</summary>
    public Func<PinVersionViewModel, Task<bool>>? ChoosePinnedVersion { get; set; }

    /// <summary>Set by the view; asks which worlds to bring in, and whether to bring any.</summary>
    public Func<WorldPickerViewModel, Task<bool>>? ChooseWorlds { get; set; }

    /// <summary>
    /// Whether the player's own install has any worlds this pack could take. Read each time
    /// it is asked rather than cached: somebody can play plain Vintage Story between opening
    /// the launcher and looking at this tab, and a button that was drawn from a stale answer
    /// is one that lies about what is there.
    /// </summary>
    public bool HasWorldsToImport =>
        InstalledWorlds.Scan(InstalledWorlds.DefaultSavesDir).Count > 0;

    /// <summary>
    /// Copies a world out of the player's own install into this pack.
    ///
    /// A pack has its own data path, so a world in a plain install is not reachable from the
    /// pack that holds the mods it was made with — and it generally cannot be opened without
    /// them. Offered here as well as at import because a pack that already exists has no
    /// other route to it, which included every pack imported before this existed.
    ///
    /// Copied, never moved: the original stays put, so plain Vintage Story keeps working and
    /// Cairn still never writes to that folder.
    /// </summary>
    [RelayCommand]
    private async Task ImportWorldsAsync()
    {
        if (ChooseWorlds is null) return;

        var picker = new WorldPickerViewModel(InstalledWorlds.DefaultSavesDir);

        if (picker.Worlds.Count == 0)
        {
            _log(Lang.Get("worldimport-none-to-bring"));
            return;
        }

        if (!await ChooseWorlds(picker)) return;

        _packData.EnsureDataPath(Id);
        var data = _packData.DataPathFor(Id);

        foreach (var world in picker.Chosen)
        {
            _log(Lang.Get("main-copying-world", world.Name, Bytes.Human(world.Size)));

            var copied = await InstalledWorlds.CopyIntoAsync(world, data);

            _log(copied.Copied
                ? Lang.Get("main-copied-world", world.Name)
                    : Lang.Get("main-copy-world-failed", world.Name, copied.Problem));
        }

        OnPropertyChanged(nameof(HasWorldsToImport));
    }

    private string CacheKey(string modId) => $"{modId}|{Manifest.GameVersion}";

    /// <summary>
    /// Asks which version of one mod to pin, then pins it.
    ///
    /// The releases are fetched when the pin is pressed rather than when the pack is shown:
    /// a twenty-mod pack would otherwise make twenty ModDB calls to answer a question
    /// nobody asked. Cached per mod and game version, so pressing pin twice — including
    /// after the reload that pinning causes — costs nothing.
    /// </summary>
    private async Task ChoosePinForRowAsync(ModRowViewModel row)
    {
        if (ChoosePinnedVersion is null) return;

        if (!_releaseCache.TryGetValue(CacheKey(row.ModId), out var releases))
        {
            row.LoadingReleases = true;

            try
            {
                releases = [.. await _moddb.ListCompatibleReleasesAsync(row.ModId, Manifest.GameVersion)];
                _releaseCache[CacheKey(row.ModId)] = releases;
            }
            catch (Exception e) when (e is ModDbException or HttpRequestException
                                          or System.Text.Json.JsonException)
            {
                Error = e.Message;
                return;
            }
            finally
            {
                row.LoadingReleases = false;
            }
        }

        var choice = new PinVersionViewModel(
            row.ModId, row.Name, row.Mod.Version, row.Locked?.Version, releases, Manifest.GameVersion);

        // Cancelled, or nothing to choose from: the mod is left exactly as it was, pin and
        // all. Removing one is the pin button on the row, not a row in this window.
        if (!await ChoosePinnedVersion(choice)) return;

        ApplyPin(row, choice.Result);
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
        row.PinChanged();

        _log(version is null
            ? Lang.Get("mods-unpinned", modId)
                : Lang.Get("mods-pinned", modId, version));
    }

    // ---- sync / launch / delete ----

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task Play()
    {
        _runs.Begin(Id, Lang.Get("play-checking-game"));

        try
        {
            await PlayCoreAsync();
        }
        finally
        {
            // Given up here only if nothing was started; a running game is let go of when
            // it exits, by whoever is watching it — which is not this pane.
            if (!_runs.IsRunning(Id)) _runs.Abandon(Id);
        }
    }

    /// <summary>What the pane's progress line says, kept where a rebuild cannot lose it.</summary>
    private void Stage(string stage) => _runs.Report(Id, stage);

    /// <summary>Offered only while there is a process to kill, not merely a launch underway.</summary>
    public bool CanForceQuit => _runs.IsRunning(Id);

    /// <summary>
    /// Play gives its slot up to Force quit once the game is up, rather than sitting there
    /// greyed out beside it. A disabled button says "not now"; what is true is that there
    /// is nothing left to press until the game goes away, and the only action worth
    /// offering is the one that makes it.
    /// </summary>
    public bool ShowPlay => !CanForceQuit;

    /// <summary>
    /// Still getting there — syncing, provisioning, starting the process. This is what the
    /// progress bar is for: once the game is up there is nothing in progress to report,
    /// and an indeterminate bar that never fills for the hours somebody is playing reads
    /// as the launcher being stuck.
    /// </summary>
    public bool IsStarting => IsLaunching && !CanForceQuit;

    /// <summary>
    /// The way out of a hung game.
    ///
    /// Behind a confirmation because it is not a quit — it is a kill, and whatever the game
    /// had not written to the save yet goes with it. It is still the right thing to offer:
    /// a game that has stopped drawing leaves the pack unplayable and the save held open,
    /// and the alternative is Activity Monitor.
    ///
    /// Nothing happens with no confirmer wired up: a destructive action that proceeds
    /// because the dialog is missing is the one failure mode this must not have.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanForceQuit))]
    private async Task ForceQuit()
    {
        if (Confirm is null) return;

        if (!await Confirm(new ConfirmViewModel(
                Lang.Get("play-force-quit-title"),
                Lang.Get("play-force-quit-body", Title),
                Lang.Get("play-force-quit-confirm"))))
            return;

        if (_runs.ForceQuit(Id)) _log(Lang.Get("play-forcing-quit"));
    }

    private async Task PlayCoreAsync()
    {
        if (ResolvedInstall is null)
        {
            await _provision(Manifest.GameVersion);
            RefreshGameState();

            if (ResolvedInstall is null)
            {
                Error = Lang.Get("play-prepare-failed", Manifest.GameVersion);
                return;
            }
        }

        Stage(Lang.Get("play-checking-mods"));

        var report = await RunSyncAsync();
        if (report is null || report.Failed)
        {
            Error = Lang.Get("play-sync-unclean");
            return;
        }

        try
        {
            var install = ResolvedInstall!;
            var launcher = new GameLauncher(install);

            // Prefer a runtime Cairn manages: for an older game version it may be the
            // only .NET of the right major on the machine.
            //
            // Said out loud when it happens: a pack seeded before mod paths were confined
            // has been loading the player's own Mods folder on top of its own, and "this
            // launch has fewer mods in it than the last one" is not something to discover
            // in-game.
            var bound = new List<string>();
            var config = new List<ModConfigChange>();

            foreach (var dropped in _packData.BeforeLaunch(Id, bound, config))
                _log(Lang.Get("play-dropped-mod-path", dropped));

            // A keyboard that changes behaviour without saying so is alarming in a way a
            // mod path is not. Only ever bindings the player had none of — see
            // ClientHotkeys — so this says what arrived, not what was taken.
            if (bound.Count > 0)
                _log(Lang.Plural("play-bound-hotkeys", bound.Count, bound.Count, string.Join(", ", bound)));

            // Every line, not a count. These change how the game plays, they are written
            // into files belonging to other people's mods, and the ones left alone are what
            // somebody needs to see to know why the pack is not behaving as its author's
            // copy does. Worded by Core so the CLI says the same thing.
            foreach (var change in config) _log(change.Describe());

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
                // For this install, not for the pack's version. They are the same question
                // only while a version has one install: a client built for this machine can
                // need a .NET of a different architecture than the stock download of the
                // same version, and provisioning the version answers for the stock one —
                // reporting the version ready while the client that will actually start
                // still has nothing to run on.
                if (_provisionRuntime is not null) await _provisionRuntime(install);
                else await _provision(Manifest.GameVersion);

                options.PreferredDotnetRoot = _runtimes.RootFor(install);
                runtime = launcher.ResolveRuntime(options);

                if (!runtime.Resolved)
                {
                    Error = Lang.Get("play-runtime-failed", install.Version, install.RequiredFramework);
                    return;
                }
            }

            Stage(Lang.Get("play-starting"));
            _log(Lang.Get("play-launching", string.Join(' ', launcher.BuildArguments(options))));

            var proc = launcher.Launch(options);
            _log(Lang.Get("play-started", proc.Id));

            // The game takes a while to put a window up. Keep saying so, and keep Play
            // disabled, until it actually exits. Handed to the registry, which outlives
            // this pane and so is still there to notice the exit.
            _runs.Track(Id, proc);
        }
        catch (Exception e)
        {
            Error = Lang.Get("play-failed", e.Message);
        }
    }

    /// <summary>
    /// Writes the pack as one shareable file. Including the lock is what makes the
    /// recipient reproduce this exact mod set rather than merely a similar one.
    ///
    /// Refused for a pack you follow, and not only for the same reason publishing is. An
    /// export carries the manifest and lock and nothing else — no canonical URL, no
    /// author — so a file made from somebody else's pack arrives at the next person as an
    /// unowned one they may publish freely. Handing out the link keeps it attributed;
    /// handing out a file launders it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
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
            _log(Lang.Get("packsettings-exported", Id, path));
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
    }

    /// <summary>Hands off to the shared confirmation rather than deleting outright.</summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    private void DeletePack() => _requestDelete(null);

    /// <summary>
    /// Where the pack's worlds, mod configs and settings live. Always its own directory;
    /// it appears on first launch.
    /// </summary>
    public string DataDirectory => _packData.DataPathFor(Id);

    // ---- hotkeys ----

    /// <summary>
    /// Every hotkey the pack's mods register, plus the game's own, with what the pack binds
    /// them to.
    ///
    /// The author reconciles the collisions once here and everybody who installs the pack
    /// gets the result on first launch. See <see cref="Cairn.Core.Hotkeys.HotkeyCatalog"/>
    /// for where the list comes from, and <see cref="Cairn.Core.Launch.ClientHotkeys"/> for
    /// what a launch does with it.
    /// </summary>
    public ObservableCollection<HotkeyRowViewModel> Hotkeys { get; } = [];

    /// <summary>
    /// Every row the pack has, filtered or not.
    ///
    /// <see cref="Hotkeys"/> is what the list shows and can be a fraction of this. Anything
    /// that answers a question about the pack — what clashes, what to save — asks this one:
    /// a filtered save would have quietly dropped the bindings somebody could not see.
    /// </summary>
    private readonly List<HotkeyRowViewModel> _allHotkeys = [];

    [ObservableProperty] public partial bool LoadingHotkeys { get; set; }

    /// <summary>Set once the tab has been opened, so seventy zips are not read on every selection.</summary>
    private bool _hotkeysLoaded;

    /// <summary>
    /// What the pack's files looked like when the rows were built — see
    /// <see cref="HotkeyCatalog.Stamp"/> — so a list read before a mod arrived is not still
    /// on screen after it did.
    ///
    /// Kept because the first thing anybody does with a new pack is add a mod to it, and
    /// the rows were read once per pack selection and never again: open the tab on an empty
    /// pack, add Packrat, come back, and its hotkey was not there. It was never read.
    /// </summary>
    private string? _hotkeysFrom;

    /// <summary>True while the rows are being built from the pack, rather than edited.</summary>
    private bool _adoptingHotkeys;

    [ObservableProperty] public partial string HotkeySummary { get; set; } = "";

    /// <summary>
    /// Narrows the list by name, id, mod or key. Forty hotkeys is a scroll; the one you are
    /// looking for is usually one you can already name.
    /// </summary>
    [ObservableProperty] public partial string HotkeySearch { get; set; } = "";

    /// <summary>
    /// Hides everything that is not part of a collision, which is the state somebody is in
    /// when they opened this tab to fix one.
    /// </summary>
    [ObservableProperty] public partial bool OnlyClashes { get; set; }

    partial void OnHotkeySearchChanged(string value) => RefreshHotkeyFilter();

    partial void OnOnlyClashesChanged(bool value)
    {
        OnPropertyChanged(nameof(NoHotkeysFoundLine));
        RefreshHotkeyFilter();
    }

    /// <summary>
    /// The row waiting for a keypress, which belongs to the window — this is who gets it.
    ///
    /// Read from the rows rather than held in a field, so it stays true however a row came
    /// to be waiting. Arming one stops any other (see <see cref="OnHotkeyArming"/>), so
    /// there is only ever the one to find.
    /// </summary>
    public HotkeyRowViewModel? CapturingRow => _allHotkeys.FirstOrDefault(r => r.Capturing);

    /// <summary>
    /// One capture at a time. Asking a second row for a key means you changed your mind
    /// about the first, not that both should be listening — and both listening is worse
    /// than it sounds, because the key lands on whichever comes first in the list.
    /// </summary>
    private void OnHotkeyArming(HotkeyRowViewModel row)
    {
        foreach (var other in _allHotkeys)
            if (!ReferenceEquals(other, row)) other.Capturing = false;
    }

    /// <summary>
    /// Reads the pack's mod zips on a background thread. Seventy archives is a second or so
    /// of disk and IL, which is fine to wait for and not fine to do on the UI thread.
    /// </summary>
    public async Task LoadHotkeysAsync(bool force = false)
    {
        var mods = _store.ModsDir(Id);
        var game = HotkeyCatalog.GameAssemblyIn(ResolvedInstall?.Directory);

        // The game's own assembly is part of the question: a pack whose version was not
        // installed when the tab was first opened has no vanilla rows, and the collisions
        // that matter most are with vanilla.
        var stamp = $"{HotkeyCatalog.Stamp(mods)}||{game}";

        if (_hotkeysLoaded && !force && stamp == _hotkeysFrom) return;

        _hotkeysLoaded = true;
        _hotkeysFrom = stamp;
        LoadingHotkeys = true;

        try
        {
            var result = await Task.Run(() => HotkeyCatalog.Read(mods, game));

            _allHotkeys.Clear();

            var declared = Manifest.Keybinds ?? [];

            _adoptingHotkeys = true;

            try
            {
                foreach (var entry in result.Entries)
                {
                    declared.TryGetValue(entry.Code, out var text);

                    _allHotkeys.Add(new HotkeyRowViewModel(
                        entry, KeyBinding.Parse(text),
                        changed: OnHotkeyEdited, arming: OnHotkeyArming));
                }
            }
            finally
            {
                _adoptingHotkeys = false;
            }

            RefreshHotkeyClashes();
            HotkeySummary = Summarise(result);
        }
        catch (Exception e)
        {
            // Retryable, because the flag is what stops a second attempt. A scan that fell
            // over on one unlucky file left the tab permanently blank otherwise: no rows,
            // no way back short of selecting another pack and returning.
            _hotkeysLoaded = false;
            HotkeySummary = Lang.Get("hotkeys-read-failed", e.Message);
        }
        finally
        {
            LoadingHotkeys = false;
        }
    }

    /// <summary>
    /// What the scan found, and what it did not.
    ///
    /// Three numbers and they mean different things. A hotkey with no readable default is
    /// still here and still bindable — it just shows a dash — so it belongs in the count of
    /// what is listed, not in the count of what is missing. Adding the two together, which
    /// this line used to do, told somebody that rows they could see were rows they could
    /// not, and the point of reporting a shortfall at all is that it is true.
    /// </summary>
    private static string Summarise(HotkeyCatalog.Result result)
    {
        var line = Lang.Plural("hotkeys-count", result.Entries.Count, result.Entries.Count);

        if (result.Keyless > 0)
            line += Lang.Get("hotkeys-keyless", result.Keyless);

        if (result.Unreadable > 0)
            line += result.Keyless > 0
                    ? Lang.Get("hotkeys-unreadable-and", result.Unreadable)
                    : Lang.Get("hotkeys-unreadable", result.Unreadable);

        return line;
    }

    /// <summary>
    /// Writes the pack the moment a binding moves.
    ///
    /// There was a Save button here, and it was the only thing in this pane with one:
    /// adding a mod, removing one and pinning a version all write on the click. Rebinding
    /// a key is the same kind of act — the click is the decision, there is no draft worth
    /// keeping — and the second step did exactly what a second step does. Edits sat unsaved,
    /// selecting another pack threw them away without a word, and the pack did not offer
    /// itself for publishing because as far as the disk was concerned nothing had happened.
    /// </summary>
    private void OnHotkeyEdited()
    {
        // Building the rows sets each one's binding, which is not somebody editing it. It
        // would also write a half-built set: a row reports its new value from inside its
        // own constructor, before the list it is being added to contains it, so the pack
        // would be saved missing whichever binding was arriving.
        if (_adoptingHotkeys) return;

        RefreshHotkeyClashes();

        // Over what the pack already declares, not instead of it. The rows are every hotkey
        // this scan could find, which is not every hotkey the manifest can name: the game's
        // own are missing until its version is installed, a mod that builds its registration
        // at runtime never produces one, and a pack whose Mods folder is mid-sync is short a
        // few. Rebuilding the dictionary from the rows deleted all of those the moment
        // somebody touched an unrelated key — silently, from the shared document, and most
        // reliably on a machine that had not downloaded the game yet.
        var bindings = new Dictionary<string, string>(
            Manifest.Keybinds ?? [], StringComparer.Ordinal);

        foreach (var row in _allHotkeys)
        {
            // A row with no binding is the pack declining to say anything about that
            // hotkey, which is a removal — Reset has to be able to take an entry out.
            if (row.Binding is null) bindings.Remove(row.Code);
            else bindings[row.Code] = row.Binding.ToString();
        }

        // Null rather than an empty object, so a pack that has never set one looks exactly
        // as it did before this existed — and reads as unchanged against what was published.
        Manifest.Keybinds = bindings.Count == 0 ? null : bindings;

        try
        {
            _store.Save(Manifest);
            Error = null;
        }
        catch (Exception e)
        {
            Error = e.Message;
            return;
        }

        // Not the full Persist: the mod list has not moved, and rebuilding it would send
        // this pack's rows back to ModDB for their names on every keystroke. What does have
        // to be recomputed is the Share button — hotkeys are part of the shared document,
        // so changing one is something to publish.
        ReloadShare();
    }

    /// <summary>
    /// Marks every row that fires on the same press as another.
    ///
    /// Over every row, not the visible ones: a hotkey hidden by a search still occupies its
    /// key, and a conflict list that depended on what was on screen would report a pack as
    /// clean because somebody typed in a box.
    ///
    /// Asked of the rows as they currently stand, because the answer changes as soon as
    /// somebody rebinds one — a list built from the mods' own defaults would keep reporting
    /// the clash the author has just fixed.
    ///
    /// The rule itself is <see cref="HotkeyClashes"/>, in Core. What lives here is only the
    /// marking up: which row gets a warning, and what the warning says. Held here once and
    /// it drifted — the copy in Core went on answering from the mods' defaults — which is
    /// the whole argument for a rule having one home.
    /// </summary>
    private void RefreshHotkeyClashes()
    {
        foreach (var row in _allHotkeys)
        {
            row.Clashes = false;
            row.ClashesWith = "";
            row.SharesHeldKey = false;
        }

        // Codes are unique across the rows — HotkeyCatalog deduplicates on them — so this
        // is how a clash's codes come back as the rows that hold them.
        var byCode = new Dictionary<string, HotkeyRowViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _allHotkeys) byCode.TryAdd(row.Code, row);

        var clashing = 0;

        var found = HotkeyClashes.Find(
            _allHotkeys.Select(r => new BoundHotkey(r.Code, r.Effective, r.IsGame)));

        foreach (var clash in found)
        {
            var rows = clash.Codes
                .Select(code => byCode.TryGetValue(code, out var row) ? row : null)
                .OfType<HotkeyRowViewModel>()
                .ToList();

            foreach (var row in rows)
            {
                row.ClashesWith = string.Join(
                    ", ", rows.Where(other => other != row).Select(other => other.Display));

                // A held key is named on the row and left out of the count. Shift and Ctrl
                // are held rather than pressed and sharing them is the design; counting
                // them would bury the five mods on P under eight rows about Shift.
                if (clash.Shared) { row.SharesHeldKey = true; continue; }

                row.Clashes = true;
                clashing++;
            }
        }

        HotkeyClashCount = clashing;
        RefreshHotkeyFilter();
    }

    /// <summary>
    /// Rebuilds the visible list from the full one.
    ///
    /// A search matches the label, the hotkey's id, the mod it came from and the key it is
    /// on — all four, because "what is on P?" and "what did CarryOn add?" are the same
    /// question asked from different ends.
    /// </summary>
    private void RefreshHotkeyFilter()
    {
        var term = HotkeySearch.Trim();

        // A term that names a key is a question about that key. "P" as a substring appears
        // in half the mod ids in a pack, which is no use at all to somebody asking what
        // else is bound to P — and that is the question this tab exists to answer.
        var asKey = KeyBinding.Parse(term);

        Hotkeys.Clear();

        foreach (var row in _allHotkeys)
        {
            // A row that no longer collides leaves the list. Under this filter the list is
            // the work remaining, and it should get shorter as the work gets done —
            // resolving a pair takes both of its rows away, which is the point.
            if (OnlyClashes && !row.Clashes) continue;
            if (term.Length > 0 && !Matches(row, term, asKey)) continue;

            Hotkeys.Add(row);
        }

        OnPropertyChanged(nameof(HotkeyListLine));
        OnPropertyChanged(nameof(ShowNoHotkeysFound));

        static bool Matches(HotkeyRowViewModel row, string term, KeyBinding? asKey) =>
            asKey is not null
                ? row.Effective is { } bound && bound.Clashes(asKey)
                : row.Display.Contains(term, StringComparison.OrdinalIgnoreCase)
                  || row.Code.Contains(term, StringComparison.OrdinalIgnoreCase)
                  || row.Source.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Says what is being hidden, so a short list is never mistaken for a small pack.</summary>
    public string HotkeyListLine =>
        _allHotkeys.Count == 0 || Hotkeys.Count == _allHotkeys.Count
            ? ""
            : Lang.Get("hotkeys-showing", Hotkeys.Count, _allHotkeys.Count);

    public bool ShowNoHotkeysFound => _allHotkeys.Count > 0 && Hotkeys.Count == 0;

    /// <summary>Why the list is empty, which is a different answer for each filter.</summary>
    public string NoHotkeysFoundLine => OnlyClashes && HotkeyClashCount == 0
        ? Lang.Get("hotkeys-none-collide")
            : Lang.Get("hotkeys-no-match");

    [ObservableProperty] public partial int HotkeyClashCount { get; set; }

    partial void OnHotkeyClashCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasHotkeyClashes));
        OnPropertyChanged(nameof(OnlyClashesLabel));
        OnPropertyChanged(nameof(NoHotkeysFoundLine));
    }

    public bool HasHotkeyClashes => HotkeyClashCount > 0;

    /// <summary>
    /// The conflicts filter says how many there are, so there is no banner saying the same
    /// thing above a list that is about to show them. One place, and it is the control you
    /// would reach for next.
    /// </summary>
    public string OnlyClashesLabel => HotkeyClashCount == 0
        ? Lang.Get("hotkeys-only-conflicts")
            : Lang.Get("hotkeys-only-conflicts-count", HotkeyClashCount);

    /// <summary>
    /// Takes a keypress for whichever row is waiting, and returns whether it wanted one.
    /// Called by the window: the keyboard belongs to the view.
    /// </summary>
    public bool CaptureHotkey(int keyCode, bool ctrl, bool alt, bool shift)
    {
        if (CapturingRow is not { } row) return false;

        row.Capturing = false;

        // Escape leaves the binding alone. It is the one key somebody presses meaning
        // "not this", and binding it would be the last thing they intended.
        if (keyCode == EscapeKey) return true;

        row.Binding = new KeyBinding(keyCode, ctrl, alt, shift);
        return true;
    }

    private static readonly int EscapeKey = KeyBinding.Parse("Escape")!.KeyCode;

    /// <summary>Stops waiting for a key, for a click somewhere else or a tab change.</summary>
    public void CancelHotkeyCapture()
    {
        if (CapturingRow is { } row) row.Capturing = false;
    }

    /// <summary>
    /// Resolves the pack against ModDB, downloads what is missing, removes what is no
    /// longer wanted, and writes the lockfile.
    ///
    /// Play is its only caller now that the separate sync button is gone. It is not dead
    /// code — it is the first half of launching, and dropping it would leave Play
    /// starting the game with whatever happened to be on disk.
    /// </summary>
    /// <param name="quiet">
    /// For a sync the user did not ask for. It still logs and still installs, but a failure
    /// does not raise the pane's error banner: the action they took — adding a mod — did
    /// work, the download is what did not, and Play will say so properly when it retries.
    /// Nor does it clear an error that was already showing.
    /// </param>
    private async Task<SyncReport?> RunSyncAsync(bool quiet = false)
    {
        IsBusy = true;
        if (!quiet) Error = null;

        try
        {
            var syncer = new PackSyncer(_moddb, _http);
            var progress = new Progress<SyncStep>(s =>
            {
                _log(Format(s));
                if (IsLaunching) Stage(Lang.Get("play-mod-stage", s.ModId, s.Detail));

                // The row stops saying "downloading…" when sync has actually reached it,
                // rather than when the whole run finishes.
                var row = Mods.FirstOrDefault(
                    m => string.Equals(m.ModId, s.ModId, StringComparison.OrdinalIgnoreCase));
                if (row is not null) row.Downloading = false;
            });

            var report = await syncer.SyncAsync(
                Manifest, _store.ModsDir(Id), _store.LockPath(Id), progress);

            if (report.Failed)
            {
                if (quiet) _log(Lang.Get("sync-failed-quiet"));
                else Error = Lang.Get("sync-failed");
            }

            return report;
        }
        catch (Exception e)
        {
            if (quiet) _log(Lang.Get("sync-background-failed", e.Message));
            else Error = e.Message;
            return null;
        }
        finally
        {
            IsBusy = false;
            ReloadMods();
        }
    }

    // ---- Mod config ----

    /// <summary>
    /// The mod settings this pack could carry, filtered by the search box.
    ///
    /// Two mods often need a line in one of their config files before they work together;
    /// the author sorts that out once, in game, and without somewhere to put the answer
    /// everybody who installs the pack finds out the hard way. This is that somewhere —
    /// <see cref="ModConfigSurvey"/> works out what they changed, and
    /// <see cref="ModConfigFiles"/> puts it back on everybody else's machine.
    /// </summary>
    public ObservableCollection<ModConfigRowViewModel> ModConfigSettings { get; } = [];

    /// <summary>Every row the survey returned. <see cref="ModConfigSettings"/> is the visible part.</summary>
    private readonly List<ModConfigRowViewModel> _allModConfig = [];

    /// <summary>True while the rows are being built from the pack, rather than ticked.</summary>
    private bool _adoptingModConfig;

    [ObservableProperty] public partial string ModConfigSearch { get; set; } = "";

    /// <summary>
    /// Every readable setting rather than only the changed ones.
    ///
    /// The way out of the one thing the baseline cannot see: a value changed during the very
    /// first session was already in the file before anything observed it, so it never reads
    /// as changed. An author who knows they moved it needs to be able to find it anyway.
    /// </summary>
    [ObservableProperty] public partial bool ShowAllSettings { get; set; }

    partial void OnModConfigSearchChanged(string value) => RefreshModConfigFilter();

    /// <summary>
    /// A filter over rows already read, not a reason to read them again.
    ///
    /// It was a reload at first, and that was wrong twice over: it silently unticked whatever
    /// somebody had just chosen — and, because ticking saves, wrote that to the pack — and it
    /// left the tab unable to tell "nothing has changed" from "this pack's mods have written
    /// nothing at all", which are opposite things to say to somebody.
    /// </summary>
    partial void OnShowAllSettingsChanged(bool value) => RefreshModConfigFilter();

    /// <summary>
    /// Reads the pack's config files and builds the rows.
    ///
    /// Synchronous, unlike the hotkey scan, and for a reason rather than by omission. That
    /// one opens seventy zip archives and walks IL; this one reads small JSON files already
    /// on disk, bounded per file by <see cref="ModConfigFiles.MaxFileToSurvey"/>. Doing it on
    /// a background thread would buy nothing and cost the race worth avoiding most — a reload
    /// landing on top of somebody who has already started ticking.
    /// </summary>
    /// <param name="adopting">
    /// The manifest was just replaced from outside this tab — by taking an author's revision
    /// — so what it declares is newer than what the files hold, and must not be written back
    /// over from them. The pack's own values reach the files at the next launch; until then
    /// they legitimately disagree.
    /// </param>
    public void LoadModConfig(bool adopting = false)
    {
        _allModConfig.Clear();
        _adoptingModConfig = true;

        try
        {
            // Everything, always, and filtered for display. Reading only the changed ones
            // would make Show all a second trip to disk, and would leave the empty tab unable
            // to say which nothing it is looking at.
            var settings = Survey();

            foreach (var setting in settings)
                _allModConfig.Add(new ModConfigRowViewModel(setting, OnModConfigEdited));

            _modConfigSignature = Signature(settings);
            ModConfigError = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ModConfigError = Lang.Get("modconfig-unreadable", e.Message);
        }
        finally
        {
            _adoptingModConfig = false;
        }

        RefreshModConfigFilter();
        OnPropertyChanged(nameof(ModConfigSummary));

        if (!adopting) CarryCurrentValues();
    }

    /// <summary>
    /// Brings the pack's declared values back into line with the files, for the keys it
    /// already carries.
    ///
    /// A tick says "this value travels with the pack". It used to be read as "this value, as
    /// it stood the moment you ticked it": the tick wrote the manifest and nothing else ever
    /// did, so changing the setting afterwards updated every row on screen and left the pack
    /// declaring the old number. Publishing then shipped it. The way out was to untick the
    /// row and tick it again, which nobody would guess and which nothing on screen suggested
    /// — the tab showed the new value beside a ticked box, which was simply untrue.
    ///
    /// Silent, like ticking is, and visible in the same place: mod config is part of the
    /// shared document, so a pack that has moved says it has unpublished changes.
    ///
    /// Only for keys already carried. An untouched setting is not adopted by having been
    /// looked at — <see cref="OnModConfigEdited"/> is still the only thing that adds one.
    /// </summary>
    private void CarryCurrentValues()
    {
        var carried = _allModConfig.Where(r => r.Carried).Select(r => r.Setting).ToList();
        if (carried.Count == 0 && Manifest.ModConfig is null) return;

        var wanted = ModConfigSurvey.ToManifest(carried);
        if (SameModConfig(wanted, Manifest.ModConfig)) return;

        Manifest.ModConfig = wanted;

        try
        {
            _store.Save(Manifest);
            Error = null;
        }
        catch (Exception e)
        {
            Error = e.Message;
            return;
        }

        OnPropertyChanged(nameof(ModConfigSummary));
        ReloadShare();
    }

    /// <summary>
    /// Whether two mod config sections say the same thing. Compared per file with DeepEquals,
    /// since the value is a sparse object and two of them differing anywhere is a difference
    /// — the same comparison PackUpdatePlan makes for the same reason.
    /// </summary>
    private static bool SameModConfig(
        IReadOnlyDictionary<string, JsonObject>? a, IReadOnlyDictionary<string, JsonObject>? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Count != b.Count) return false;

        foreach (var (file, value) in a)
            if (!b.TryGetValue(file, out var other) || !JsonNode.DeepEquals(value, other))
                return false;

        return true;
    }

    [ObservableProperty] public partial string? ModConfigError { get; set; }

    private FileSystemWatcher? _modConfigWatcher;

    /// <summary>
    /// Which reload is the current one. A save is several file events — editors write a temp
    /// file and rename over the original, and the game rewrites every config at once — so the
    /// last one to arrive wins and the ones before it are dropped rather than each causing a
    /// pass over the folder.
    /// </summary>
    private int _modConfigTick;

    /// <summary>
    /// What the files said when the list was last built, so a write that changes nothing this
    /// tab shows does not rebuild it. The game rewrites every config file on exit; without
    /// this, coming back from a session would reset the scroll position of a list where
    /// nothing had moved.
    /// </summary>
    private string _modConfigSignature = "";

    /// <summary>
    /// Watches the pack's config folder while the tab is open, so a value changed in an
    /// editor — or by the game, or by ConfigLib's own settings screen — shows up without
    /// having to leave the tab and come back.
    ///
    /// ConfigLib watches these same files for the same reason, which is a fair sign that a
    /// watcher here is not going to surprise it.
    /// </summary>
    private void WatchModConfig()
    {
        if (_modConfigWatcher is not null) return;

        var folder = ModConfigFolder;

        // Nothing to watch, and nothing to show either — the tab says to play the pack once.
        // Re-attempted whenever the tab is opened, and after the folder button makes one.
        if (!Directory.Exists(folder)) return;

        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                // XLeveling and friends keep their settings a level down.
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };

            watcher.Changed += OnModConfigChanged;
            watcher.Created += OnModConfigChanged;
            watcher.Deleted += OnModConfigChanged;
            watcher.Renamed += OnModConfigChanged;

            // Not Error: a watcher that dies takes the live updating with it and leaves the
            // tab working exactly as it did before this existed, which is not worth a message.
            watcher.EnableRaisingEvents = true;
            _modConfigWatcher = watcher;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or PlatformNotSupportedException or ArgumentException)
        {
            _modConfigWatcher = null;
        }
    }

    private void StopWatchingModConfig()
    {
        var watcher = _modConfigWatcher;
        _modConfigWatcher = null;

        if (watcher is null) return;

        watcher.EnableRaisingEvents = false;
        watcher.Changed -= OnModConfigChanged;
        watcher.Created -= OnModConfigChanged;
        watcher.Deleted -= OnModConfigChanged;
        watcher.Renamed -= OnModConfigChanged;
        watcher.Dispose();
    }

    /// <summary>
    /// Arrives on a thread pool thread, from outside the application entirely. Everything it
    /// touches after the delay is on the UI thread.
    /// </summary>
    private void OnModConfigChanged(object? sender, FileSystemEventArgs e)
    {
        var mine = Interlocked.Increment(ref _modConfigTick);

        _ = Task.Delay(TimeSpan.FromMilliseconds(400)).ContinueWith(_ =>
        {
            // A later write landed while this one was waiting. That one will do the reload.
            if (Volatile.Read(ref _modConfigTick) != mine) return;

            Dispatcher.UIThread.Post(ReloadModConfigIfChanged);
        }, TaskScheduler.Default);
    }

    private void ReloadModConfigIfChanged()
    {
        // Left the tab while the delay was running, or the pack was closed under it.
        if (SelectedTab != ModConfigTab || _modConfigWatcher is null) return;

        try
        {
            if (Signature(Survey()) == _modConfigSignature) return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Mid-write, most likely. The next event reloads.
            return;
        }

        LoadModConfig();
    }

    private IReadOnlyList<ModConfigSetting> Survey() =>
        ModConfigSurvey.Read(_packData.DataPathFor(Id), Manifest.ModConfig, includeUnchanged: true);

    /// <summary>
    /// What the files say, as one comparable string. Values only — whether a row is ticked
    /// comes from the manifest, which nothing outside this window changes.
    /// </summary>
    private static string Signature(IEnumerable<ModConfigSetting> settings) =>
        string.Join('\n', settings.Select(s => $"{s.File} {s.Key} {s.CurrentText}"));

    public void Dispose() => StopWatchingModConfig();

    /// <summary>
    /// This pack's mod config folder. A property rather than only a command so a test can say
    /// where the button goes without a file manager opening on the machine running it.
    /// </summary>
    public string ModConfigFolder => ModConfigFiles.DirectoryIn(_packData.DataPathFor(Id));

    /// <summary>
    /// Opens that folder, which is otherwise buried at
    /// <c>~/.cairn/packs/&lt;id&gt;/data/ModConfig</c> — a path nobody should have to know,
    /// least of all somebody who has just been told a setting cannot be carried and wants to
    /// look at the file themselves.
    ///
    /// Created if it is not there. A pack that has never been launched has no such folder,
    /// and a button that silently does nothing is worse than an empty window: the game makes
    /// this directory itself on first run, so making it early costs nothing and means the
    /// button always does what it says.
    /// </summary>
    [RelayCommand]
    private void OpenModConfigFolder()
    {
        var folder = ModConfigFolder;

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Say it below, with the same line as a folder that would not open.
        }

        // The folder may only just have come into existence, which is the one case the tab
        // could not have started watching it on the way in.
        WatchModConfig();

        if (!Files.OpenFolder(folder)) _log(Lang.Get("modconfig-open-failed", folder));
    }

    private void RefreshModConfigFilter()
    {
        var term = ModConfigSearch.Trim();

        ModConfigSettings.Clear();

        foreach (var row in _allModConfig)
        {
            // What the author changed, plus what the pack already says — which together are
            // the rows somebody came here to act on. Everything else is behind Show all.
            if (!ShowAllSettings && !row.IsChanged && !row.Carried) continue;

            if (term.Length > 0
                && !row.Key.Contains(term, StringComparison.OrdinalIgnoreCase)
                && !row.File.Contains(term, StringComparison.OrdinalIgnoreCase))
                continue;

            ModConfigSettings.Add(row);
        }

        OnPropertyChanged(nameof(ModConfigListLine));
        OnPropertyChanged(nameof(ShowNoModConfigFound));
        OnPropertyChanged(nameof(NoModConfigFoundLine));
    }

    public string ModConfigListLine =>
        _allModConfig.Count == 0 || ModConfigSettings.Count == _allModConfig.Count
            ? ""
            : Lang.Get("modconfig-showing", ModConfigSettings.Count, _allModConfig.Count);

    public bool ShowNoModConfigFound => ModConfigSettings.Count == 0;

    /// <summary>Why the list is empty, which is a different answer for each reason.</summary>
    public string NoModConfigFoundLine
    {
        get
        {
            // Nothing readable at all, which for a pack nobody has played yet is the
            // ordinary state rather than a problem — the mods write these files when they
            // first run. Distinct from having settings and none of them matching.
            if (_allModConfig.Count == 0) return Lang.Get("modconfig-none-written");

            if (ModConfigSearch.Trim().Length > 0) return Lang.Get("modconfig-none-match");

            // A pack that has not been launched since Cairn started keeping a baseline. Not
            // the same as nothing having changed, and saying so would be a lie about
            // somebody's own pack — every existing pack lands here on first upgrade, with a
            // config folder full of values whose history nothing recorded.
            if (!_allModConfig.Any(r => r.Setting.HasBaseline))
                return Lang.Get("modconfig-no-baseline");

            return Lang.Get("modconfig-none-changed");
        }
    }

    public string ModConfigSummary
    {
        get
        {
            var carried = _allModConfig.Count(r => r.Carried);

            // Plural rather than a ternary on "s": that ternary is a rule about English
            // baked into a string nobody could translate around.
            return carried == 0
                ? Lang.Get("modconfig-intro")
                : Lang.Plural("modconfig-carried", carried, carried);
        }
    }

    /// <summary>
    /// Writes the ticked rows into the pack.
    ///
    /// Rebuilt from the rows rather than merged over what the manifest already says — the
    /// opposite of the hotkey tab, and deliberately. A hotkey row exists only where the scan
    /// could read a registration, so rebuilding there would drop the ones it could not; every
    /// row here comes from a file that is present, and a key the manifest names that no file
    /// has gets a row of its own. So the rows are the whole truth, and rebuilding is what
    /// lets unticking take an entry out.
    /// </summary>
    private void OnModConfigEdited()
    {
        // Building the rows sets each one's tick, which is not somebody choosing it.
        if (_adoptingModConfig) return;

        Manifest.ModConfig = ModConfigSurvey.ToManifest(
            _allModConfig.Where(r => r.Carried).Select(r => r.Setting));

        try
        {
            _store.Save(Manifest);
            Error = null;
        }
        catch (Exception e)
        {
            Error = e.Message;
            return;
        }

        OnPropertyChanged(nameof(ModConfigSummary));

        // Mod config is part of the shared document, so ticking one is something to publish.
        // Not the full Persist: the mod list has not moved.
        ReloadShare();
    }

    private static string Format(SyncStep s)
    {
        var marker = s.Action switch
        {
            SyncAction.Downloaded => Lang.Get("sync-added"),
                SyncAction.Updated => Lang.Get("sync-updated"),
                SyncAction.Removed => Lang.Get("sync-removed"),
                SyncAction.Unchanged => Lang.Get("sync-ok"),
                SyncAction.Warned => Lang.Get("sync-warning"),
                _ => Lang.Get("sync-failed-marker"),
        };

        return $"{marker,-8} {s.ModId,-24} {s.Detail}";
    }
}
