using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cairn.Core;
using Cairn.Core.Games;
using Cairn.Core.Launch;
using Cairn.Core.Runtime;
using Cairn.Core.ModDb;
using Cairn.Core.Packs;
using Cairn.Core.Updates;

namespace Cairn.App.ViewModels;

/// <summary>A pack as listed in the sidebar.</summary>
public class PackListItemViewModel(PackManifest manifest) : ViewModelBase
{
    public PackManifest Manifest { get; } = manifest;

    public string Id => Manifest.Id;
    public string Display => Manifest.Name ?? Manifest.Id;

    public string Subtitle =>
        $"game {Manifest.GameVersion}  ·  {Manifest.Mods.Count} mod{(Manifest.Mods.Count == 1 ? "" : "s")}";

    /// <summary>
    /// Shown only when the pack has a server. "connect" decides whether launching jumps
    /// straight into one — it does not restrict the pack to that server, and a pack
    /// without it is not a singleplayer pack: it opens at the main menu, from which
    /// multiplayer is as available as it ever was.
    /// </summary>
    public bool HasServer => !string.IsNullOrWhiteSpace(Manifest.Connect);

    public string ServerLine => HasServer ? $"auto-joins {Manifest.Connect}" : "";

    /// <summary>
    /// The detail pane edits this same manifest instance, and these are computed getters
    /// with nothing to raise a change for them — so a rename or an added mod left the
    /// sidebar showing stale text until the list was rebuilt from disk.
    /// </summary>
    public void Changed()
    {
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(HasServer));
        OnPropertyChanged(nameof(ServerLine));
    }
}

public partial class MainViewModel : ViewModelBase
{
    private readonly HttpClient _http;
    private readonly ModDbClient _moddb;
    private readonly PackStore _store;
    private readonly PackData _packData;
    private readonly GameStore _gameStore;
    private readonly GameLibrary _library;
    private readonly RuntimeStore _runtimes;
    private readonly GameProvisioner _provisioner;
    private CancellationTokenSource? _provisionCts;
    private readonly GameInstall? _install;

    /// <summary>
    /// <paramref name="handler"/> exists so tests can run offline. Pack rows fetch their
    /// names and icons from ModDB as soon as a pack is shown, which would otherwise make
    /// the whole app suite depend on the network being up.
    /// </summary>
    public MainViewModel(HttpMessageHandler? handler = null)
    {
        _http = (handler is null ? new HttpClient() : new HttpClient(handler));
        _http.Timeout = TimeSpan.FromMinutes(5);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("cairn/0.1");
        _moddb = new ModDbClient(_http);
        _store = new PackStore();
        _packData = new PackData(_store);
        _gameStore = new GameStore();
        _install = GameInstall.TryLocate();
        _library = new GameLibrary(_gameStore, _install);
        _runtimes = new RuntimeStore();
        _provisioner = new GameProvisioner(_http, _gameStore, _runtimes);

        Games = new GamesViewModel(
            _http, _gameStore, _runtimes, Note, onLibraryChanged: OnLibraryChanged,
            system: _install,
            // Removing a version a pack targets is allowed — it is disk space, and Play
            // fetches it again — but it should say so first.
            packsUsing: version => Packs
                .Where(p => p.Manifest.GameVersion == version)
                .Select(p => p.Display)
                .ToList());

        NewPackGameVersion = _install?.Version is { } v and not "unknown" ? v : "1.22.5";

        // Populate at construction as well as on open: the ComboBox is part of the window
        // from the start, and binding it against an empty collection makes it coerce its
        // selection to null before the form is ever shown.
        PopulateInstalledVersions();

        LoadPacks();
    }

    public ObservableCollection<PackListItemViewModel> Packs { get; } = [];

    /// <summary>
    /// One log per pack, kept for the session. The Log tab lives inside a pack, so a
    /// single shared collection showed every pack's launches under all of them. Keyed
    /// here rather than held by the detail view model, which is rebuilt on every
    /// selection change and would lose the history each time you switched away.
    /// </summary>
    private readonly Dictionary<string, ObservableCollection<string>> _logs =
        new(StringComparer.OrdinalIgnoreCase);

    private ObservableCollection<string> LogFor(string packId) =>
        _logs.TryGetValue(packId, out var log) ? log : _logs[packId] = [];

    public GamesViewModel Games { get; }

    [ObservableProperty] public partial PackListItemViewModel? SelectedPack { get; set; }
    [ObservableProperty] public partial PackDetailViewModel? Detail { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool Provisioning { get; set; }

    /// <summary>Which game version is being fetched, so a pack can tell whether the work
    /// in progress is its own.</summary>
    [ObservableProperty] public partial string? ProvisioningVersion { get; set; }
    [ObservableProperty] public partial string ProvisionStatus { get; set; } = "";
    [ObservableProperty] public partial double ProvisionFraction { get; set; }

    /// <summary>
    /// True while the current step cannot say how far along it is — unpacking, or running
    /// the Windows installer, which takes minutes and reports nothing. Collapsing a
    /// missing fraction to zero left the bar sitting at empty, looking hung.
    /// </summary>
    [ObservableProperty] public partial bool ProvisionIndeterminate { get; set; } = true;

    // ---- new-pack form ----

    [ObservableProperty] public partial bool IsImporting { get; set; }

    // ---- delete confirmation ----

    [ObservableProperty] public partial bool ConfirmingDelete { get; set; }

    /// <summary>Named in the prompt so it is obvious which pack is about to go.</summary>
    public string DeleteTargetName => SelectedPack?.Display ?? "";

    /// <summary>
    /// What deleting this pack actually destroys, itemised, with what the disk gets back.
    ///
    /// Measured rather than described: this is the last thing read before something
    /// irreversible, and "and its downloaded mods?" is not enough to decide on when the
    /// answer might be several gigabytes and a world someone has played for a month.
    /// </summary>
    public string DeleteConsequence
    {
        get
        {
            if (SelectedPack is null) return "";

            var contents = PackContents.Of(_store, SelectedPack.Id);
            var lines = contents.Describe();

            var body = lines.Count == 0
                ? "There is nothing downloaded under it yet."
                : "This deletes:\n" + string.Join("\n", lines.Select(l => "  • " + l));

            return $"{body}\n\nFrees {Bytes.Human(contents.TotalBytes)}. This cannot be undone.";
        }
    }

    public bool CanDeleteSelected => SelectedPack is not null;

    /// <summary>
    /// Shows a confirmation and returns whether to go ahead. Supplied by the view; when
    /// absent — headless tests — the prompt is left armed on ConfirmingDelete instead.
    /// </summary>
    public Func<ConfirmViewModel, Task<bool>>? Confirm { get; set; }

    /// <summary>
    /// Deleting removes the pack, every mod zip under it and its worlds, so it asks first.
    ///
    /// In a dialog, not in the page: this prompt lives at the bottom of a scrolling tab,
    /// where arming it rendered the warning below the fold — the one place a destructive
    /// prompt must never be.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private async Task RequestDelete()
    {
        OnPropertyChanged(nameof(DeleteTargetName));
        OnPropertyChanged(nameof(DeleteConsequence));

        if (Confirm is null)
        {
            ConfirmingDelete = true;
            return;
        }

        var confirmed = await Confirm(new ConfirmViewModel(
            $"Delete “{DeleteTargetName}”?", DeleteConsequence, "Delete pack"));

        if (confirmed) ConfirmDelete();
    }

    [RelayCommand]
    private void CancelDelete() => ConfirmingDelete = false;

    // ---- a newer Cairn ----

    /// <summary>
    /// The update this machine has been offered but not yet answered. Held so a headless
    /// run can inspect it, exactly as the delete prompt is.
    /// </summary>
    public UpdateAvailable? OfferedUpdate { get; private set; }

    /// <summary>
    /// How often somebody looks at the clock — not how often the server is asked, which
    /// <see cref="UpdateChecker.CheckInterval"/> owns, nor how often one release is raised,
    /// which <see cref="UpdateChecker.NotifyInterval"/> owns.
    ///
    /// Checking only at startup meant a launcher left open all week never heard about the
    /// release that happened on Tuesday, and this one is left open: it is the thing you
    /// press Play from. A tick that almost always reads one small file and returns is the
    /// cheapest way to make those intervals mean what they say.
    ///
    /// Half the check interval, so a check lands within about an hour of falling due
    /// rather than up to a whole one late. Anything shorter buys nothing: the checker
    /// itself is what decides whether the tick becomes a request.
    /// </summary>
    public static readonly TimeSpan UpdatePollInterval = TimeSpan.FromHours(1);

    private DispatcherTimer? _updateTimer;
    private bool _checkingForUpdate;

    /// <summary>
    /// Checks now, and keeps checking for as long as the app is open.
    ///
    /// Started by the app rather than the constructor so the test suite — which builds
    /// this view model hundreds of times — never starts a timer or touches the network.
    /// </summary>
    public void StartUpdateChecks()
    {
        _ = CheckForUpdateAsync();

        _updateTimer = new DispatcherTimer(
            UpdatePollInterval, DispatcherPriority.Background, (_, _) => _ = CheckForUpdateAsync());

        _updateTimer.Start();

        // The same shape for the pack an author may have republished. Selecting a pack is
        // the natural trigger and covers most of it, but a launcher left open on one pack
        // all afternoon would never look again — and that is exactly the launcher somebody
        // is about to press Play on.
        _packUpdateTimer = new DispatcherTimer(
            PackUpdatePollInterval, DispatcherPriority.Background,
            (_, _) => _ = Detail?.CheckForPackUpdateAsync() ?? Task.CompletedTask);

        _packUpdateTimer.Start();
    }

    private DispatcherTimer? _packUpdateTimer;

    /// <summary>
    /// How often the open pack is looked at — not how often its author is asked, which
    /// <see cref="PackUpdateCheck.CheckInterval"/> owns and is two hours.
    ///
    /// Shorter than that interval on purpose, for the reason the app's own poll is: a tick
    /// no more frequent than the interval turns "every two hours" into "every two to four",
    /// because the two drift against each other. An hourly tick that almost always reads
    /// one small file and returns is what makes the interval mean what it says.
    /// </summary>
    public static readonly TimeSpan PackUpdatePollInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// Asks whether there is a newer Cairn, and offers it once if so.
    ///
    /// Fire and forget from startup: it runs behind the window, does nothing at all on
    /// most launches — the check is due once a day — and cannot fail in a way worth
    /// telling anybody about. The dialog is the ordinary confirmation, because "here is a
    /// thing, do you want it" is what that window already is; a second window would exist
    /// only to be a different shape.
    /// </summary>
    /// <param name="checker">
    /// Supplied by tests. The real one reads this build's stamped version, which under a
    /// test host is "dev" — the case that correctly suppresses everything, and so the one
    /// case in which the dialog can never be exercised.
    /// </param>
    public async Task CheckForUpdateAsync(UpdateChecker? checker = null, CancellationToken ct = default)
    {
        // The timer fires on a schedule of its own and the dialog is modal, so a tick can
        // land while the last one is still on screen. The state file would refuse the
        // second offer anyway — the version is recorded before the dialog opens — but a
        // second HTTP request nobody will read is still waste.
        if (_checkingForUpdate) return;
        _checkingForUpdate = true;

        try
        {
            var update = await (checker ?? new UpdateChecker(_http)).CheckAsync(ct).ConfigureAwait(true);
            if (update is null) return;

            OfferedUpdate = update;

            // The status bar rather than a pack's log: this is about the app, and it
            // belongs to no pack.
            Note($"Cairn {update.Version} is available");

            if (Confirm is null) return;

            var wanted = await Confirm(new ConfirmViewModel(
                $"Cairn {update.Version} is available",
                $"You are running {CairnVersion.Current}. "
                + (update.File is null
                    ? "The download page has the build for your machine."
                    : $"The {update.File.Label} build is {update.File.SizeText}.")
                + "\n\nNothing is installed for you — this opens the download in your browser, "
                + "and your packs, worlds and settings are untouched by replacing the app.",
                update.ButtonLabel));

            if (wanted) Browser.Open(update.DownloadUrl);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // Checking is the least important thing this app does.
        }
        finally
        {
            _checkingForUpdate = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    public void ConfirmDelete()
    {
        var pack = SelectedPack!;
        ConfirmingDelete = false;

        try
        {
            _store.Delete(pack.Id);
            // Otherwise a new pack created under the same id inherits its history.
            _logs.Remove(pack.Id);
            Note($"deleted pack '{pack.Id}' and its downloaded mods");
            LoadPacks();
        }
        catch (Exception e)
        {
            Note($"could not delete '{pack.Id}': {e.Message}");
        }
    }

    // ---- import form ----

    [ObservableProperty] public partial string ImportText { get; set; } = "";
    [ObservableProperty] public partial string ImportAsId { get; set; } = "";
    [ObservableProperty] public partial bool ImportReproduce { get; set; } = true;
    [ObservableProperty] public partial string? ImportError { get; set; }
    [ObservableProperty] public partial bool ImportBusy { get; set; }

    public bool HasImportError => !string.IsNullOrEmpty(ImportError);

    partial void OnImportErrorChanged(string? value) => OnPropertyChanged(nameof(HasImportError));

    partial void OnImportTextChanged(string value)
    {
        ImportError = null;
        ImportPackCommand.NotifyCanExecuteChanged();
    }

    partial void OnImportBusyChanged(bool value) => ImportPackCommand.NotifyCanExecuteChanged();

    partial void OnIsImportingChanged(bool value) => RefreshPaneState();
    [ObservableProperty] public partial bool IsCreating { get; set; }
    [ObservableProperty] public partial string NewPackName { get; set; } = "";

    /// <summary>
    /// The id the pack will be saved under, derived from the name. Shown rather than
    /// asked for: it is a directory name and it travels in shared bundles, which is the
    /// machine's problem, not something to make someone invent a second time.
    /// </summary>
    public string NewPackSlug => _store.SuggestId(NewPackName);

    public bool HasNewPackSlug => !string.IsNullOrWhiteSpace(NewPackName);
    [ObservableProperty] public partial string NewPackGameVersion { get; set; }

    /// <summary>
    /// Versions offered in the new-pack form: what is installed, then everything the
    /// catalog publishes. Typing a version string from memory is not a reasonable ask.
    /// </summary>
    public ObservableCollection<string> GameVersionChoices { get; } = [];

    [ObservableProperty] public partial bool LoadingVersions { get; set; }
    [ObservableProperty] public partial string NewPackConnect { get; set; } = "";
    [ObservableProperty] public partial string? NewPackError { get; set; }

    public bool HasNewPackError => !string.IsNullOrEmpty(NewPackError);

    public bool HasPacks => Packs.Count > 0;

    public bool HasSelection => Detail is not null;

    // Avalonia has no multi-condition IsVisible without a converter, so the three
    // mutually exclusive states of the right-hand pane are computed here.
    // Provisioning takes over the pane: editing a pack whose game is mid-download invites
    // changes that the in-flight install knows nothing about.
    public bool ShowProvisioning => Provisioning;
    public bool ShowImport => IsImporting && !Provisioning;
    public bool ShowCreate => IsCreating && !IsImporting && !Provisioning;
    public bool ShowDetail => Detail is not null && !IsCreating
                              && !IsImporting && !Provisioning;
    public bool ShowEmpty => Detail is null && !IsCreating
                             && !IsImporting && !Provisioning;

    /// <summary>Drives IsEnabled on the sidebar, so nothing can be started mid-download.</summary>
    public bool NotProvisioning => !Provisioning;

    private void RefreshPaneState()
    {
        OnPropertyChanged(nameof(ShowProvisioning));
        OnPropertyChanged(nameof(NotProvisioning));
        OnPropertyChanged(nameof(ShowImport));
        OnPropertyChanged(nameof(ShowCreate));
        OnPropertyChanged(nameof(ShowDetail));
        OnPropertyChanged(nameof(ShowEmpty));
    }

    partial void OnIsCreatingChanged(bool value) => RefreshPaneState();

    // A pack whose version is mid-download should not also be told it is missing.
    partial void OnProvisioningChanged(bool value)
    {
        Detail?.RefreshGameState();
        RefreshPaneState();
        CancelProvisionCommand.NotifyCanExecuteChanged();
    }

    partial void OnProvisioningVersionChanged(string? value) => Detail?.RefreshGameState();

    /// <summary>A game install appearing or disappearing changes what every pack can launch.</summary>
    private void OnLibraryChanged() => Detail?.RefreshGameState();

    partial void OnNewPackErrorChanged(string? value) => OnPropertyChanged(nameof(HasNewPackError));

    partial void OnNewPackNameChanged(string value)
    {
        NewPackError = null;
        OnPropertyChanged(nameof(NewPackSlug));
        OnPropertyChanged(nameof(HasNewPackSlug));
        CreatePackCommand.NotifyCanExecuteChanged();
    }

    partial void OnDetailChanged(PackDetailViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        RefreshPaneState();
    }

    partial void OnSelectedPackChanged(PackListItemViewModel? value)
    {
        ConfirmingDelete = false;
        OnPropertyChanged(nameof(DeleteTargetName));
        OnPropertyChanged(nameof(CanDeleteSelected));
        RequestDeleteCommand.NotifyCanExecuteChanged();
        ConfirmDeleteCommand.NotifyCanExecuteChanged();

        if (value is null)
        {
            Detail = null;
            return;
        }

        Detail = new PackDetailViewModel(
            value.Manifest, _store, _moddb, _http, _library, _runtimes,
            log: LogFor(value.Id),
            note: line => NoteFor(value.Id, line),
            // The sidebar row shows the same manifest the detail pane is editing.
            onChanged: value.Changed,
            provision: v => ProvisionAsync(v, value.Id),
            isProvisioning: v => Provisioning && ProvisioningVersion == v,
            requestDelete: RequestDeleteCommand.Execute,
            knownGameVersions: KnownGameVersionsAsync);

        Detail.ConfirmVersionChange = ConfirmVersionChange;
        Detail.ConfirmPublish = ConfirmPublish;

        // Read through rather than copied, because the window sets this once and the detail
        // pane is rebuilt on every selection — capturing the value here would hand the
        // first pack a null and every later one the real thing.
        Detail.CopyToClipboard = text => CopyToClipboard?.Invoke(text) ?? Task.CompletedTask;
        Detail.ConfirmPackUpdate = ConfirmPackUpdate;

        // Fills the version picker in the background; the pane is usable before it arrives.
        _ = Detail.LoadGameVersionsAsync();

        // Asks the author whether they have published since, for a pack that follows one.
        // Fire and forget behind the pane: it says nothing unless there is something, and
        // an author's server being down is not news to anybody.
        _ = Detail.CheckForPackUpdateAsync();
    }

    /// <summary>
    /// Versions a pack can be pointed at: everything ModDB's publisher lists, newest first,
    /// with what is installed locally folded in so an offline machine still offers its own.
    /// </summary>
    private async Task<IReadOnlyList<string>> KnownGameVersionsAsync(CancellationToken ct)
    {
        var installed = _gameStore.ListInstalled().Select(i => i.Version).ToList();
        if (_install is not null) installed.Add(_install.Version);

        List<string> published = [];
        try
        {
            published = (await new GameCatalog(_http).ListReleasesAsync(ct: ct))
                .Select(r => r.Version).ToList();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Offline, or a catalog we cannot parse: what is on the machine is still worth
            // offering, and a version picker is no place to fail hard.
        }

        return published.Concat(installed)
            .Where(GameVersions.IsPlausibleVersion)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(v => v, GameVersionComparer.Ascending)
            .ToList();
    }

    private void LoadPacks()
    {
        var previouslySelected = SelectedPack?.Id;

        Packs.Clear();

        foreach (var id in _store.ListIds())
        {
            try
            {
                Packs.Add(new PackListItemViewModel(_store.Load(id)));
            }
            catch (Exception e)
            {
                Note($"could not read pack '{id}': {e.Message}");
            }
        }

        OnPropertyChanged(nameof(HasPacks));

        SelectedPack = Packs.FirstOrDefault(p => p.Id == previouslySelected)
                       ?? Packs.FirstOrDefault();

        Status = Packs.Count == 0
            ? "No packs yet — click “New pack” to make one."
            : $"{Packs.Count} pack{(Packs.Count == 1 ? "" : "s")}";
    }

    /// <summary>
    /// An event belonging to the launcher rather than to any one pack — a pack created or
    /// deleted, a game version installed from the Game versions pane. These go to the
    /// status bar only; filing them under whichever pack happened to be selected is the
    /// cross-contamination the per-pack logs exist to avoid.
    /// </summary>
    private void Note(string line) => Status = line;

    /// <summary>An event that belongs to one pack: its log, and the status bar.</summary>
    private void NoteFor(string packId, string line)
    {
        LogFor(packId).Add(line);
        Status = line;
    }

    [RelayCommand]
    private void BeginImport()
    {
        ImportText = "";
        ImportAsId = "";
        ImportError = null;
        IsCreating = false;
        IsImporting = true;
    }

    [RelayCommand]
    private void CancelImport()
    {
        IsImporting = false;
        ImportError = null;
    }

    /// <summary>
    /// Follows a <c>cairn://</c> link from a pack page: fetches what it names and shows it,
    /// for a yes or a no.
    ///
    /// A link can be on anybody's web page, so what makes this safe is not treating the
    /// link as trusted but showing what it resolved to — who published it, from what host,
    /// and every mod and version it would bring — and doing nothing until somebody agrees.
    /// Even then nothing downloads: adding writes a manifest, and mods arrive on a sync
    /// they ask for.
    /// </summary>
    public async Task<bool> FollowLinkAsync(string link)
    {
        if (!PackUri.TryGetDocumentUrl(link, out var url)) return false;

        await OfferAsync(url);
        return true;
    }

    /// <summary>
    /// Accepts either a pasted bundle or a URL to one, so a pack can be shared as a file,
    /// a gist link, or a blob of text in a chat message.
    ///
    /// An address goes through the same dialog a link does — pasting a URL from a chat
    /// message tells you no more about what is in it than clicking one does. Text or a
    /// file in your hand is different, and imports directly.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportPack()
    {
        var source = ImportText.Trim();

        if (PackSources.IsRemote(source))
        {
            await OfferAsync(source);
            return;
        }

        ImportBusy = true;
        ImportError = null;

        try
        {
            var json = File.Exists(source) ? File.ReadAllText(source) : source;

            Added(_store.Import(
                PackBundle.Parse(json),
                // Slugged like a new pack's name, so "Anego Copy" is accepted here rather
                // than rejected for containing a space.
                string.IsNullOrWhiteSpace(ImportAsId) ? null : PackId.FromOrFallback(ImportAsId),
                ImportReproduce));
        }
        catch (Exception e)
        {
            ImportError = e.Message;
        }
        finally
        {
            ImportBusy = false;
        }
    }

    /// <summary>
    /// Fetches a pack from a URL and puts it in front of somebody before anything happens.
    ///
    /// Errors land on the import pane rather than in a dialog with blanks in it — a pack
    /// that could not be fetched has nothing to show, and "here is a form full of nothing,
    /// approve it?" is worse than a sentence saying what went wrong.
    /// </summary>
    private async Task OfferAsync(string url)
    {
        ImportBusy = true;
        ImportError = null;

        try
        {
            // A pack decides which mods get installed, so it must not arrive over a
            // connection anyone on the path can rewrite. Loopback has no such path.
            if (PackSources.IsRewritableInFlight(url))
            {
                ShowImportError("Refusing to import over http — use an https:// address.", url);
                return;
            }

            var response = await _http.GetAsync(url);

            // A withdrawn pack answers rather than 404s, precisely so this can say what
            // happened instead of "not found".
            if (response.StatusCode == HttpStatusCode.Gone)
            {
                ShowImportError("That pack was withdrawn by whoever published it.", url);
                return;
            }

            response.EnsureSuccessStatusCode();

            var bundle = PackBundle.Parse(await response.Content.ReadAsStringAsync());
            var offer = new ImportViewModel(bundle, url, id => _store.Exists(id));

            if (ConfirmImport is null || !await ConfirmImport(offer))
            {
                IsImporting = false;
                return;
            }

            Added(_store.Import(bundle, PackId.FromOrFallback(offer.AsId), offer.Reproduce));
        }
        catch (Exception e)
        {
            ShowImportError(e.Message, url);
        }
        finally
        {
            ImportBusy = false;
        }
    }

    /// <summary>
    /// Puts a failure somewhere it can be read and retried. A link followed from a browser
    /// has no pane open yet, so this opens one rather than failing invisibly.
    /// </summary>
    private void ShowImportError(string message, string url)
    {
        if (!IsImporting)
        {
            BeginImport();
            ImportText = url;
        }

        ImportError = message;
    }

    private void Added(PackManifest manifest)
    {
        Note($"imported pack '{manifest.Id}' ({manifest.Mods.Count} mods)");
        IsImporting = false;

        LoadPacks();
        SelectedPack = Packs.FirstOrDefault(p => p.Id == manifest.Id);
    }

    private bool CanImport => !ImportBusy && !string.IsNullOrWhiteSpace(ImportText);

    /// <summary>
    /// Opens application settings.
    ///
    /// A window rather than a pane: none of it is about the pack you have selected, and
    /// putting it in the pack area is what made "Game versions" read as a pack action.
    /// The view supplies the opener, since Core knows nothing about windows.
    /// </summary>
    public Func<PreferencesViewModel, Task>? OpenPreferences { get; set; }

    /// <summary>
    /// Set by the view, which owns the only TopLevel there is to ask for a clipboard.
    /// Forwarded to each detail pane so "Copy diagnostics" works wherever it is pressed.
    /// </summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    private Func<VersionChangeViewModel, Task<bool>>? _confirmVersionChange;
    private Func<ShareViewModel, Task<bool>>? _confirmPublish;

    /// <summary>
    /// Set by the view; see PackDetailViewModel.ConfirmVersionChange.
    ///
    /// Assigning it reaches the current pack as well as later ones. The constructor selects
    /// a pack, so the first PackDetailViewModel exists before the view has had a chance to
    /// supply this — and a plain auto-property left that one holding a null it never
    /// revisited, which meant Check silently did nothing for the pack you start on.
    /// </summary>
    public Func<VersionChangeViewModel, Task<bool>>? ConfirmVersionChange
    {
        get => _confirmVersionChange;
        set
        {
            _confirmVersionChange = value;
            if (Detail is not null) Detail.ConfirmVersionChange = value;
        }
    }

    private Func<PackUpdateViewModel, Task<bool>>? _confirmPackUpdate;

    /// <summary>Set by the view; reaches the current pack too, for the reason above.</summary>
    public Func<PackUpdateViewModel, Task<bool>>? ConfirmPackUpdate
    {
        get => _confirmPackUpdate;
        set
        {
            _confirmPackUpdate = value;
            if (Detail is not null) Detail.ConfirmPackUpdate = value;
        }
    }

    /// <summary>
    /// Shows a fetched pack and returns whether to add it. Supplied by the view; when
    /// absent — headless tests — nothing is added, which is the safe way to be missing.
    /// </summary>
    public Func<ImportViewModel, Task<bool>>? ConfirmImport { get; set; }

    /// <summary>Set by the view; same arrangement as ConfirmVersionChange above.</summary>
    public Func<ShareViewModel, Task<bool>>? ConfirmPublish
    {
        get => _confirmPublish;
        set
        {
            _confirmPublish = value;
            if (Detail is not null) Detail.ConfirmPublish = value;
        }
    }

    [RelayCommand]
    private async Task ShowPreferences()
    {
        if (OpenPreferences is null) return;

        if (!Games.CatalogLoaded) Games.RefreshCatalogCommand.Execute(null);

        // No confirmer supplied here on purpose: PreferencesWindow parents one to itself,
        // so a prompt raised from Preferences does not hand focus to the main window.
        await OpenPreferences(new PreferencesViewModel(
            Games, _store, _gameStore, _runtimes, new ModIconCache(_http), new ModInfoCache(_moddb)));

        // Removing a game version from in there changes what every pack can launch.
        Games.RefreshInstalled();
        OnLibraryChanged();
    }

    [RelayCommand]
    private async Task BeginCreate()
    {
        IsImporting = false;
        NewPackName = "";
        NewPackConnect = "";
        NewPackError = null;

        // Populate before the pane appears. A ComboBox bound to an empty collection
        // coerces its selection to null, and re-assigning the same string afterwards
        // raises no PropertyChanged, so the selection would never come back.
        PopulateInstalledVersions();
        IsCreating = true;

        await AppendCatalogVersionsAsync();
    }

    private void PopulateInstalledVersions()
    {
        var installed = new List<string>();
        if (_install is not null) installed.Add(_install.Version);
        installed.AddRange(_gameStore.ListInstalled().Select(i => i.Version));

        SetVersionChoices(installed);
    }

    /// <summary>
    /// Replaces the offered versions, newest first. Installed versions used to be listed
    /// ahead of the catalog, which put an older installed version above newer published
    /// ones; the whole list is sorted instead of relying on insertion order.
    /// </summary>
    private void SetVersionChoices(IEnumerable<string> versions)
    {
        // "unknown" is what GameInstall reports for an install whose assembly it could not
        // read. It is not something a pack can target, so it must not be offerable.
        var ordered = GameVersionComparer.Descending(
            versions.Where(GameVersions.IsPlausibleVersion).Distinct()).ToList();

        GameVersionChoices.Clear();
        foreach (var v in ordered) GameVersionChoices.Add(v);

        if (GameVersionChoices.Count > 0
            && (NewPackGameVersion is null || !GameVersionChoices.Contains(NewPackGameVersion)))
            NewPackGameVersion = GameVersionChoices[0];
    }

    /// <summary>
    /// Adds everything the catalog publishes on top of what is installed. A catalog
    /// failure is not fatal — the form still works with the installed versions.
    /// </summary>
    private async Task AppendCatalogVersionsAsync()
    {
        LoadingVersions = true;
        try
        {
            var releases = await new GameCatalog(_http).ListReleasesAsync();

            // Keep whatever the user already picked; only the ordering is rebuilt.
            var chosen = NewPackGameVersion;
            SetVersionChoices(GameVersionChoices.Concat(releases.Select(r => r.Version)));

            if (chosen is not null && GameVersionChoices.Contains(chosen))
                NewPackGameVersion = chosen;
        }
        catch (Exception e)
        {
            Note($"could not load the version list: {e.Message}");
        }
        finally
        {
            LoadingVersions = false;
        }
    }

    [RelayCommand]
    private void CancelCreate()
    {
        IsCreating = false;
        NewPackError = null;
    }

    [RelayCommand(CanExecute = nameof(CanCreatePack))]
    private void CreatePack()
    {
        if (string.IsNullOrWhiteSpace(NewPackName))
        {
            NewPackError = "Enter a name.";
            return;
        }

        // Derived here rather than read off the bound property, so two packs created in
        // quick succession cannot race to the same id.
        var id = _store.SuggestId(NewPackName);

        // The store validates too; this only surfaces a problem before touching the disk.
        var problem = _store.DescribeIdProblem(id);
        if (problem is not null)
        {
            NewPackError = problem;
            return;
        }

        // A ComboBox nulls a selection that is not among its items, so this can genuinely
        // be null rather than merely empty.
        var gameVersion = NewPackGameVersion?.Trim();
        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            NewPackError = "Choose a game version.";
            return;
        }

        try
        {
            _store.Create(id, gameVersion, NewPackName?.Trim(), NewPackConnect?.Trim());
        }
        catch (Exception e)
        {
            NewPackError = e.Message;
            return;
        }

        IsCreating = false;
        Note($"created pack '{id}'");
        LoadPacks();
        SelectedPack = Packs.FirstOrDefault(p => p.Id == id);

        // Creating a pack for a version you do not have should just work; making the user
        // discover the Game versions pane and the .NET requirement is the bug.
    }

    /// <summary>
    /// Downloads whatever the version needs, reporting into the status bar.
    ///
    /// A game version is shared between packs, but provisioning is always started by
    /// pressing Play on one of them — so it is logged against the pack that asked, which
    /// is where someone looking for "why did that take 30 seconds" will go.
    /// </summary>
    public async Task ProvisionAsync(string gameVersion, string? forPackId = null)
    {
        void Say(string line)
        {
            if (forPackId is null) Note(line);
            else NoteFor(forPackId, line);
        }

        var plan = _provisioner.Plan(gameVersion, _install);
        if (!plan.AnythingToDo) return;

        _provisionCts?.Dispose();
        _provisionCts = new CancellationTokenSource();

        Provisioning = true;
        ProvisioningVersion = gameVersion;
        ProvisionFraction = 0;
        // Nothing has reported a fraction yet, and resolving the download takes a moment.
        ProvisionIndeterminate = true;
        ProvisionStatus = plan.Describe();
        Say(plan.Describe());

        try
        {
            var progress = new Progress<ProvisionStep>(p =>
            {
                ProvisionStatus = p.Detail;
                ProvisionIndeterminate = p.Fraction is null;
                ProvisionFraction = p.Fraction ?? 0;
            });

            await _provisioner.EnsureAsync(gameVersion, _install, progress, _provisionCts.Token);

            ProvisionStatus = $"Vintage Story {gameVersion} is ready";
            Say(ProvisionStatus);

            Games.RefreshInstalled();
            OnLibraryChanged();
        }
        catch (OperationCanceledException)
        {
            // The installer unpacks through a staging directory and removes it on failure,
            // so a cancelled download leaves nothing half-installed.
            ProvisionStatus = $"Cancelled downloading {gameVersion}";
            Say(ProvisionStatus);
        }
        catch (Exception e)
        {
            ProvisionStatus = $"Could not prepare {gameVersion}: {e.Message}";
            Say(ProvisionStatus);
        }
        finally
        {
            Provisioning = false;
            ProvisioningVersion = null;
        }
    }

    public bool CanCancelProvision => Provisioning;

    [RelayCommand(CanExecute = nameof(CanCancelProvision))]
    private void CancelProvision()
    {
        ProvisionStatus = "cancelling…";
        _provisionCts?.Cancel();
    }

    private bool CanCreatePack => !string.IsNullOrWhiteSpace(NewPackName);
}
