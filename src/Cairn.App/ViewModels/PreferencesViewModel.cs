using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cairn.Core;
using Cairn.Core.Games;
using Cairn.Core.ModDb;
using Cairn.Core.Runtime;
using Cairn.Core.Packs;

namespace Cairn.App.ViewModels;

/// <summary>
/// Application settings, as opposed to a pack's own.
///
/// Lives in its own window because everything here is about Cairn rather than about the
/// pack you happen to have selected — mixing the two into the pack pane is what made
/// "Game versions" sit oddly beside "New pack".
/// </summary>
public partial class PreferencesViewModel : ViewModelBase
{
    private readonly PackStore _store;
    private readonly GameStore _games;
    private readonly RuntimeStore _runtimes;
    private readonly ModIconCache _icons;
    private readonly ModInfoCache _modInfo;

    public PreferencesViewModel(
        GamesViewModel games,
        PackStore store,
        GameStore gameStore,
        RuntimeStore runtimes,
        ModIconCache icons,
        ModInfoCache modInfo)
    {
        Games = games;
        _store = store;
        _games = gameStore;
        _runtimes = runtimes;
        _icons = icons;
        _modInfo = modInfo;

        Refresh();
    }

    /// <summary>The existing game-version screen, now a section rather than a destination.</summary>
    public GamesViewModel Games { get; }

    [ObservableProperty] public partial string GamesSize { get; set; } = "";
    [ObservableProperty] public partial string RuntimesSize { get; set; } = "";
    [ObservableProperty] public partial string CacheSize { get; set; } = "";
    [ObservableProperty] public partial string PacksSize { get; set; } = "";
    [ObservableProperty] public partial string TotalSize { get; set; } = "";

    [ObservableProperty] public partial string GamesDetail { get; set; } = "";
    [ObservableProperty] public partial string RuntimesDetail { get; set; } = "";
    [ObservableProperty] public partial string PacksDetail { get; set; } = "";

    // ---- how large the interface is drawn ----

    /// <summary>Offered as steps rather than a slider: these are the ones worth picking.</summary>
    public IReadOnlyList<string> ScaleChoices { get; } =
        [.. UiScale.Choices.Select(UiScale.Describe)];

    /// <summary>
    /// Applied as you pick it rather than on a Save, because the only way to know whether
    /// a size is comfortable is to look at it.
    /// </summary>
    [ObservableProperty] public partial string SelectedScale { get; set; } = UiScale.Describe(UiScale.Current);

    partial void OnSelectedScaleChanged(string value)
    {
        var chosen = UiScale.Choices.FirstOrDefault(c => UiScale.Describe(c) == value);
        if (chosen == 0) return;

        UiScale.Current = chosen;
        UiScale.Save();
    }

    /// <summary>
    /// Which version this is. Read from the assembly rather than held in a constant, so a
    /// build cannot claim a number nobody stamped — see <see cref="CairnVersion"/>.
    /// </summary>
    public string Version => CairnVersion.Current;

    public string CairnHome => CairnPaths.Root;

    /// <summary>
    /// Recomputes the storage picture. Directory walks, so it is done on demand rather
    /// than bound live — this window is opened deliberately, not left watching.
    /// </summary>
    [RelayCommand]
    public void Refresh()
    {
        var games = DirectorySize(CairnPaths.GamesRoot);
        var runtimes = DirectorySize(CairnPaths.RuntimesRoot);
        var cache = DirectorySize(CairnPaths.CacheRoot);
        var packs = DirectorySize(CairnPaths.PacksRoot);

        GamesSize = Human(games);
        RuntimesSize = Human(runtimes);
        CacheSize = Human(cache);
        PacksSize = Human(packs);
        // Reported separately from games because it is neither a game nor a cache: it is
        // the largest thing Cairn writes and the only one that does not come back on its
        // own, so leaving it out of the totals made several gigabytes unaccountable.
        // Its own row rather than folded into the caches, because it is the one people
        // have a reason to clear on purpose: a mod author publishes a release, and Cairn
        // goes on reporting the answer it remembered for another few minutes.
        var checks = new ModUpdateCache();
        UpdateChecksDetail = checks.Count() == 0
            ? "none remembered"
            : $"{Count(checks.Count(), "pack")}, kept {ModUpdateCache.Lifetime.TotalMinutes:0} minutes";
        HasUpdateChecks = checks.Count() > 0;

        BuildTrees = GameCleanup.BuildTreesUnder(CairnPaths.BuildsRoot);
        var builds = BuildTrees.Sum(t => t.Bytes);

        BuildsSize = Human(builds);
        HasBuildTrees = builds > 0;
        BuildsDetail = BuildTrees.Count == 0
            ? "none"
            : string.Join(", ", BuildTrees.Select(t => t.Label));

        TotalSize = Human(games + runtimes + cache + packs + builds);

        var installed = _games.ListInstalled().Count();
        GamesDetail = Count(installed, "version");

        RuntimesDetail = Count(_runtimes.ListInstalled().Count(), "runtime");
        PacksDetail = Count(_store.ListIds().Count(), "pack");
    }

    [ObservableProperty] public partial string UpdateChecksDetail { get; set; } = "";
    [ObservableProperty] public partial bool HasUpdateChecks { get; set; }

    /// <summary>
    /// Forgets what "check for mod updates" last answered, so the next check asks ModDB.
    ///
    /// No confirmation: it deletes a few kilobytes that rebuild themselves on the next
    /// press, which is the one thing on this page that genuinely costs nothing to undo.
    /// </summary>
    [RelayCommand]
    private void ClearUpdateChecks()
    {
        new ModUpdateCache().Clear();
        CleanupSummary = "Forgot the remembered update checks; the next check will ask ModDB.";
        Refresh();
    }

    [ObservableProperty] public partial string BuildsSize { get; set; } = "";
    [ObservableProperty] public partial string BuildsDetail { get; set; } = "";
    [ObservableProperty] public partial bool HasBuildTrees { get; set; }

    private IReadOnlyList<CleanupTarget> BuildTrees { get; set; } = [];

    /// <summary>
    /// Deletes the working trees, keeping any client built from them.
    ///
    /// Its own action rather than part of Clean up, because it fails that sweep's promise:
    /// what it removes does not come back on its own, it comes back after twenty minutes of
    /// compiling. Worth offering all the same — between pin bumps it is gigabytes doing
    /// nothing.
    /// </summary>
    [RelayCommand(CanExecute = nameof(NotCleaningUp))]
    private async Task RemoveBuildTrees()
    {
        if (BuildTrees.Count == 0) return;

        var total = BuildTrees.Sum(t => t.Bytes);

        var message = "This deletes:\n"
                      + string.Join("\n", BuildTrees.Select(
                          t => $"  • {t.Label} build tree ({Bytes.Human(t.Bytes)})"))
                      + $"\n\nFrees {Bytes.Human(total)}. Any client already built from them "
                      + "is kept and goes on working.\n\nRebuilding one takes 15–30 minutes, "
                      + "so this is worth doing only if you need the space.";

        if (Confirm is not null
            && !await Confirm(new ConfirmViewModel("Remove build trees?", message, "Remove")))
            return;

        IsCleaningUp = true;

        try
        {
            foreach (var tree in BuildTrees)
                await Task.Run(() =>
                {
                    try { Directory.Delete(tree.Directory, recursive: true); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                });

            CleanupSummary = $"Removed the build trees, freeing {Bytes.Human(total)}.";
        }
        finally
        {
            IsCleaningUp = false;
            Refresh();
        }
    }

    /// <summary>Set by the view; the same confirmation dialog the pack delete uses.</summary>
    public Func<ConfirmViewModel, Task<bool>>? Confirm { get; set; }

    /// <summary>
    /// Asks for a directory, returning null if the user thought better of it. Set by the
    /// view because picking a folder is the platform's job and this is a view model —
    /// which also means a test can answer it without a dialog.
    /// </summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    /// <summary>
    /// Whether CAIRN_HOME is what decided the root, in which case moving from here cannot
    /// work: the pointer would be written and then outranked by the variable.
    ///
    /// Fully qualified because this class has a CairnHome property of its own, which
    /// shadows the type.
    /// </summary>
    public bool HomeIsFromEnvironment =>
        Cairn.Core.CairnHome.Resolve().Source is HomeSource.Environment;

    /// <summary>
    /// Refused before the folder picker rather than after it.
    ///
    /// It used to be enabled: you chose a directory, waited for the dialog, and were then
    /// told the setting would be ignored. The people that wasted are precisely the ones who
    /// wanted this — the README told them to set CAIRN_HOME to move the directory long
    /// before there was a button, so the ones who worked around its absence are the ones who
    /// had followed the advice.
    /// </summary>
    public bool CanMoveHome => NotCleaningUp && !HomeIsFromEnvironment;

    /// <summary>True while the tree is being copied. See <see cref="IsCleaningUp"/>.</summary>
    [ObservableProperty] public partial bool IsMovingHome { get; set; }

    [ObservableProperty] public partial string MoveStage { get; set; } = "";

    /// <summary>How far the copy has got, 0 to 100, for the bar beside the text.</summary>
    [ObservableProperty] public partial double MovePercent { get; set; }


    /// <summary>
    /// What to do with the old copy, once there is an old copy. Empty until then.
    ///
    /// Left on screen rather than said once in a toast: somebody who has just moved 40 GB
    /// off a full disk needs to know that the 40 GB is still on it, and needs to be able to
    /// read it again after they have gone and looked.
    /// </summary>
    [ObservableProperty] public partial string MoveAftermath { get; set; } = "";

    /// <summary>
    /// Moves everything Cairn keeps to a directory the user chooses.
    ///
    /// Every rule is <see cref="HomeMigration"/>'s — what can be refused, what gets copied,
    /// what gets rewritten, and that Cairn is repointed only once it has all arrived. This
    /// asks where, shows what it will cost, and reports what happened.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMoveHome))]
    private async Task MoveHome()
    {
        if (PickFolder is null) return;

        if (await PickFolder() is not { } chosen) return;

        var plan = await Task.Run(() => HomeMigration.Plan(chosen));

        // A refusal is the ordinary outcome of choosing the wrong folder, not an error —
        // the picker will happily offer a disk with no room on it, or the directory Cairn
        // is already using.
        if (!plan.CanMove)
        {
            MoveStage = "";
            MoveAftermath = plan.Problem!;
            return;
        }

        var message = $"Copies everything Cairn keeps to:\n\n  {plan.To}\n\n"
                      + $"{plan.Files} files, {HomeMigration.Describe(plan.Bytes)}"
                      + (plan.Links > 0 ? $", and {plan.Links} links kept as links" : "")
                      + ".\n\nEverything is copied and checked file by file, Cairn is "
                      + "repointed, and only then is the original at " + plan.From
                      + " deleted.\n\nNothing is removed until the new copy has been "
                      + "verified — but when this finishes, the old one is gone.";

        if (Confirm is not null
            && !await Confirm(new ConfirmViewModel("Move Cairn's files?", message, "Move")))
            return;

        IsMovingHome = true;
        MoveAftermath = "";

        try
        {
            // Throttled to whole percents. Reported per file, a root with tens of thousands
            // of them would post that many updates to the UI thread, each one a property
            // change and a text layout — the progress display becoming the reason the copy
            // is slow.
            var lastPercent = -1;

            var progress = new Progress<MoveProgress>(p =>
            {
                var percent = p.BytesTotal == 0 ? 100 : (int)(100 * p.Bytes / p.BytesTotal);
                if (percent == lastPercent) return;

                lastPercent = percent;
                MovePercent = percent;
                MoveStage = $"{percent}% — {p.Files} of {p.FilesTotal} files";
            });

            var result = await Task.Run(() => HomeMigration.Move(plan, progress));

            MoveAftermath = result.RemovalProblem is { } stuck
                // The move worked. Saying so first matters: somebody told only that a
                // deletion failed will go looking for data that is exactly where it should be.
                ? $"Moved to {plan.To}, checked file by file. The original at "
                  + $"{result.OldRoot} could not be removed ({stuck}), so it is still using "
                  + $"{HomeMigration.Describe(result.Bytes)} — delete it by hand when you can."
                  + (result.KeepInOldRoot is { } keep
                      ? $" Keep {keep}, which is what points Cairn at the new location."
                      : "")
                : $"Moved to {plan.To}, checked file by file, and removed the original — "
                  + $"{HomeMigration.Describe(result.Freed)} freed.";
        }
        catch (Exception e) when (e is MoveFailed or IOException or UnauthorizedAccessException)
        {
            // Nothing was repointed — that is the whole design — so the old root is still
            // live and saying so is the useful half of the message.
            // CairnPaths.Root, not CairnHome.Resolve() — this class has a CairnHome property
            // of its own, which shadows the type. Same answer either way.
            MoveAftermath = $"{e.Message}\n\nCairn is still using {CairnPaths.Root}.";
        }
        finally
        {
            IsMovingHome = false;
            MoveStage = "";
            OnPropertyChanged(nameof(CairnHome));
            Refresh();
        }
    }

    [ObservableProperty] public partial string CleanupSummary { get; set; } = "";

    /// <summary>
    /// True while files are being deleted. Removing several gigabytes takes real time, and
    /// doing it on the UI thread froze the window — which reads as a hang, not as work.
    /// </summary>
    [ObservableProperty] public partial bool IsCleaningUp { get; set; }

    [ObservableProperty] public partial string CleanupStage { get; set; } = "";

    /// <summary>
    /// Gates every button that touches files, the move included, so none of them can start
    /// while another is running. A sweep deleting game versions while a copy is reading them
    /// is not a case worth having.
    /// </summary>
    public bool NotCleaningUp => !IsCleaningUp && !IsMovingHome;

    partial void OnIsCleaningUpChanged(bool value) => BusyChanged();

    partial void OnIsMovingHomeChanged(bool value) => BusyChanged();

    private void BusyChanged()
    {
        OnPropertyChanged(nameof(NotCleaningUp));
        OnPropertyChanged(nameof(CanMoveHome));
        CleanUpCommand.NotifyCanExecuteChanged();
        RemoveBuildTreesCommand.NotifyCanExecuteChanged();
        MoveHomeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Removes game versions no pack targets, and any private .NET runtime left with
    /// nothing to run.
    ///
    /// Safe to offer because none of it is irreplaceable — every version is
    /// re-downloadable and Play fetches whatever a pack needs — but it is still shown in
    /// full first, because "600 MB is gone" is not something to discover afterwards.
    /// </summary>
    [RelayCommand(CanExecute = nameof(NotCleaningUp))]
    private async Task CleanUp()
    {
        var cacheBytes = DirectorySize(CairnPaths.CacheRoot);

        var plan = GameCleanup.Plan(_games, _runtimes, _store) with
        {
            Caches = cacheBytes > 0
                ? [new CleanupTarget("cached icons and mod details", CairnPaths.CacheRoot, cacheBytes)]
                : [],
        };

        if (plan.IsBlocked)
        {
            CleanupSummary = plan.Blocked!;
            return;
        }

        if (!plan.AnythingToDo)
        {
            CleanupSummary = "Nothing to clean up — every installed version is in use.";
            return;
        }

        var lines = plan.Describe();
        var kept = plan.Kept.Count == 0
            ? ""
            : $"\n\nKeeps {string.Join(", ", plan.Kept)}, which packs still target.";

        var message = "This deletes:\n"
                      + string.Join("\n", lines.Select(l => "  • " + l))
                      + $"\n\nFrees {Bytes.Human(plan.TotalBytes)}. "
                      + "Any of it downloads again when a pack needs it."
                      + kept;

        if (Confirm is not null
            && !await Confirm(new ConfirmViewModel("Clean up unused downloads?", message, "Clean up")))
            return;

        IsCleaningUp = true;
        CleanupSummary = "";

        var removed = 0;
        var failures = new List<string>();
        var stage = new Progress<string>(s => CleanupStage = s);

        try
        {
            // Off the UI thread: this is gigabytes of file deletion, and the window has to
            // stay responsive enough to show that it is working.
            await Task.Run(() =>
            {
                var report = (IProgress<string>)stage;

                foreach (var version in plan.Versions)
                {
                    report.Report($"removing Vintage Story {version.Label}…");
                    try
                    {
                        _games.Remove(GameInstall.TryAt(version.Directory)
                                      ?? throw new InvalidOperationException("it is no longer there"));
                        removed++;
                    }
                    catch (Exception e)
                    {
                        failures.Add($"{version.Label}: {e.Message}");
                    }
                }

                foreach (var runtime in plan.Runtimes)
                {
                    report.Report($"removing .NET {runtime.Label}…");
                    try
                    {
                        _runtimes.Remove(DotnetRuntimeLocator.Inspect(runtime.Directory)
                                         ?? throw new InvalidOperationException("it is no longer there"));
                        removed++;
                    }
                    catch (Exception e)
                    {
                        failures.Add($"{runtime.Label}: {e.Message}");
                    }
                }

                foreach (var cache in plan.Caches)
                {
                    report.Report("emptying caches…");
                    try
                    {
                        _icons.Clear();
                        _modInfo.Clear();
                        removed++;
                    }
                    catch (Exception e)
                    {
                        failures.Add($"{cache.Label}: {e.Message}");
                    }
                }
            });
        }
        finally
        {
            IsCleaningUp = false;
            CleanupStage = "";
        }

        Games.RefreshInstalled();
        Refresh();

        CleanupSummary = failures.Count > 0
            ? $"Removed {removed}; could not remove {string.Join("; ", failures)}."
            : $"Removed {removed} item{(removed == 1 ? "" : "s")}, freeing {Bytes.Human(plan.TotalBytes)}.";
    }

    private static string Count(int n, string noun) =>
        n == 0 ? $"no {noun}s" : $"{n} {noun}{(n == 1 ? "" : "s")}";

    private static long DirectorySize(string path) => DirectoryGrowth.Measure(path);

    /// <summary>Sizes people can read: a game version is gigabytes, a cache is kilobytes.</summary>
    public static string Human(long bytes) => Bytes.Human(bytes);
}
