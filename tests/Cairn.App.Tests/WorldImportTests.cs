using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Cairn.App.ViewModels;
using Cairn.App.Views;
using Xunit;

namespace Cairn.App.Tests;

/// <summary>
/// Choosing worlds to bring out of a plain Vintage Story install and into a pack.
///
/// Nothing is ticked when the list appears. The mods are the pack and arrive with it; a
/// world is gigabytes of somebody's save, the pack works without it, and copying one is a
/// thing to ask for rather than a thing to discover afterwards.
/// </summary>
[Collection(AvaloniaTests.Collection)]
public class WorldImportTests : IDisposable
{
    private readonly string _saves = Path.Combine(
        Path.GetTempPath(), "cairn-worldpick-" + Guid.NewGuid().ToString("n")[..8], "Saves");

    public WorldImportTests() => Directory.CreateDirectory(_saves);

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_saves)!;
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private void WriteWorld(string name, int bytes = 2 * 1024 * 1024) =>
        File.WriteAllBytes(Path.Combine(_saves, name + ".vcdbs"), new byte[bytes]);

    private static IEnumerable<string> VisibleText(Visual root) =>
        root.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text))
            .Select(t => t.Text!);

    [AvaloniaFact]
    public void Nothing_is_ticked_when_the_list_appears()
    {
        WriteWorld("Awesome Kingdom Tales");
        WriteWorld("Old World");

        var picker = new WorldPickerViewModel(_saves);

        Assert.Equal(2, picker.Worlds.Count);
        Assert.All(picker.Worlds, w => Assert.False(w.Chosen));
        Assert.Empty(picker.Chosen);
        Assert.False(picker.HasChosen);
        Assert.Contains("Tick any you want a copy of", picker.Summary);
    }

    [AvaloniaFact]
    public void Ticking_a_world_says_what_it_will_cost_and_what_it_will_not()
    {
        WriteWorld("Awesome Kingdom Tales", bytes: 3 * 1024 * 1024);

        var picker = new WorldPickerViewModel(_saves);
        picker.Worlds[0].Chosen = true;

        Assert.Contains("Copying 1 world (3 MB)", picker.Summary);

        // The sentence that makes this safe to press, on the screen where it is pressed.
        Assert.Contains("Your own copies stay where they are", picker.Summary);
        Assert.True(picker.HasChosen);
        Assert.Equal("Awesome Kingdom Tales", Assert.Single(picker.Chosen).Name);
    }

    [AvaloniaFact]
    public void An_install_with_no_worlds_says_so()
    {
        var picker = new WorldPickerViewModel(_saves);

        Assert.False(picker.Any);
        Assert.Contains("No worlds", picker.Summary);
    }

    [AvaloniaFact]
    public void The_window_lists_every_world_with_its_size()
    {
        WriteWorld("Awesome Kingdom Tales", bytes: 4 * 1024 * 1024);
        WriteWorld("Old World", bytes: 1024 * 1024);

        var picker = new WorldPickerViewModel(_saves);
        var window = new WorldImportWindow { DataContext = picker };
        window.Show();

        var text = VisibleText(window).ToList();

        Assert.Contains(text, t => t.Contains("Awesome Kingdom Tales"));
        Assert.Contains(text, t => t == "4 MB");
        Assert.Contains(text, t => t == "1 MB");

        // Copying is off until something is ticked.
        var copy = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CopyButton");
        Assert.False(copy.IsEnabled);

        picker.Worlds[0].Chosen = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(copy.IsEnabled);
    }
}
