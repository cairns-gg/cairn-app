using Cairn.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cairn.Core.Packs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cairn.App.ViewModels;

/// <summary>Where a pack is being brought in from.</summary>
public enum ImportSource
{
    /// <summary>The mods already installed in plain Vintage Story on this machine.</summary>
    Install,

    /// <summary>A https:// address, which is shown for approval before anything is taken.</summary>
    Link,

    /// <summary>A pack somebody sent as text, or a file on disk.</summary>
    Paste,
}

/// <summary>
/// One installed mod as a row: what it is, and what importing would do with it.
///
/// Listed the moment its zip has been read, which is instant, and told what will become of
/// it when its ModDB lookup lands, which is not. The two used to arrive together, so a
/// folder of mods somebody already owned took as long to appear as it took to check —
/// making it look as though Cairn were off finding their mods rather than reading them.
/// </summary>
public sealed partial class ImportRowViewModel(InstalledMod mod) : ViewModelBase
{
    /// <summary>Null until the lookup comes back.</summary>
    private ImportCandidate? candidate;

    public ImportRowViewModel(ImportCandidate decided) : this(decided.Mod) => Decide(decided);

    public void Decide(ImportCandidate decided)
    {
        candidate = decided;

        OnPropertyChanged(nameof(Included));
        OnPropertyChanged(nameof(RowOpacity));
        OnPropertyChanged(nameof(Verdict));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(Candidate));
        OnPropertyChanged(nameof(CanInclude));
        OnPropertyChanged(nameof(Include));
    }

    /// <summary>Which zip this row is, for matching a verdict to it.</summary>
    public string FileName => mod.FileName;

    /// <summary>The candidate behind this row, for a caller settling the whole plan.</summary>
    public ImportCandidate? Candidate => candidate;

    /// <summary>
    /// Whether this one could go in at all — see <see cref="ImportCandidate.CanInclude"/>.
    /// False leaves the tick on the row and disabled, rather than removing it: a row with no
    /// control where every other row has one reads as an oversight, and the verdict beside it
    /// is the explanation.
    /// </summary>
    public bool CanInclude => candidate?.CanInclude ?? false;

    /// <summary>
    /// Whether it is going in, written straight through to the candidate the plan is built
    /// from — so what is ticked on screen and what CreatePack reads cannot drift apart.
    ///
    /// On for everything that can go in. The folder is the answer: somebody choosing this
    /// source has said "a pack of what I am running", and a list that started empty would
    /// ask them to say it again forty times.
    /// </summary>
    public bool Include
    {
        // The effective answer, not the stored wish. A candidate keeps Include set whatever
        // its verdict, so a mod ModDB cannot serve would otherwise draw a ticked box that
        // happened to be disabled — which reads as "going in" to everybody who does not
        // notice it is greyed.
        get => candidate?.Included ?? false;
        set
        {
            if (candidate is not { } c || c.Include == value) return;

            c.Include = value;

            OnPropertyChanged();
            OnPropertyChanged(nameof(Included));
            OnPropertyChanged(nameof(RowOpacity));

            Settled?.Invoke();
        }
    }

    /// <summary>Told when a tick moves, so the count above the list keeps up with it.</summary>
    public Action? Settled { get; set; }

    public string Name => mod.Describe;

    public string ModId => candidate?.ModId ?? mod.ModId ?? "";

    public bool Included => candidate?.Included ?? false;

    /// <summary>
    /// The ones that will not make it are dimmed rather than hidden. A count that does not
    /// add up is worse than a list with three greyed rows in it. A row still waiting on its
    /// lookup is neither, and stays at full strength until there is something to say.
    /// </summary>
    public double RowOpacity => candidate is null || Included ? 1.0 : 0.5;

    /// <summary>
    /// The verdict in the words of somebody looking at their own mods, not the enum's.
    /// "unknown" is accurate and says nothing; "ModDB has no mod with this id" tells you it
    /// is a private build and there is nothing to fix.
    /// </summary>
    public string Verdict => candidate is null ? Lang.Get("importsrc-checking") : candidate.Verdict switch
    {
        ImportVerdict.Ready => Lang.Get("importsrc-verdict-ready"),
        ImportVerdict.Newest => Lang.Get("importsrc-verdict-newest"),
        ImportVerdict.Accepted => Lang.Get("importsrc-verdict-accepted"),
        ImportVerdict.Unreadable => Lang.Get("importsrc-verdict-unreadable"),
        ImportVerdict.Duplicate => Lang.Get("importsrc-verdict-duplicate"),
        ImportVerdict.Disabled => Lang.Get("importsrc-verdict-disabled"),
        ImportVerdict.Unknown => Lang.Get("importsrc-verdict-unknown"),
        ImportVerdict.Incompatible => Lang.Get("importsrc-verdict-incompatible"),
        _ => Lang.Get("importsrc-verdict-unchecked"),
    };

    /// <summary>
    /// Blank for a mod that is simply going in: its note is the version, and the name above
    /// it already ends in that version. Every other verdict has something to say.
    ///
    /// Accepted is blank for the same reason once removed. Its note names the version and
    /// says the release is unmarked — but the row's title is already the name and version,
    /// and the verdict beside it already reads "not marked for this game". Three sayings of
    /// one thing, wrapped over two lines, on what is now the common case rather than the
    /// rare one. The CLI keeps the full sentence, having no column to put it in.
    /// </summary>
    public string Note =>
        candidate is null || candidate.Verdict is ImportVerdict.Ready or ImportVerdict.Accepted
            ? ""
            : candidate.Note;
}

/// <summary>
/// The one place a pack arrives from, whichever way it arrives.
///
/// It used to be a pane with a single box that took either a URL or pasted text and guessed
/// which. Guessing was the smaller problem: there was no way at all to bring in the mods
/// somebody already had, which is the state nearly every new user is in — they have played
/// Vintage Story, they have a Mods folder, and the launcher offered them an empty pack and a
/// search box.
///
/// So the three ways in are named and offered together. The dialog only ever *collects* —
/// scanning an install produces a plan and nothing more. Creating the pack, following a
/// link, taking pasted text: all of that stays with the caller, which is the thing that owns
/// packs.
/// </summary>
public sealed partial class ImportSourceViewModel : ViewModelBase
{
    private readonly InstallImport _importer;

    /// <summary>
    /// The install being read for a version, which a person may correct. Not readonly for
    /// that reason, and everything derived from it — the version, the note, and every mod's
    /// verdict — is recomputed when it moves.
    /// </summary>
    private GameInstall? _install;

    /// <summary>What the pack targets when there is no install at all to take it from.</summary>
    private readonly string _fallbackVersion;
    private readonly Func<string?, string> _suggestId;
    /// <summary>
    /// Mod ids and filenames switched off in the game's settings, which live in the data path
    /// — so this moves with the mods folder rather than being read once.
    /// </summary>
    private IReadOnlySet<string> _disabled;

    /// <summary>Cancels a scan somebody has walked away from.</summary>
    private CancellationTokenSource? _scan;

    /// <param name="modsDir">
    /// The folder to read. The player's own, by default — Cairn only ever reads it.
    /// </param>
    /// <param name="install">
    /// The Vintage Story install this folder belongs to, when there is one.
    ///
    /// It is where the pack's own game version comes from — importing the mods you are
    /// running into a pack for some other version is a thing to ask for, not a default. It
    /// also decides whether an unmarked mod may be imported on the strength of somebody
    /// running it; see <see cref="InstallImport.PlanAsync"/>, and note that having no
    /// install is therefore not merely cosmetic: it silently withdraws that allowance.
    ///
    /// Null when Cairn could not find one, which is what <see cref="ChooseInstall"/> exists
    /// to fix. The choice is made here rather than in Preferences because this is the only
    /// screen where the answer changes anything a person is looking at.
    /// </param>
    /// <param name="gameVersion">
    /// What the pack will target when there is no install to take it from. Never offered as
    /// a choice: see <see cref="GameVersion"/>.
    /// </param>
    /// <param name="savesDir">
    /// Where the same install keeps its worlds. Offered alongside the mods because a world
    /// made under a mod set generally cannot be opened without it — importing the mods and
    /// leaving the worlds behind is half a job.
    /// </param>
    public ImportSourceViewModel(
        InstallImport importer,
        string modsDir,
        string savesDir,
        IReadOnlySet<string> disabled,
        GameInstall? install,
        string gameVersion,
        Func<string?, string> suggestId)
    {
        Worlds = new WorldPickerViewModel(savesDir);

        // The folder holding the Mods folder, which is where the worlds and settings are.
        // Taken from the saves directory rather than derived a second way, so the three of
        // them cannot disagree about which install is being read.
        _dataPath = Path.GetDirectoryName(savesDir) ?? savesDir;
        _modConfig = InstalledModConfigs.Measure(_dataPath);

        _importer = importer;
        _suggestId = suggestId;
        _disabled = disabled;
        _install = install;
        _fallbackVersion = gameVersion;

        // The list frame is drawn only when it has rows. Empty, a bordered scroll area is
        // indistinguishable from a disabled multi-line text box, and the dialog opens in
        // exactly that state — so the first thing it showed was a field you could not type
        // in, above a button you could not press.
        Mods.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRows));

        ModsDir = modsDir;

        PackName = Lang.Get("importsrc-default-name");
    }

    // ---- which of the three ----

    [ObservableProperty] public partial ImportSource Source { get; set; } = ImportSource.Install;

    partial void OnSourceChanged(ImportSource value)
    {
        Error = null;

        OnPropertyChanged(nameof(FromInstall));
        OnPropertyChanged(nameof(FromLink));
        OnPropertyChanged(nameof(FromPaste));
        OnPropertyChanged(nameof(CanImport));
        OnPropertyChanged(nameof(ImportLabel));

        // Reading an install is forty ModDB lookups for a forty-mod folder. Somebody who
        // switched to a link is not waiting on them, and somebody who switches back has
        // asked for them again.
        if (value != ImportSource.Install) _scan?.Cancel();
        else if (!Scanned && !Busy) _ = ScanAsync();
    }

    /// <summary>
    /// Bound both ways by the radio buttons: a set turns into a source change, and a source
    /// change re-reads all three so the group stays consistent however it was moved.
    /// </summary>
    public bool FromInstall
    {
        get => Source == ImportSource.Install;
        set { if (value) Source = ImportSource.Install; }
    }

    public bool FromLink
    {
        get => Source == ImportSource.Link;
        set { if (value) Source = ImportSource.Link; }
    }

    public bool FromPaste
    {
        get => Source == ImportSource.Paste;
        set { if (value) Source = ImportSource.Paste; }
    }

    public string ImportLabel =>
        Source == ImportSource.Install ? Lang.Get("importsrc-create") : Lang.Get("importsrc-import");

    // ---- a link, or pasted text ----

    [ObservableProperty] public partial string Url { get; set; } = "";

    [ObservableProperty] public partial string Text { get; set; } = "";

    /// <summary>Left blank to keep whatever the author called it.</summary>
    [ObservableProperty] public partial string AsId { get; set; } = "";

    partial void OnUrlChanged(string value) => OnPropertyChanged(nameof(CanImport));

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(CanImport));

    /// <summary>What the caller should act on: the URL, or the pasted pack.</summary>
    public string Payload => (Source == ImportSource.Link ? Url : Text).Trim();

    // ---- an install ----

    public string ModsDir { get; private set; }

    /// <summary>
    /// The folder holding it, which is where the worlds and the mod settings are. Kept
    /// because two of the three things this dialog offers hang off it rather than off the
    /// Mods folder that was named.
    /// </summary>
    private string _dataPath;

    /// <summary>Whether the mods folder is one somebody named, rather than the game's own.</summary>
    public bool ModsAreChosen => !string.IsNullOrWhiteSpace(CairnSettings.Load().GameDataPath);

    /// <summary>
    /// Reads a different folder for mods, and the worlds beside it.
    ///
    /// Asked for as the Mods folder rather than the data path holding it. That is the folder
    /// people can name — the game's own instructions send them to it and their zips are in
    /// it — while "data path" is a phrase nobody uses unless they have set <c>--dataPath</c>.
    /// <see cref="InstalledMods.ChooseModsFolder"/> takes either end and works out the rest.
    /// </summary>
    [RelayCommand]
    private async Task ChooseMods()
    {
        if (PickFolder is null) return;

        if (await PickFolder() is not { } picked) return;

        if (InstalledMods.ChooseModsFolder(picked) is not { } folder)
        {
            ModsProblem = Lang.Get("importsrc-mods-invalid", picked);
            return;
        }

        ModsProblem = "";
        CairnSettings.Update(s => s.GameDataPath = folder.DataPath);

        Retarget(folder);
    }

    /// <summary>
    /// Why a chosen mods folder was refused, or empty. See <see cref="InstallProblem"/> for
    /// why this stays on screen rather than passing by.
    /// </summary>
    [ObservableProperty] public partial string ModsProblem { get; set; } = "";

    private void Retarget(InstalledMods.ModsFolder folder)
    {
        ModsDir = folder.ModsDir;
        _disabled = InstalledMods.DisabledIn(folder.DataPath);

        Worlds = new WorldPickerViewModel(InstalledWorlds.SavesIn(folder.DataPath));

        _dataPath = folder.DataPath;
        _modConfig = InstalledModConfigs.Measure(folder.DataPath);

        // A folder with no worlds should not leave the box ticked from the last one.
        BringWorlds = false;

        OnPropertyChanged(nameof(ModsDir));
        OnPropertyChanged(nameof(ModsAreChosen));
        OnPropertyChanged(nameof(Worlds));
        OnPropertyChanged(nameof(HasWorlds));
        OnPropertyChanged(nameof(WorldsLabel));
        OnPropertyChanged(nameof(HasModConfig));
        OnPropertyChanged(nameof(ModConfigLabel));

        // The version the pack targets is in that line whenever no install names one.
        OnPropertyChanged(nameof(GameDetail));

        if (Source == ImportSource.Install) _ = ScanAsync();
    }

    /// <summary>
    /// There is deliberately no way back to "look for them again".
    ///
    /// Change… is how a wrong answer is corrected, and the case a reset would exist for
    /// mends itself: a stored directory that has stopped being an install is skipped by
    /// GameInstall.CandidateDirectories and the search runs on past it. A button for it was
    /// another control on the dialog that least needs them, and it undid both corrections
    /// in order to fix either one.
    /// </summary>
    public bool InstallIsChosen => !string.IsNullOrWhiteSpace(CairnSettings.Load().GameInstallPath);

    /// <summary>
    /// The version of the install the folder belongs to, or null when there is no install
    /// to ask — a folder left behind by a game that has since been moved or removed.
    ///
    /// Implausible versions are refused rather than passed on: "unknown" comes back from an
    /// install whose assembly could not be read, and testimony about a version nobody can
    /// name is not testimony.
    /// </summary>
    public string? PlayedOn =>
        _install?.Version is { } v && GameVersions.IsPlausibleVersion(v) ? v : null;

    /// <summary>Where that install is, for the line that offers to change it.</summary>
    public string? InstallDirectory => _install?.Directory;

    /// <summary>
    /// The version for the Game row, or the word for not having found one.
    ///
    /// A property rather than a TargetNullValue on the binding, which cannot take a
    /// translated string: a markup extension there is evaluated as the fallback *value* and
    /// the row rendered the words "Avalonia.Data.Binding".
    /// </summary>
    public string GameLine => PlayedOn ?? Lang.Get("importsrc-no-game");

    /// <summary>
    /// The rest of the Game row: where the install is, or — when there is none — what that
    /// costs, which is the half nobody would work out for themselves.
    ///
    /// One line doing both jobs rather than a paragraph underneath doing the second. The
    /// column is empty in exactly the case the sentence is needed, and a warning read where
    /// the thing it is about is already being read beats one stacked below the block.
    /// </summary>
    public string GameDetail => InstallDirectory ?? Lang.Get("importsrc-no-install", GameVersion);

    public bool HasInstall => PlayedOn is not null;

    /// <summary>
    /// The version the pack will target: the one the mods are being run on.
    ///
    /// Not a choice, and it used to be one — a dropdown reading "Scan for game 1.22.6",
    /// which looked like a filter on the scan, was defaulted from the newest version Cairn
    /// knew about rather than the install being read, and asked a question with exactly one
    /// sensible answer. Importing is "give me a pack of what I am running"; targeting some
    /// other version is a different job, and the pack's Settings tab already does it
    /// properly, with a preview of what each mod would do.
    /// </summary>
    public string GameVersion => PlayedOn ?? _fallbackVersion;

    /// <summary>
    /// The worlds in the same install, to bring across with the mods. Copied, never moved:
    /// see <see cref="InstalledWorlds"/>.
    ///
    /// Rebuilt rather than fixed, because the folder it reads follows the mods folder: they
    /// are <c>Saves</c> and <c>Mods</c> beside each other under one data path, and somebody
    /// who corrected one and found the other still listing worlds from the old place would
    /// have been given half a repair.
    /// </summary>
    public WorldPickerViewModel Worlds { get; private set; }

    /// <summary>
    /// Whether the worlds in the same folder come across too — all of them, or none.
    ///
    /// A checkbox rather than the list it used to be. Picking between worlds one at a time is
    /// a real thing to want and the pack's own Settings tab already does it properly, with
    /// the pack in front of you; here it was the tallest thing on a screen whose subject is
    /// mods, and it grew with somebody's save folder rather than with anything this dialog
    /// is about.
    ///
    /// Off, unlike the mods. A mod is a few hundred kilobytes and the point of the screen; a
    /// world is gigabytes, is not needed for the pack to work, and copying one nobody asked
    /// for is the kind of default that fills a disk.
    /// </summary>
    [ObservableProperty] public partial bool BringWorlds { get; set; }

    public bool HasWorlds => Worlds.Any;

    /// <summary>Names the cost, because that is what the answer turns on.</summary>
    public string WorldsLabel => Lang.Plural(
        "importsrc-bring-worlds", Worlds.Worlds.Count,
        Worlds.Worlds.Count, Bytes.Human(Worlds.TotalBytes));

    /// <summary>What the caller copies: all of them, or none.</summary>
    public IReadOnlyList<InstalledWorld> ChosenWorlds => BringWorlds ? Worlds.All : [];

    // ---- the settings that made those mods work together ----

    /// <summary>
    /// Whether the mod settings in the same folder come across.
    ///
    /// On, unlike the worlds, and for the reasons that make them different things. These are
    /// kilobytes rather than gigabytes; the pack does not merely work better with them, it is
    /// not the thing that was being played without them — plenty of mods only get along once
    /// a value has been changed, and a pack whose mods are right and whose settings are the
    /// authors' defaults is a different pack. Somebody choosing this source asked for what
    /// they are running.
    ///
    /// It copies files into this pack and nothing else. What a pack carries *to other people*
    /// is declared in its manifest, one value at a time, in the Mod config tab — these files
    /// are what that tab then has to offer.
    /// </summary>
    [ObservableProperty] public partial bool BringModConfig { get; set; } = true;

    private InstalledModConfigs.Contents _modConfig = new(0, 0);

    public bool HasModConfig => _modConfig.Any;

    public string ModConfigLabel => Lang.Plural(
        "importsrc-bring-modconfig", _modConfig.Files,
        _modConfig.Files, Bytes.Human(_modConfig.Bytes));

    /// <summary>Where they would be copied from, or null when the box is off or empty.</summary>
    public string? ChosenModConfigFrom =>
        BringModConfig && HasModConfig ? _dataPath : null;

    /// <summary>
    /// Asks for a directory, returning null if the user thought better of it. Set by the
    /// view, because picking a folder is the platform's job and this is a view model —
    /// which also lets a test answer it without a dialog.
    /// </summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    /// <summary>
    /// Why a chosen directory was refused, or empty. Left on screen: somebody who picked the
    /// wrong folder is about to pick another one and needs to be able to read what was wrong
    /// with the first while the picker is open.
    /// </summary>
    [ObservableProperty] public partial string InstallProblem { get; set; } = "";

    /// <summary>
    /// Points Cairn at the install it could not find, or at the other one on a machine with
    /// two.
    ///
    /// Remembered in settings rather than held for this dialog alone. It is the same answer
    /// next time, and it is the answer <see cref="GameProvisioner"/> uses to launch a pack
    /// from a copy of the game somebody already has instead of downloading a second one —
    /// so an import is where this gets settled, and everything afterwards is the better for
    /// it having been.
    ///
    /// Checked before it is stored, and forgiving about one level down — see
    /// <see cref="GameInstall.Choose"/>, which is what lets a macOS folder picker that
    /// cannot enter Vintagestory.app still be used to select it.
    /// </summary>
    [RelayCommand]
    private async Task ChooseInstall()
    {
        if (PickFolder is null) return;

        if (await PickFolder() is not { } chosen) return;

        if (GameInstall.Choose(chosen) is not { } found)
        {
            InstallProblem = Lang.Get("importsrc-install-invalid", chosen);
            return;
        }

        // An install whose version cannot be read is no use for either thing this answers:
        // the pack takes its version from here, and a pack launches from an install only
        // when the two versions match. Taken without this check it was accepted in silence
        // and then reported as no install at all — a folder chosen, no complaint, and the
        // same "no Vintage Story install found" line still sitting underneath it.
        if (!GameVersions.IsPlausibleVersion(found.Version))
        {
            InstallProblem = Lang.Get("importsrc-install-no-version", found.Directory);
            return;
        }

        InstallProblem = "";
        CairnSettings.Update(s => s.GameInstallPath = found.Directory);

        Adopt(found);
    }

    /// <summary>
    /// Takes a different install and rebuilds everything that rested on the old one.
    ///
    /// The rescan is the point rather than a refresh. The game version decides every single
    /// verdict on the list — which releases ModDB will serve, and whether a mod marked for
    /// nothing like it may be taken on the strength of somebody running it — so a list left
    /// standing after the version moved is a list of answers to a question nobody asked any
    /// more, with an Import button under it.
    /// </summary>
    private void Adopt(GameInstall? install)
    {
        _install = install;

        OnPropertyChanged(nameof(PlayedOn));
        OnPropertyChanged(nameof(GameLine));
        OnPropertyChanged(nameof(InstallDirectory));
        OnPropertyChanged(nameof(HasInstall));
        OnPropertyChanged(nameof(GameVersion));
        OnPropertyChanged(nameof(GameDetail));
        OnPropertyChanged(nameof(InstallIsChosen));

        InstallChanged?.Invoke();

        if (Source == ImportSource.Install) _ = ScanAsync();
    }

    /// <summary>
    /// Told to whoever opened this dialog, because the install is not this window's to keep:
    /// the launcher behind it built a game library and a version list out of the old answer.
    /// </summary>
    public Action? InstallChanged { get; set; }

    [ObservableProperty] public partial string PackName { get; set; }

    partial void OnPackNameChanged(string value)
    {
        OnPropertyChanged(nameof(Slug));
        OnPropertyChanged(nameof(CanImport));
    }

    /// <summary>The directory it would live in. Nobody is asked to invent one.</summary>
    public string Slug => _suggestId(PackName);

    public ObservableCollection<ImportRowViewModel> Mods { get; } = [];

    public bool HasRows => Mods.Count > 0;

    /// <summary>What the plan came to, so the review list has an answer above it.</summary>
    [ObservableProperty] public partial string Summary { get; set; } = "";

    [ObservableProperty] public partial string? Progress { get; set; }

    [ObservableProperty] public partial bool Busy { get; set; }

    [ObservableProperty] public partial bool Scanned { get; set; }

    partial void OnBusyChanged(bool value) => OnPropertyChanged(nameof(CanImport));

    partial void OnScannedChanged(bool value) => OnPropertyChanged(nameof(CanImport));

    /// <summary>The plan the caller creates a pack from. Empty until a scan has run.</summary>
    public IReadOnlyList<ImportCandidate> Plan { get; private set; } = [];

    [ObservableProperty] public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    /// <summary>
    /// Reads the folder and works out what each mod would become.
    ///
    /// Runs on its own when the dialog opens rather than waiting to be asked. Pressing a
    /// button to make the thing you chose happen is a step that answers nothing: there is
    /// only one folder, only one game version, and the list is the whole point of picking
    /// this source. Choosing one of the other two cancels it — see
    /// <see cref="OnSourceChanged"/> — so somebody who came here to paste a link does not
    /// pay for forty ModDB lookups on the way past.
    /// </summary>
    [RelayCommand]
    public async Task ScanAsync()
    {
        if (Busy) return;

        _scan?.Dispose();
        _scan = new CancellationTokenSource();
        var ct = _scan.Token;

        Busy = true;
        Error = null;
        Mods.Clear();
        Scanned = false;

        try
        {
            var scan = InstalledMods.Scan(ModsDir);

            if (scan.Mods.Count == 0)
            {
                // The folder is named in the row above rather than here as well: it was
                // said twice on a screen whose problem is how much it says.
                Summary = Lang.Get("importsrc-no-zips");
                return;
            }

            // The folder, immediately. Reading the zips is instant — it is the lookups that
            // take a moment, and each row says "checking…" until its own comes back.
            foreach (var mod in scan.Mods)
                Mods.Add(new ImportRowViewModel(mod) { Settled = Resettle });

            var checking = Mods.ToDictionary(r => r.FileName, StringComparer.OrdinalIgnoreCase);
            var done = 0;

            Summary = Lang.Get("importsrc-scanning", scan.Mods.Count);

            var plan = await _importer.PlanAsync(
                scan, GameVersion, _disabled,
                new System.Progress<ImportCandidate>(c =>
                {
                    if (checking.TryGetValue(c.Mod.FileName, out var row)) row.Decide(c);
                    Progress = Lang.Get("importsrc-checked", ++done, scan.Mods.Count);
                }),
                ct);

            Plan = plan;

            // Worst news first — but only once every row has a verdict, since sorting on one
            // as it arrives would shuffle the list under somebody reading it. Somebody
            // deciding whether to go ahead needs the mods that will not make it, and putting
            // them under thirty that will is where they are not read.
            var order = plan
                .OrderBy(c => c.Included)
                .ThenBy(c => c.Mod.Describe, StringComparer.OrdinalIgnoreCase)
                .Select(c => c.Mod.FileName)
                .ToList();

            Mods.Clear();
            foreach (var file in order)
                if (checking.TryGetValue(file, out var row))
                    Mods.Add(row);

            var taking = plan.Count(c => c.Included);

            // Two sentences rather than one built by concatenation: the tail inflects a
            // noun and its verb together, and the head has to be able to precede it in
            // whatever order a language puts them.
            // Kept, because the summary is rebuilt whenever a row is settled again and the
            // count of what the folder held besides mods does not change with it. Recomputed
            // from the scan each time it was needed, ticking a box would quietly drop the
            // sentence saying two things in the folder had been passed over.
            _ignored = scan.Ignored.Count;

            Summary = Describe(taking, scan.Mods.Count);

            Scanned = taking > 0;
        }
        catch (OperationCanceledException)
        {
            // Walked away from this source. Nothing to report and nothing to leave behind.
            Summary = "";
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
        finally
        {
            Busy = false;
            Progress = null;
        }
    }

    /// <summary>
    /// The plan and the summary, from what the rows now say. Called after a row changes its
    /// mind rather than recomputing the scan, which would cost forty lookups to learn one
    /// answer.
    /// </summary>
    private void Resettle()
    {
        var taking = Plan.Count(c => c.Included);

        Summary = Describe(taking, Plan.Count);
        Scanned = taking > 0;

        OnPropertyChanged(nameof(CanImport));
    }

    /// <summary>What the folder came to, in the two sentences it takes to say it.</summary>
    private string Describe(int taking, int found) =>
        Lang.Get("importsrc-scanned", taking, found, GameVersion)
        + (_ignored > 0 ? Lang.Plural("importsrc-ignored", _ignored, _ignored) : "");

    /// <summary>Things in the folder that were not mod zips. See <see cref="Describe"/>.</summary>
    private int _ignored;

    // ---- whether the button works ----

    public bool CanImport => !Busy && Source switch
    {
        ImportSource.Install => Scanned && IdProblem is null,
        ImportSource.Link => Url.Trim().Length > 0,
        _ => Text.Trim().Length > 0,
    };

    /// <summary>
    /// Caught on the form rather than after it closes, which is the worst moment to learn
    /// the one thing on here that was fixable. A name that collides needs no message: the
    /// slug it suggests is already made unique against the packs that exist.
    /// </summary>
    public string? IdProblem =>
        Source == ImportSource.Install && string.IsNullOrWhiteSpace(PackName)
            ? Lang.Get("importsrc-needs-name")
            : null;
}
