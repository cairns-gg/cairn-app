using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using Cairn.App.ViewModels;
using Cairn.App.Views;
using Cairn.Core;
using Cairn.Core.Games;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.App.Tests;

/// <summary>
/// Pointing a pack at a client somebody built themselves.
///
/// The reason this exists at all is that Cairn's pinned Optimum revision goes stale between
/// releases, and a player who builds their own should not have to wait for a Cairn update to
/// use it. So the thing being held here is that the control is reachable — including, above
/// all, on the versions Cairn has no revision for, which is exactly where it is needed and
/// exactly where the panel used to be hidden.
///
/// What a picked directory has to be is ClientAdoption's business and is held in the Core
/// suite; this is the window's half.
/// </summary>
[Collection(AvaloniaTests.Collection)]
public class PackOwnClientTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "cairn-ownclient-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly string? _previous = Environment.GetEnvironmentVariable("CAIRN_HOME");

    public PackOwnClientTests()
    {
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("CAIRN_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CAIRN_HOME", _previous);
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    private PackStore Store => new(Path.Combine(_home, "packs"));

    private GameStore Games => new(Path.Combine(_home, "games"));

    private static string LauncherName =>
        OperatingSystem.IsWindows() ? "Optimum.exe" : "Optimum";

    /// <summary>
    /// A build tree of their own, outside anything Cairn owns.
    ///
    /// Named for the version because a forged VintagestoryAPI.dll carries no metadata, and
    /// GameStore falls back to the directory name — which is how the rest of the suite gets
    /// a version onto a fake install.
    /// </summary>
    private string TheirBuild(string version, bool withLauncher = true)
    {
        var dir = Path.Combine(_home, "src", "Optimum", "publish", version);
        Directory.CreateDirectory(dir);

        File.WriteAllText(Path.Combine(dir, OperatingSystem.IsWindows()
            ? "Vintagestory.exe" : "Vintagestory"), "");
        File.WriteAllText(Path.Combine(dir, "VintagestoryAPI.dll"), "");

        if (withLauncher) File.WriteAllText(Path.Combine(dir, LauncherName), "");

        return dir;
    }

    /// <summary>Records it the way the picker would, without needing a real assembly to read.</summary>
    private string Pointed(string version)
    {
        var dir = TheirBuild(version);
        Games.External.Remember(new ExternalClient(dir, "Optimum", LauncherName));
        return dir;
    }

    private (MainWindow Window, MainViewModel Main, PackDetailViewModel Detail) Open(
        string gameVersion)
    {
        new PackManifest { Id = "anego", Name = "Anego", GameVersion = gameVersion, Mods = [] }
            .Save(Store.ManifestPath("anego"));

        var vm = new MainViewModel(new OfflineHandler());
        var window = new MainWindow { DataContext = vm };
        window.Show();

        vm.Confirm = null;
        vm.ConfirmVersionChange = null;
        vm.ConfirmImport = null;
        vm.RunOptimumBuild = null;
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        return (window, vm, vm.Detail!);
    }

    /// <summary>The Settings tab, which a TabControl does not realise until it is selected.</summary>
    private static Control Settings(MainWindow window)
    {
        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
        tabs.SelectedItem = tabs.GetVisualDescendants().OfType<TabItem>()
            .Single(t => (t.Header as string) == "Settings");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        return tabs;
    }

    private static Button Named(MainWindow window, string name) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);

    private static bool Showing(MainWindow window, string name) =>
        Named(window, name).IsEffectivelyVisible;

    [AvaloniaFact]
    public void The_button_is_on_screen_where_cairn_has_no_build_of_its_own()
    {
        // The case the whole feature is for, and the one the old panel hid. Asserted on the
        // visual tree because Avalonia resolves bindings at runtime: a wrong path fails
        // silently and the button simply never appears.
        var (window, _, detail) = Open("1.20.0");

        Assert.False(detail.CanBuildOptimum);

        Settings(window);

        Assert.True(Showing(window, "AdoptClient"));
    }

    [AvaloniaFact]
    public void Cancelling_the_picker_records_nothing()
    {
        var (_, main, detail) = Open("1.20.0");

        main.PickClientFolder = () => Task.FromResult<string?>(null);

        detail.AdoptClientCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Empty(Games.External.All);
        Assert.False(detail.IsUsingExternal);
        Assert.Equal("", detail.AdoptProblem);
    }

    [AvaloniaFact]
    public async Task A_folder_that_is_not_a_client_is_refused_without_a_dialog()
    {
        var (_, main, detail) = Open("1.20.0");

        var stock = TheirBuild("1.20.0", withLauncher: false);

        main.PickClientFolder = () => Task.FromResult<string?>(stock);

        // Nothing may ask, because nothing has been committed: a dialog here would put an OK
        // between somebody and the picker they need to open again.
        main.Confirm = _ => throw new InvalidOperationException("should not have asked");

        await detail.AdoptClientCommand.ExecuteAsync(null);

        Assert.Empty(Games.External.All);
        Assert.True(detail.HasAdoptProblem);
        Assert.Contains("stock game", detail.AdoptProblem);
    }

    [AvaloniaFact]
    public async Task Declining_the_confirmation_records_nothing()
    {
        // The confirmation is not a formality: the risk of the feature is running a binary
        // other than the one being named on screen.
        var (_, main, detail) = Open("1.22.7");

        main.PickClientFolder = () => Task.FromResult<string?>(TheirBuild("1.22.7"));
        main.Confirm = _ => Task.FromResult(false);

        await detail.AdoptClientCommand.ExecuteAsync(null);

        Assert.Empty(Games.External.All);
        Assert.False(detail.IsUsingExternal);
    }

    [AvaloniaFact]
    public void A_recorded_client_is_offered_to_a_pack_with_one_click()
    {
        // Pointed at once, for the machine. Five packs on the same version should not mean
        // opening the same folder five times.
        var dir = Pointed("1.22.7");
        var (_, _, detail) = Open("1.22.7");

        Assert.True(detail.CanUseExternal);
        Assert.False(detail.IsUsingExternal);

        detail.UseExternalCommand.Execute(null);

        Assert.True(detail.IsUsingExternal);
        Assert.True(detail.IsUsingVariant);
        Assert.Equal(dir, detail.ExternalPathLine);

        // Said differently from Cairn's own build, because what follows differs: Cairn's is
        // Cairn's to replace and theirs is not.
        Assert.Contains("your own build", detail.InstallChoiceLine);
    }

    [AvaloniaFact]
    public void A_recorded_client_for_another_version_is_not_offered()
    {
        Pointed("1.22.5");
        var (_, _, detail) = Open("1.22.7");

        Assert.False(detail.CanUseExternal);
        Assert.False(detail.IsUsingExternal);
    }

    [AvaloniaFact]
    public void Building_is_not_offered_to_somebody_who_builds_their_own()
    {
        // They have answered the question the button asks, and a twenty-minute compile of
        // an older revision is not a second opinion worth putting beside their own build.
        Pointed(Cairn.Core.Games.Optimum.OptimumSource.Newest.GameVersion);

        var (_, _, detail) = Open(Cairn.Core.Games.Optimum.OptimumSource.Newest.GameVersion);

        Assert.False(detail.CanBuildOptimum);
        Assert.True(detail.CanUseExternal);
    }

    [AvaloniaFact]
    public void Going_back_to_the_stock_game_is_always_reachable()
    {
        // Without it, pointing at a client would be a decision nothing on screen could undo.
        var (window, _, detail) = Open("1.22.7");
        Pointed("1.22.7");
        detail.RefreshGameState();

        detail.UseExternalCommand.Execute(null);
        Assert.True(detail.IsUsingExternal);

        Settings(window);
        Assert.True(Showing(window, "UseStockGame"));

        // And the picker button becomes "point somewhere else", because that is what
        // pressing it does now.
        Assert.False(Showing(window, "AdoptClient"));
        Assert.True(Showing(window, "RepointClient"));

        detail.UseStockGameCommand.Execute(null);

        Assert.False(detail.IsUsingExternal);
        Assert.False(detail.IsUsingVariant);

        // Forgotten as a choice, not as a client: it is still there to go back to.
        Assert.Single(Games.External.All);
        Assert.True(detail.CanUseExternal);
    }

    [AvaloniaFact]
    public void A_client_that_has_gone_drops_the_pack_back_and_names_the_path()
    {
        // They moved or renamed the directory they built in, which is the whole clue — and
        // the reason this says more than Cairn's own "the install is gone".
        var dir = Pointed("1.22.7");
        var (_, _, detail) = Open("1.22.7");

        detail.UseExternalCommand.Execute(null);
        Assert.True(detail.IsUsingExternal);

        Directory.Delete(dir, recursive: true);
        detail.RefreshGameState();

        Assert.True(detail.ChosenInstallMissing);
        Assert.False(detail.IsUsingVariant);
        Assert.Contains(dir, detail.InstallChoiceLine);
    }
}
