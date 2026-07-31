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

    public string CairnHome => CairnPaths.Root;
    public string SharedDataPath => GameInstall.DefaultDataPath;
    public string GameInstallPath => GameInstall.TryLocate()?.Directory ?? "(not found)";

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
        TotalSize = Human(games + runtimes + cache + packs);

        var installed = _games.ListInstalled().Count();
        GamesDetail = Count(installed, "version");

        RuntimesDetail = Count(_runtimes.ListInstalled().Count(), "runtime");
        PacksDetail = Count(_store.ListIds().Count(), "pack");
    }

    /// <summary>Set by the view; the same confirmation dialog the pack delete uses.</summary>
    public Func<ConfirmViewModel, Task<bool>>? Confirm { get; set; }

    [ObservableProperty] public partial string CleanupSummary { get; set; } = "";

    /// <summary>
    /// True while files are being deleted. Removing several gigabytes takes real time, and
    /// doing it on the UI thread froze the window — which reads as a hang, not as work.
    /// </summary>
    [ObservableProperty] public partial bool IsCleaningUp { get; set; }

    [ObservableProperty] public partial string CleanupStage { get; set; } = "";

    public bool NotCleaningUp => !IsCleaningUp;

    partial void OnIsCleaningUpChanged(bool value)
    {
        OnPropertyChanged(nameof(NotCleaningUp));
        CleanUpCommand.NotifyCanExecuteChanged();
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
        var plan = GameCleanup.Plan(_games, _runtimes, _store);

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

    /// <summary>
    /// Empties the icon and mod-detail caches. Safe by construction — everything in there
    /// is re-fetchable, which is why it lives apart from packs and games.
    /// </summary>
    [RelayCommand]
    private void ClearCache()
    {
        _icons.Clear();
        _modInfo.Clear();
        Refresh();
    }

    private static string Count(int n, string noun) =>
        n == 0 ? $"no {noun}s" : $"{n} {noun}{(n == 1 ? "" : "s")}";

    private static long DirectorySize(string path) => DirectoryGrowth.Measure(path);

    /// <summary>Sizes people can read: a game version is gigabytes, a cache is kilobytes.</summary>
    public static string Human(long bytes) => Bytes.Human(bytes);
}
