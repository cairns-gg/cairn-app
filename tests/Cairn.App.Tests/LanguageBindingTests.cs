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

/// <summary>
/// The Preferences picker, which is the only way a person can change language.
/// </summary>
[Collection(AvaloniaTests.Collection)]
public class LanguagePickerTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "cairn-langpick-" + Guid.NewGuid().ToString("n")[..8]);

    public LanguagePickerTests()
    {
        Directory.CreateDirectory(Path.Combine(_home, "packs"));
        Environment.SetEnvironmentVariable("CAIRN_HOME", _home);
        Environment.SetEnvironmentVariable(LanguageChoice.EnvironmentVariable, null);
    }

    public void Dispose()
    {
        Lang.Reset();
        Environment.SetEnvironmentVariable("CAIRN_HOME", null);
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    [AvaloniaFact]
    public void The_picker_offers_automatic_first_and_then_what_ships()
    {
        var picker = new LanguageSettingViewModel();

        Assert.Equal("Automatic", picker.Choices[0]);
        Assert.Contains("English", picker.Choices);

        // Nothing chosen yet, so it sits on Automatic and says what that worked out to.
        Assert.Equal("Automatic", picker.Selected);
        Assert.Contains("Following", picker.Note);
    }

    /// <summary>
    /// Written through CairnSettings.Update, so choosing a language cannot erase the scale —
    /// which is the bug that kept this picker from existing at all.
    /// </summary>
    [AvaloniaFact]
    public void Choosing_one_is_remembered_without_disturbing_the_scale()
    {
        CairnSettings.Update(s => s.UiScale = 1.5);

        new LanguageSettingViewModel().Selected = "English";

        var saved = CairnSettings.Load();
        Assert.Equal("en", saved.Language);
        Assert.Equal(1.5, saved.UiScale);
    }

    [AvaloniaFact]
    public void Going_back_to_automatic_forgets_the_choice()
    {
        var picker = new LanguageSettingViewModel();

        picker.Selected = "English";
        Assert.Equal("en", CairnSettings.Load().Language);

        picker.Selected = "Automatic";
        Assert.Null(CairnSettings.Load().Language);
    }

    /// <summary>
    /// CAIRN_LANG outranks the setting, and the row says so rather than showing a choice
    /// that is not in force.
    /// </summary>
    [AvaloniaFact]
    public void The_environment_says_so_in_the_note()
    {
        Environment.SetEnvironmentVariable(LanguageChoice.EnvironmentVariable, "fr");

        try
        {
            Assert.Contains("CAIRN_LANG", new LanguageSettingViewModel().Note);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LanguageChoice.EnvironmentVariable, null);
        }
    }
}

/// <summary>
/// The picker and CAIRN_LANG_DIR, which were two halves of a workflow that did not meet.
/// </summary>
[Collection(AvaloniaTests.Collection)]
public class LooseTranslationTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "cairn-loose-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly string _lang = Path.Combine(
        Path.GetTempPath(), "cairn-loosel-" + Guid.NewGuid().ToString("n")[..8]);

    public LooseTranslationTests()
    {
        Directory.CreateDirectory(Path.Combine(_home, "packs"));
        Directory.CreateDirectory(_lang);

        File.WriteAllText(Path.Combine(_lang, "fr.json"),
            """{ "_language-name": "Français", "tab-modconfig": "Config des mods" }""");

        Environment.SetEnvironmentVariable("CAIRN_HOME", _home);
        Environment.SetEnvironmentVariable(LanguageChoice.OverrideVariable, _lang);
    }

    public void Dispose()
    {
        Lang.Reset();
        Environment.SetEnvironmentVariable("CAIRN_HOME", null);
        Environment.SetEnvironmentVariable(LanguageChoice.OverrideVariable, null);

        foreach (var dir in new[] { _home, _lang })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// The whole point of the override directory: drop a file in, restart, pick it. It used
    /// to load and never appear, so reaching it needed CAIRN_LANG set as well — which is a
    /// second environment variable for people whose defining trait is not wanting to build
    /// the project.
    /// </summary>
    [AvaloniaFact]
    public void A_translation_dropped_in_the_override_folder_is_offered()
    {
        Assert.Contains("Français", new LanguageSettingViewModel().Choices);
    }

    [AvaloniaFact]
    public void And_choosing_it_translates_the_window()
    {
        var vm = new MainViewModel(new OfflineHandler());
        var window = new MainWindow { DataContext = vm };
        window.Show();

        new LanguageSettingViewModel().Selected = "Français";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal("fr", Lang.Current);
        Assert.Equal("Config des mods", Lang.Get("tab-modconfig"));
        Assert.Equal("fr", CairnSettings.Load().Language);

        // The TabControl's items, not its visual descendants: a tab that has never been
        // selected is not realised, so only the logical list has all five in it.
        var headers = window.GetVisualDescendants().OfType<TabControl>().First()
            .Items.OfType<TabItem>().Select(t => t.Header as string).ToList();

        Assert.Contains("Config des mods", headers);
    }
}
