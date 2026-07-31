using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Cairn.App.ViewModels;
using Cairn.App.Views;
using Xunit;

namespace Cairn.App.Tests;

/// <summary>
/// The destructive-action dialog. Inline, the pack delete prompt sat at the bottom of a
/// scrolling tab, so arming it drew the warning below the fold — the one place a
/// destructive prompt must never be.
/// </summary>
public class ConfirmWindowTests
{
    private static ConfirmViewModel Prompt(string? message = null) => new(
        "Delete “Anego Server”?",
        message ?? "This deletes the pack, its downloaded mods, and 3 worlds (412 MB). This cannot be undone.",
        "Delete pack");

    private static Button Find(Visual root, string name) =>
        root.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);

    [AvaloniaFact]
    public void It_names_the_target_and_the_cost_and_the_action()
    {
        var window = new ConfirmWindow { DataContext = Prompt() };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var text = window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();

        Assert.Contains(text, t => t.Contains("Anego Server"));
        Assert.Contains(text, t => t.Contains("3 worlds"));

        // The button says what it does, so it is the warning as much as the text is.
        Assert.Equal("Delete pack", Find(window, "ConfirmButton").Content);
    }

    [AvaloniaTheory]
    [InlineData("ConfirmButton", true)]
    [InlineData("CancelButton", false)]
    public async Task Only_the_named_action_closes_with_a_yes(string button, bool expected)
    {
        var owner = new Window();
        owner.Show();

        var window = new ConfirmWindow { DataContext = Prompt() };
        var result = window.ShowDialog<bool>(owner);

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Find(window, button).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(expected, await result);
    }

    [AvaloniaFact]
    public async Task Dismissing_it_any_other_way_means_no()
    {
        var owner = new Window();
        owner.Show();

        var window = new ConfirmWindow { DataContext = Prompt() };
        var result = window.ShowDialog<bool>(owner);

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.Close();

        Assert.False(await result);
    }

    [AvaloniaFact]
    public void A_long_message_scrolls_and_leaves_the_buttons_where_they_are()
    {
        // A pack with many worlds makes this text long. It must not push the buttons off,
        // which is the failure this dialog exists to fix.
        var window = new ConfirmWindow
        {
            DataContext = Prompt(string.Join(", ", Enumerable.Range(1, 300).Select(i => $"World {i}"))),
        };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        foreach (var name in new[] { "ConfirmButton", "CancelButton" })
        {
            var button = Find(window, name);
            var position = button.TranslatePoint(default, window)!.Value;

            Assert.True(button.IsEffectivelyVisible, $"{name} is not visible");
            Assert.True(position.Y + button.Bounds.Height <= window.Height,
                $"{name} is below the window bottom: {position.Y + button.Bounds.Height} > {window.Height}");
        }
    }
}
