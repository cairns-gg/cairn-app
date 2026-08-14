using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Cairn.App.ViewModels;
using Cairn.App.Views;
using Cairn.Core;
using Xunit;

namespace Cairn.App.Tests;

/// <summary>
/// That a translation actually reaches the screen.
///
/// The language file here is written by the test rather than shipped. Cairn ships English
/// only, and inventing a German one to prove the machinery works would put a translation
/// nobody wrote into the product — where it would be read as the real thing, and where the
/// coverage test would then hold it to a standard no machine translation meets.
/// </summary>
[Collection(AvaloniaTests.Collection)]
public class LanguageBindingTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "cairn-langui-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "cairn-langhome-" + Guid.NewGuid().ToString("n")[..8]);

    public LanguageBindingTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Path.Combine(_home, "packs"));

        File.WriteAllText(Path.Combine(_dir, "xx.json"), """
        {
          "tab-modconfig": "Mod-Konfiguration",
          "tab-hotkeys": "Tastenkürzel"
        }
        """);

        Environment.SetEnvironmentVariable("CAIRN_HOME", _home);
    }

    public void Dispose()
    {
        // Back to English before the next test in this collection, which shares the process
        // and would otherwise inherit whatever this left behind.
        Lang.Reset();

        Environment.SetEnvironmentVariable("CAIRN_HOME", null);
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    private static List<string?> TabHeaders(Window window) =>
        window.GetVisualDescendants().OfType<TabControl>().First()
            .Items.OfType<TabItem>().Select(t => t.Header as string).ToList();

    [AvaloniaFact]
    public void A_translated_string_reaches_the_window()
    {
        Lang.Use("xx", _dir);

        var vm = new MainViewModel(new OfflineHandler());
        var window = new MainWindow { DataContext = vm };
        window.Show();

        var headers = TabHeaders(window);

        Assert.Contains("Mod-Konfiguration", headers);
        Assert.Contains("Tastenkürzel", headers);

        // And what the translation has not reached stays readable rather than going blank.
        Assert.Contains("Mods", headers);
    }

    /// <summary>
    /// The whole reason the markup binds through an indexer rather than reading the string
    /// once. A language that only applied to windows opened afterwards would make the setting
    /// feel like something you restart for.
    /// </summary>
    [AvaloniaFact]
    public void Changing_language_reaches_a_window_that_is_already_open()
    {
        var vm = new MainViewModel(new OfflineHandler());
        var window = new MainWindow { DataContext = vm };
        window.Show();

        Assert.Contains("Mod config", TabHeaders(window));

        Lang.Use("xx", _dir);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Contains("Mod-Konfiguration", TabHeaders(window));
    }

    [AvaloniaFact]
    public void Going_back_to_English_restores_every_label()
    {
        var vm = new MainViewModel(new OfflineHandler());
        var window = new MainWindow { DataContext = vm };
        window.Show();

        Lang.Use("xx", _dir);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Contains("Mod-Konfiguration", TabHeaders(window));

        Lang.Reset();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Contains("Mod config", TabHeaders(window));
    }
}
