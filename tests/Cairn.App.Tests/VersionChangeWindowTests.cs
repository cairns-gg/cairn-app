using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Cairn.App.ViewModels;
using Cairn.App.Views;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.App.Tests;

/// <summary>
/// The confirmation dialog. It exists because a pack can have dozens of mods: inline in
/// the Settings tab, a long verdict list pushed the buttons that act on it out of reach.
/// </summary>
public class VersionChangeWindowTests
{
    private static VersionChangeViewModel Change(int mods, bool downgrade = false)
    {
        var verdicts = Enumerable.Range(1, mods)
            .Select(i => new ModVerdict(
                $"mod-{i:00}", "1.0.0", "2.0.0", ModOutcome.Moves, "1.0.0 → 2.0.0"))
            .ToList();

        return new VersionChangeViewModel(new VersionChangePlan(
            downgrade ? "1.22.6" : "1.22.5",
            downgrade ? "1.22.5" : "1.22.6",
            verdicts,
            Worlds: []));
    }

    private static (VersionChangeWindow Window, ScrollViewer List) Show(VersionChangeViewModel change)
    {
        var window = new VersionChangeWindow { DataContext = change };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        return (window, window.GetVisualDescendants().OfType<ScrollViewer>().Single());
    }

    private static Button Find(Visual root, string name) =>
        root.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);

    [AvaloniaFact]
    public void A_long_list_scrolls_instead_of_growing_the_window()
    {
        var (window, list) = Show(Change(mods: 60));

        // The list is taller than the space it has, and that space is finite.
        Assert.True(list.Extent.Height > list.Viewport.Height,
            $"list did not overflow: extent {list.Extent.Height}, viewport {list.Viewport.Height}");

        Assert.True(list.Viewport.Height <= window.Height);
    }

    [AvaloniaFact]
    public void The_buttons_stay_in_view_however_many_mods_there_are()
    {
        // The whole reason this is a dialog: with 60 mods inline, Apply was off the bottom
        // of a tab that did not scroll.
        var (window, _) = Show(Change(mods: 60));

        foreach (var name in new[] { "ApplyButton", "CancelButton" })
        {
            var button = Find(window, name);
            var bounds = button.Bounds;
            var position = button.TranslatePoint(default, window)!.Value;

            Assert.True(button.IsEffectivelyVisible, $"{name} is not visible");
            Assert.True(position.Y + bounds.Height <= window.Height,
                $"{name} is below the window bottom: {position.Y + bounds.Height} > {window.Height}");
        }
    }

    [AvaloniaFact]
    public void A_short_list_does_not_scroll()
    {
        var (_, list) = Show(Change(mods: 2));

        Assert.True(list.Extent.Height <= list.Viewport.Height);
    }

    [AvaloniaFact]
    public void Every_mod_is_present_however_long_the_list()
    {
        // Scrolling, not truncation: a verdict that was silently dropped is the one that
        // would have changed the decision.
        var change = Change(mods: 60);
        var (window, _) = Show(change);

        Assert.Equal(60, change.Mods.Count);

        var rendered = window.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text).ToList();

        Assert.Contains("mod-01", rendered);
        Assert.Contains("mod-60", rendered);
    }

    [AvaloniaTheory]
    [InlineData("ApplyButton", true)]
    [InlineData("CancelButton", false)]
    public async Task Only_Apply_closes_with_a_yes(string button, bool expected)
    {
        var owner = new Window();
        owner.Show();

        var window = new VersionChangeWindow { DataContext = Change(mods: 3) };
        var result = window.ShowDialog<bool>(owner);

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Find(window, button).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(expected, await result);
    }

    [AvaloniaFact]
    public async Task Closing_the_dialog_any_other_way_leaves_the_pack_alone()
    {
        // Escape, the title bar: anything that is not Apply must not count as consent.
        var owner = new Window();
        owner.Show();

        var window = new VersionChangeWindow { DataContext = Change(mods: 3) };
        var result = window.ShowDialog<bool>(owner);

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.Close();

        Assert.False(await result);
    }

    [AvaloniaFact]
    public void The_header_says_what_would_happen_and_that_nothing_has_yet()
    {
        var (window, _) = Show(Change(mods: 3));

        var text = window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible)
            .Select(t => t.Text ?? "").ToList();

        Assert.Contains(text, t => t.Contains("Upgrade 1.22.5 → 1.22.6"));
        Assert.Contains(text, t => t.Contains("Nothing has changed yet"));
    }
}
