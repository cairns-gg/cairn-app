using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Cairn.App;
using Cairn.App.Views;
using Xunit;

namespace Cairn.App.Tests;

/// <summary>
/// Making the interface bigger. Scaling the window rather than the font size: a larger
/// label inside a same-sized button is more cramped, not more readable.
/// </summary>
[Collection(AvaloniaTests.Collection)]
public class UiScaleTests : IDisposable
{
    private readonly double _original = UiScale.Current;
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "cairn-scale-" + Guid.NewGuid().ToString("n")[..8]);

    public UiScaleTests() => Environment.SetEnvironmentVariable("CAIRN_HOME", _home);

    public void Dispose()
    {
        UiScale.Current = _original;
        Environment.SetEnvironmentVariable("CAIRN_HOME", null);
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    private static Window Scaled()
    {
        var window = new ConfirmWindow
        {
            DataContext = new ViewModels.ConfirmViewModel("Title", "Message", "Do it"),
        };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static ScaleTransform? TransformOf(Visual window) =>
        window.GetVisualDescendants().OfType<LayoutTransformControl>()
            .FirstOrDefault()?.LayoutTransform as ScaleTransform;

    [AvaloniaFact]
    public void A_window_is_wrapped_so_its_whole_content_scales()
    {
        UiScale.Current = 1.5;
        var window = Scaled();

        var transform = TransformOf(window);

        Assert.NotNull(transform);
        Assert.Equal(1.5, transform!.ScaleX);
        Assert.Equal(1.5, transform.ScaleY);
    }

    [AvaloniaFact]
    public void Changing_it_reaches_windows_that_are_already_open()
    {
        // The only way to know whether a size is comfortable is to look at it, so this
        // must not need a restart.
        UiScale.Current = 1.0;
        var window = Scaled();

        UiScale.Current = 1.25;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(1.25, TransformOf(window)!.ScaleX);
    }

    [AvaloniaFact]
    public void The_window_grows_with_its_content()
    {
        // Otherwise turning the scale up just clips a window that no longer fits.
        UiScale.Current = 1.0;
        var window = Scaled();
        var before = window.Width;

        UiScale.Current = 2.0;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(before * 2, window.Width);
    }

    [AvaloniaFact]
    public void A_window_never_grows_past_the_display()
    {
        // The people who want this are on laptops. A window scaled off the bottom of the
        // screen is worse than small text.
        UiScale.Current = 1.0;
        var window = Scaled();

        var screen = window.Screens?.Primary;
        if (screen is null) return;   // headless with no screen; nothing to clamp against

        var limit = screen.WorkingArea.Height / (screen.Scaling <= 0 ? 1 : screen.Scaling);

        UiScale.Current = 2.0;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(window.Height <= limit,
            $"window is {window.Height} tall, the usable screen is {limit}");
    }

    [AvaloniaFact]
    public void A_closed_window_stops_listening()
    {
        // It would otherwise be resized forever by a static event.
        UiScale.Current = 1.0;
        var window = Scaled();
        window.Close();

        UiScale.Current = 1.5;

        Assert.Equal(1.0, TransformOf(window)?.ScaleX ?? 1.0);
    }

    [AvaloniaTheory]
    [InlineData(0.2, 1.0)]
    [InlineData(5.0, 2.0)]
    public void Absurd_values_are_clamped(double asked, double expected)
    {
        UiScale.Current = asked;
        Assert.Equal(expected, UiScale.Current);
    }

    [AvaloniaFact]
    public void The_choice_survives_a_restart()
    {
        UiScale.Current = 1.25;
        UiScale.Save();

        UiScale.Current = 1.0;
        UiScale.Load();

        Assert.Equal(1.25, UiScale.Current);
    }

    [AvaloniaFact]
    public void A_missing_or_corrupt_settings_file_is_not_fatal()
    {
        UiScale.Load();   // nothing written yet

        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "settings.json"), "{ not json");

        UiScale.Load();   // must not throw

        Assert.InRange(UiScale.Current, UiScale.Min, UiScale.Max);
    }

    [AvaloniaFact]
    public void Every_offered_choice_is_one_it_will_accept()
    {
        foreach (var choice in UiScale.Choices)
        {
            UiScale.Current = choice;
            Assert.Equal(choice, UiScale.Current);
        }
    }
}
