using System;
using System.Collections.Generic;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media;
using Cairn.Core;

namespace Cairn.App;

/// <summary>
/// How large the interface is drawn.
///
/// Scaling the whole window rather than only the font size: a bigger label in a
/// same-sized button is not more readable, it is more cramped. A LayoutTransformControl
/// scales its child and re-measures it, so text, padding, icons and row heights all grow
/// together and wrapping still happens at the right place — a render transform would
/// simply magnify and clip.
/// </summary>
public static class UiScale
{
    /// <summary>Below this text is small; above it, one window stops fitting a laptop screen.</summary>
    public const double Min = 1.0;
    public const double Max = 2.0;

    /// <summary>Offered as steps rather than a slider: these are the ones worth picking.</summary>
    public static IReadOnlyList<double> Choices { get; } = [1.0, 1.15, 1.25, 1.5, 1.75, 2.0];

    private static double _current = 1.0;

    public static double Current
    {
        get => _current;
        set
        {
            var clamped = Math.Clamp(value, Min, Max);
            if (Math.Abs(clamped - _current) < 0.001) return;

            _current = clamped;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>Raised when the scale changes, so open windows can follow without a restart.</summary>
    public static event EventHandler? Changed;

    public static string Describe(double scale) => $"{scale * 100:F0}%";

    // ---- applying it to a window ----

    /// <summary>
    /// Scales this window's content, now and whenever the setting changes.
    ///
    /// Its declared size is treated as the size at 100% and scaled with the content —
    /// otherwise turning the scale up would just clip a window that no longer fits.
    /// </summary>
    public static void Attach(Window window)
    {
        if (window.Content is not Control content) return;

        // Detach before re-parenting: a Control cannot belong to two parents, and the
        // window still owns this one until its Content is cleared.
        window.Content = null;

        var host = new LayoutTransformControl { Child = content };
        window.Content = host;

        var design = new
        {
            window.Width, window.Height,
            window.MinWidth, window.MinHeight,
        };

        void Apply(object? sender, EventArgs e)
        {
            host.LayoutTransform = new ScaleTransform(Current, Current);

            var (maxWidth, maxHeight) = ScreenLimit(window);

            // Never larger than the display: the people who want this are on laptops, and
            // a window scaled off the bottom of the screen is worse than small text.
            // Min first, since a Min above the current Width would be rejected on the way up.
            if (!double.IsNaN(design.MinWidth)) window.MinWidth = Math.Min(design.MinWidth * Current, maxWidth);
            if (!double.IsNaN(design.MinHeight)) window.MinHeight = Math.Min(design.MinHeight * Current, maxHeight);
            if (!double.IsNaN(design.Width)) window.Width = Math.Min(design.Width * Current, maxWidth);
            if (!double.IsNaN(design.Height)) window.Height = Math.Min(design.Height * Current, maxHeight);
        }

        Apply(null, EventArgs.Empty);

        Changed += Apply;
        window.Closed += (_, _) => Changed -= Apply;
    }

    /// <summary>
    /// The usable display area in the same units as Window.Width, less a margin for the
    /// window frame. Falls back to no limit when there is no screen to ask — a headless
    /// run, or a platform that reports none.
    /// </summary>
    private static (double Width, double Height) ScreenLimit(Window window)
    {
        try
        {
            var screen = window.Screens?.ScreenFromWindow(window) ?? window.Screens?.Primary;
            if (screen is null) return (double.PositiveInfinity, double.PositiveInfinity);

            var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;

            return (screen.WorkingArea.Width / scaling * 0.98,
                    screen.WorkingArea.Height / scaling * 0.98);
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
        {
            return (double.PositiveInfinity, double.PositiveInfinity);
        }
    }

    // ---- remembering it ----

    private sealed class Stored
    {
        public double UiScale { get; set; } = 1.0;
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Never throws: an unreadable settings file costs the default, not a start-up.</summary>
    public static void Load()
    {
        try
        {
            if (!File.Exists(CairnPaths.SettingsPath)) return;

            var stored = JsonSerializer.Deserialize<Stored>(
                File.ReadAllText(CairnPaths.SettingsPath), Json);

            if (stored is not null) Current = stored.UiScale;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Defaults are fine.
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(CairnPaths.Root);

            // Staged and moved, like the caches: a half-written file reads as corrupt.
            var staging = CairnPaths.SettingsPath + "." + Path.GetRandomFileName();
            File.WriteAllText(staging, JsonSerializer.Serialize(new Stored { UiScale = Current }, Json));
            File.Move(staging, CairnPaths.SettingsPath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing the preference costs one re-selection.
        }
    }
}
