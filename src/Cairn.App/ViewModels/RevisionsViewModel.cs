using System.Globalization;
using Cairn.Core;
using Cairn.Core.Cairns;
using Cairn.Core.Packs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cairn.App.ViewModels;

/// <summary>One revision the site kept, described well enough to pick it out.</summary>
public sealed class RevisionChoiceViewModel(PackRevision revision, int current, int publishedFromHere)
{
    public int Revision { get; } = revision.Revision;

    public string Label => Lang.Get("revisions-row", Revision);

    public string When { get; } = revision.PublishedAt is { } at
        ? at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
        : "";

    /// <summary>
    /// The two revisions somebody needs told apart from the rest: the one followers have now,
    /// and the one this copy last sent — which differ when it was published from elsewhere.
    /// </summary>
    public string Note { get; } =
        revision.Revision == current ? Lang.Get("revisions-current")
        : revision.Revision == publishedFromHere ? Lang.Get("revisions-from-here")
        : "";

    public bool HasNote => Note.Length > 0;
}

/// <summary>One line of what a restore would do to one mod.</summary>
public sealed class RevisionChangeViewModel(ModChange change)
{
    public string ModId { get; } = change.ModId;
    public string What { get; } = RevisionPreview.Describe(change);
}

/// <summary>
/// Choosing an earlier revision of your own pack to put back.
///
/// Choosing one fetches it and says what it would do — per mod, because somebody here is
/// usually hunting for the one mod that broke their world. Nothing is restored until the
/// window closes on the button, and nothing is published at all: the restored pack reads as
/// having changes to publish, and is played first.
/// </summary>
public sealed partial class RevisionsViewModel : ViewModelBase
{
    private readonly Func<int, Task<(PackBundle Bundle, RevisionPreview Preview)>> _load;
    private readonly Dictionary<int, (PackBundle Bundle, RevisionPreview Preview)> _loaded = [];
    private readonly bool _archived;
    private readonly IReadOnlyList<string> _worlds;
    private readonly Action? _openWorlds;
    private int _generation;

    /// <param name="publishedFromHere">The revision this copy's link records.</param>
    /// <param name="load">Fetches one revision and works out what restoring it would do.</param>
    /// <param name="worlds">The pack's worlds by name, for the warning.</param>
    /// <param name="openWorlds">Opens the folder they are in, so backing up is one click away.</param>
    public RevisionsViewModel(
        string packName, PackHistory history, int publishedFromHere,
        Func<int, Task<(PackBundle, RevisionPreview)>> load,
        IReadOnlyList<string>? worlds = null, Action? openWorlds = null)
    {
        PackName = packName;
        _archived = history.Archived;
        _load = load;
        _worlds = worlds ?? [];
        _openWorlds = openWorlds;

        Choices = [.. history.Revisions.Select(r => new RevisionChoiceViewModel(r, history.Current, publishedFromHere))];

        // The one before what followers have now, since that is nearly always the one being
        // looked for — and starting there fetches it, so the window opens with an answer.
        Selected = Choices.FirstOrDefault(c => c.Revision < history.Current);
    }

    public string PackName { get; }

    public string Title => Lang.Get("revisions-title", PackName);

    public IReadOnlyList<RevisionChoiceViewModel> Choices { get; }

    public bool HasChoices => Choices.Count > 0;

    /// <summary>Why the list is empty, which is never the author's doing.</summary>
    public string EmptyNote => _archived
        ? Lang.Get("revisions-none")
        : Lang.Get("restore-no-history-kept");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RestoreLabel), nameof(CanRestore))]
    public partial RevisionChoiceViewModel? Selected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(Changes), nameof(CanRestore), nameof(WorldWarning))]
    public partial RevisionPreview? Preview { get; set; }

    /// <summary>
    /// Shown whatever is selected, beside the button, in the error colours. The restore is
    /// undone by restoring again; what it does to a world on the next launch is not. See
    /// <see cref="RevisionPreview.WorldWarning"/>.
    /// </summary>
    public string WorldWarning => RevisionPreview.WorldWarning(_worlds, Preview);

    public bool CanOpenWorlds => _openWorlds is not null;

    [RelayCommand]
    private void OpenWorlds() => _openWorlds?.Invoke();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(CanRestore))]
    public partial string? Error { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRestore), nameof(Summary))]
    public partial bool IsLoading { get; set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    public string Summary => IsLoading ? Lang.Get("revisions-fetching") : Preview?.Summary ?? "";

    public IReadOnlyList<RevisionChangeViewModel> Changes =>
        Preview is null ? [] : [.. Preview.Changes.Select(c => new RevisionChangeViewModel(c))];

    public string RestoreLabel => Selected is { } s
        ? Lang.Get("revisions-restore", s.Revision)
        : Lang.Get("revisions-restore-none");

    /// <summary>
    /// Only once the selected revision has been fetched and differs from the pack as it is.
    /// Restoring a revision identical to the pack would change nothing and say it had.
    /// Whether the preview is the selection's is <see cref="LoadAsync"/>'s to guarantee.
    /// </summary>
    public bool CanRestore =>
        !IsLoading && !HasError && Selected is not null && Preview is { Anything: true };

    /// <summary>What the button restores, once it is pressed.</summary>
    public PackBundle? Chosen =>
        CanRestore && _loaded.TryGetValue(Selected!.Revision, out var got) ? got.Bundle : null;

    /// <summary>The fetch for the current selection, so a test can wait for it.</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    partial void OnSelectedChanged(RevisionChoiceViewModel? value) =>
        Loading = value is null ? Task.CompletedTask : LoadAsync(value.Revision);

    private async Task LoadAsync(int revision)
    {
        // Clicking down the list fires a fetch per row, and they land in any order. Only the
        // one still selected may say anything.
        var generation = ++_generation;

        Error = null;

        if (_loaded.TryGetValue(revision, out var known))
        {
            Preview = known.Preview;
            IsLoading = false;
            return;
        }

        Preview = null;
        IsLoading = true;

        try
        {
            var got = await _load(revision);
            _loaded[revision] = got;

            if (generation != _generation) return;
            Preview = got.Preview;
        }
        catch (Exception e)
        {
            if (generation != _generation) return;
            Error = e.Message;
        }
        finally
        {
            if (generation == _generation) IsLoading = false;
        }
    }
}
