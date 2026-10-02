using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using Cairn.App.ViewModels;
using Cairn.App.Views;
using Cairn.Core;
using Xunit;

namespace Cairn.App.Tests;

/// <summary>
/// The local mods folder row in Preferences. What it does at launch is LocalMods', and is
/// tested in Cairn.Core.Tests; what is held here is that the row is drawn, that a choice
/// reaches settings.json, and that it can be taken back.
/// </summary>
[Collection(AvaloniaTests.Collection)]
public class LocalModsPreferenceTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "cairn-localmods-ui-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly string? _previousHome = Environment.GetEnvironmentVariable("CAIRN_HOME");

    public LocalModsPreferenceTests()
    {
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("CAIRN_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CAIRN_HOME", _previousHome);
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    private static PreferencesViewModel Model()
    {
        var main = new MainViewModel(new OfflineHandler());

        PreferencesViewModel? captured = null;
        main.OpenPreferences = p => { captured = p; return Task.CompletedTask; };
        main.ShowPreferencesCommand.Execute(null);

        Assert.NotNull(captured);
        return captured!;
    }

    private static PreferencesWindow ShowOverview(PreferencesViewModel model)
    {
        var window = new PreferencesWindow { DataContext = model };
        window.Show();

        var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
        tabs.SelectedItem = tabs.GetVisualDescendants().OfType<TabItem>()
            .Single(t => (t.Header as string) == "Overview");

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static T Named<T>(PreferencesWindow window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [AvaloniaFact]
    public void Unset_it_says_packs_load_only_their_own()
    {
        var window = ShowOverview(Model());

        Assert.Contains("only its own mods", Named<TextBlock>(window, "LocalModsText").Text);
        Assert.True(Named<Button>(window, "ChooseLocalModsButton").IsEffectivelyVisible);
        Assert.False(Named<Button>(window, "ClearLocalModsButton").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task A_chosen_folder_is_saved_and_shown()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_home, "dev-mods")).FullName;

        var model = Model();
        var window = ShowOverview(model);

        // After showing it: the window hands the model the platform's picker.
        model.PickLocalModsFolder = _ => Task.FromResult<string?>(folder);

        await model.ChooseLocalModsCommand.ExecuteAsync(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(folder, CairnSettings.Load().LocalModsPath);
        Assert.Equal(folder, Named<TextBlock>(window, "LocalModsText").Text);
        Assert.True(Named<Button>(window, "ClearLocalModsButton").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Cancelling_the_picker_keeps_what_was_there()
    {
        CairnSettings.Update(s => s.LocalModsPath = "/somewhere");

        var model = Model();
        model.PickLocalModsFolder = _ => Task.FromResult<string?>(null);

        await model.ChooseLocalModsCommand.ExecuteAsync(null);

        Assert.Equal("/somewhere", CairnSettings.Load().LocalModsPath);
    }

    [AvaloniaFact]
    public void Stopping_clears_it_and_leaves_the_other_settings_alone()
    {
        CairnSettings.Update(s =>
        {
            s.LocalModsPath = "/somewhere";
            s.Language = "fr";
        });

        var model = Model();
        Assert.True(model.HasLocalMods);

        model.ClearLocalModsCommand.Execute(null);

        Assert.False(model.HasLocalMods);
        Assert.Null(CairnSettings.Load().LocalModsPath);
        Assert.Equal("fr", CairnSettings.Load().Language);
    }
}
