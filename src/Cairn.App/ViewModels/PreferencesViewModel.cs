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

    private static long DirectorySize(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return 0;

            return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => { try { return f.Length; } catch (IOException) { return 0L; } });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Sizes people can read: a game version is gigabytes, a cache is kilobytes.</summary>
    public static string Human(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F0} KB",
        _ => $"{bytes} B",
    };
}
