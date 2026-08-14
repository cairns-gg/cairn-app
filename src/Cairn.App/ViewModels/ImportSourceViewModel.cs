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
    }

    /// <summary>Which zip this row is, for matching a verdict to it.</summary>
    public string FileName => mod.FileName;

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
    /// </summary>
    public string Note =>
        candidate is null || candidate.Verdict == ImportVerdict.Ready ? "" : candidate.Note;
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
    private readonly Func<string?, string> _suggestId;
    private readonly IReadOnlySet<string> _disabled;

    /// <summary>Cancels a scan somebody has walked away from.</summary>
    private CancellationTokenSource? _scan;

    /// <param name="modsDir">
    /// The folder to read. The player's own, by default — Cairn only ever reads it.
    /// </param>
    /// <param name="playedOn">
    /// The version of the Vintage Story install this folder belongs to, when there is one.
    ///
    /// It is where the pack's own game version comes from — importing the mods you are
    /// running into a pack for some other version is a thing to ask for, not a default. It
    /// also decides whether an unmarked mod may be imported on the strength of somebody
    /// running it; see <see cref="InstallImport.PlanAsync"/>.
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
        string? playedOn,
        string gameVersion,
        Func<string?, string> suggestId)
    {
        Worlds = new WorldPickerViewModel(savesDir);

        _importer = importer;
        _suggestId = suggestId;
        _disabled = disabled;
        PlayedOn = playedOn;

        // The list frame is drawn only when it has rows. Empty, a bordered scroll area is
        // indistinguishable from a disabled multi-line text box, and the dialog opens in
        // exactly that state — so the first thing it showed was a field you could not type
        // in, above a button you could not press.
        Mods.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRows));

        ModsDir = modsDir;
        GameVersion = playedOn ?? gameVersion;

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

    public string ModsDir { get; }

    /// <summary>
    /// The version of the install the folder belongs to, or null when there is no install
    /// to ask — a folder left behind by a game that has since been moved or removed.
    /// </summary>
    public string? PlayedOn { get; }

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
    public string GameVersion { get; }

    /// <summary>
    /// The worlds in the same install, to bring across with the mods. Copied, never moved:
    /// see <see cref="InstalledWorlds"/>.
    /// </summary>
    public WorldPickerViewModel Worlds { get; }

    /// <summary>Where the mods are coming from and what they will be built for.</summary>
    public string InstallNote => PlayedOn is null
        ? Lang.Get("importsrc-no-install", GameVersion)
        : Lang.Get("importsrc-install-is", PlayedOn);

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
                Summary = Lang.Get("importsrc-no-zips", ModsDir);
                return;
            }

            // The folder, immediately. Reading the zips is instant — it is the lookups that
            // take a moment, and each row says "checking…" until its own comes back.
            foreach (var mod in scan.Mods) Mods.Add(new ImportRowViewModel(mod));

            var checking = Mods.ToDictionary(r => r.FileName, StringComparer.OrdinalIgnoreCase);
            var done = 0;

            Summary = Lang.Get("importsrc-scanning", scan.Mods.Count);

            var plan = await _importer.PlanAsync(
                scan, GameVersion, _disabled, PlayedOn,
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
            Summary = Lang.Get("importsrc-scanned", taking, scan.Mods.Count, GameVersion)
                      + (scan.Ignored.Count > 0
                          ? Lang.Plural("importsrc-ignored", scan.Ignored.Count, scan.Ignored.Count)
                          : "");

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
