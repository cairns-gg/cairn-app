using Avalonia.Headless.XUnit;
using Cairn.App.ViewModels;
using Cairn.App.Views;
using Cairn.Core;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.App.Tests;

/// <summary>
/// Choosing which install a pack launches with.
///
/// The rule being held is that a modified client only ever runs because somebody said so.
/// Core already refuses to return one from any automatic path; this is the other half —
/// that the launcher records a choice, honours it, and copes when the build behind it goes
/// away without leaving a pack that will not start and will not say why.
///
/// The choice is made by the Optimum panel's buttons rather than a list of installs; see
/// PackOptimumTests for that. What is held here is what the choice means once recorded.
/// </summary>
[Collection(AvaloniaTests.Collection)]
public class PackInstallChoiceTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "cairn-install-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly string? _previous = Environment.GetEnvironmentVariable("CAIRN_HOME");

    public PackInstallChoiceTests()
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

    /// <summary>Enough of an install for TryAt, optionally marked as a modified build.</summary>
    private string Install(string name, string? variant = null)
    {
        var dir = Games.DirIn(Path.Combine(_home, "games"), name);
        Directory.CreateDirectory(dir);

        File.WriteAllText(Path.Combine(dir, OperatingSystem.IsWindows()
            ? "Vintagestory.exe" : "Vintagestory"), "");
        File.WriteAllText(Path.Combine(dir, "VintagestoryAPI.dll"), "");

        if (variant is not null)
            File.WriteAllText(Path.Combine(dir, GameInstall.VariantMarker), variant);

        return dir;
    }

    private PackDetailViewModel Open()
    {
        new PackManifest { Id = "anego", Name = "Anego", GameVersion = "1.22.5", Mods = [] }
            .Save(Store.ManifestPath("anego"));

        var vm = new MainViewModel(new OfflineHandler());
        new MainWindow { DataContext = vm }.Show();

        vm.Confirm = null;
        vm.ConfirmVersionChange = null;
        vm.ConfirmImport = null;
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        return vm.Detail!;
    }

    [AvaloniaFact]
    public void A_pack_with_no_choice_offers_no_picker()
    {
        // The ordinary case, and the reason the picker is conditional: one install for the
        // version means there is nothing to ask.
        var detail = Open();

        Assert.Null(detail.ChosenInstall);
        Assert.False(detail.ChosenInstallMissing);
        Assert.False(detail.HasInstallNote);
        Assert.Equal("", detail.InstallChoiceLine);
    }

    [AvaloniaFact]
    public void A_variant_runs_only_once_the_pack_has_been_told_to_use_it()
    {
        var optimum = Install("1.22.5-optimum", "Optimum");
        var detail = Open();

        // Present on the machine and still not running: nothing picks a modified client
        // on somebody's behalf.
        Assert.Null(detail.ChosenInstall);

        detail.ChooseInstall(GameInstall.TryAt(optimum));

        Assert.NotNull(detail.ChosenInstall);
        Assert.Equal("Optimum", detail.ChosenInstall.Variant);
        Assert.Equal(optimum, detail.ResolvedInstall!.Directory);
        Assert.Contains("Optimum", detail.InstallChoiceLine);

        // Recorded where it cannot travel: a path on this machine is meaningless to
        // anybody a pack is published to.
        Assert.Equal(optimum, Store.LoadLocalState("anego").InstallDirectory);
    }

    [AvaloniaFact]
    public void Choosing_the_stock_install_clears_the_choice_rather_than_pinning_it()
    {
        var optimum = Install("1.22.5-optimum", "Optimum");
        var stock = Install("1.22.5");

        var detail = Open();
        detail.ChooseInstall(GameInstall.TryAt(optimum));
        Assert.NotNull(Store.LoadLocalState("anego").InstallDirectory);

        detail.ChooseInstall(GameInstall.TryAt(stock));

        // No choice at all, not a pinned path: a pack bound to the stock directory would
        // still be bound to it after Cairn replaced that directory on the next update.
        Assert.Null(Store.LoadLocalState("anego").InstallDirectory);
        Assert.Null(detail.ChosenInstall);
    }

    [AvaloniaFact]
    public void An_install_that_has_gone_falls_back_and_says_so()
    {
        var optimum = Install("1.22.5-optimum", "Optimum");
        var detail = Open();
        detail.ChooseInstall(GameInstall.TryAt(optimum));

        Directory.Delete(optimum, recursive: true);

        // Nobody deletes a build and connects it to a pack refusing to start, so this
        // reports rather than failing — and it has to be said out loud, because the pack
        // silently reverting to the stock game is otherwise invisible.
        Assert.True(detail.ChosenInstallMissing);
        Assert.Null(detail.ChosenInstall);
        Assert.True(detail.HasInstallNote);
        Assert.Contains("gone", detail.InstallChoiceLine);
    }
}
