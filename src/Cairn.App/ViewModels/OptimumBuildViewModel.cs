using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Cairn.Core;
using Cairn.Core.Games.Optimum;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cairn.App.ViewModels;

/// <summary>
/// A build of the Optimum client, while it happens.
///
/// Shown after the cost has been confirmed, and it stays on screen for twenty minutes — so
/// the two things it must never do are look finished when it is not, and look stuck when it
/// is working. Hence a phase line that always says what is happening and a log that can be
/// opened to watch it, rather than a bar that sits at one value through the longest step.
/// </summary>
public sealed partial class OptimumBuildViewModel : ViewModelBase
{
    private readonly OptimumProvisioner? _provisioner;
    private readonly OptimumSource _source;
    private readonly GameInstall? _vanilla;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Lines waiting to reach the list, and the timer that moves them.
    ///
    /// Batched because a decompile emits tens of thousands of lines in a few minutes, and
    /// one collection change per line locks the UI thread up doing layout — the log made
    /// the window unusable before this existed, which is the opposite of what it is for.
    /// </summary>
    private readonly List<string> _pending = [];
    private readonly DispatcherTimer _flush;

    /// <summary>
    /// How much of the log is kept on screen.
    ///
    /// The file keeps everything; this is only what somebody can scroll. An unbounded list
    /// of a decompile's output is hundreds of megabytes of text boxes.
    /// </summary>
    public const int MaxLines = 2000;

    public OptimumBuildViewModel(
        OptimumProvisioner provisioner, OptimumSource source, GameInstall? vanilla)
    {
        _provisioner = provisioner;
        _source = source;
        _vanilla = vanilla;

        Title = $"Building Optimum {source.Version}";
        Phase = "starting";
        Detail = "getting ready";

        _flush = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _flush.Tick += (_, _) => Drain();
    }

    /// <summary>
    /// A build to look at rather than to run: the window renders exactly as it does
    /// mid-build, and <see cref="StartAsync"/> does nothing.
    ///
    /// Exists because the window is otherwise unphotographable. It starts its build when it
    /// opens, so a screenshot of it means either a real twenty-minute compile or a picture
    /// of a failure — and the same is true of a designer preview. Avalonia already takes
    /// this shape for design-time data; this is the same idea with the log filled in.
    /// </summary>
    public OptimumBuildViewModel(
        OptimumSource source, string phase, string detail, double fraction,
        IEnumerable<string> log, bool expanded = true)
    {
        _source = source;
        _provisioner = null;
        _vanilla = null;

        Title = $"Building Optimum {source.Version}";
        Phase = phase;
        Detail = detail;
        Fraction = fraction;
        LogExpanded = expanded;

        foreach (var line in log) Log.Add(line);

        _flush = new DispatcherTimer();
    }

    public string Title { get; }

    /// <summary>Where the whole log lives, named on screen so a failure can be sent on.</summary>
    public string LogPath => _provisioner?.LogPath ?? "";

    [ObservableProperty] private string _phase;
    [ObservableProperty] private string _detail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIndeterminate))]
    private double _fraction;

    /// <summary>
    /// True until a step reports a fraction.
    ///
    /// The bootstrap runs for most of the build and cannot report progress — it is somebody
    /// else's script — so the bar swings rather than sitting still and reading as frozen.
    /// </summary>
    public bool IsIndeterminate => Fraction <= 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFinished))]
    private bool _isRunning = true;

    public bool IsFinished => !IsRunning;

    [ObservableProperty] private bool _failed;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _succeeded;

    /// <summary>Whether the log is open. Closed by default: it is reassurance, not the point.</summary>
    [ObservableProperty] private bool _logExpanded;

    public ObservableCollection<string> Log { get; } = [];

    /// <summary>The install this produced, once it has.</summary>
    public GameInstall? Result { get; private set; }

    /// <summary>
    /// Runs the build. Called by the window once it is on screen, so the first lines of
    /// output have somewhere to go.
    /// </summary>
    public async Task StartAsync()
    {
        // Nothing to run: this one was built to be looked at. See the preview constructor.
        if (_provisioner is null) return;

        _flush.Start();

        var progress = new Progress<OptimumStep>(step =>
        {
            Phase = step.Phase;
            Detail = step.Detail;
            if (step.Fraction is { } f) Fraction = f;
        });

        var log = new Progress<string>(line =>
        {
            lock (_pending) _pending.Add(line);
        });

        try
        {
            Result = await _provisioner.BuildAsync(_source, _vanilla, progress, log, _cts.Token);

            Succeeded = true;
            Phase = "done";
            Detail = $"Optimum {_source.Version} is installed.";
            Fraction = 1;
        }
        catch (OperationCanceledException)
        {
            Phase = "cancelled";
            Detail = "The build was stopped. Nothing was changed.";
        }
        catch (Exception e)
        {
            Failed = true;
            Phase = "failed";
            Detail = "The build did not finish.";
            Error = e.Message;

            // Opened rather than merely available: the reason is in the output, and a
            // failure with a collapsed log is a dialog that explains nothing.
            LogExpanded = true;
        }
        finally
        {
            IsRunning = false;
            Drain();
            _flush.Stop();
        }
    }

    /// <summary>Moves buffered lines onto the list, dropping the oldest past the cap.</summary>
    private void Drain()
    {
        string[] batch;

        lock (_pending)
        {
            if (_pending.Count == 0) return;
            batch = [.. _pending];
            _pending.Clear();
        }

        foreach (var line in batch) Log.Add(line);

        while (Log.Count > MaxLines) Log.RemoveAt(0);
    }

    [RelayCommand]
    private void Cancel() => _cts.Cancel();

    /// <summary>
    /// Whether closing the window should be taken as cancelling.
    ///
    /// A build left running with nothing on screen would keep a decompiler going for ten
    /// minutes with no way to stop it, so the window closing stops the work.
    /// </summary>
    public void Closing()
    {
        if (IsRunning) _cts.Cancel();
        _flush.Stop();
    }
}
