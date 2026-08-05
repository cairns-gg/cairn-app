using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cairn.Core.Packs;

namespace Cairn.App.ViewModels;

/// <summary>
/// One mod inside a pack: what the manifest asks for, plus what the lockfile says is
/// actually on disk.
///
/// The row owns its own actions and its own version list. Both used to live on the pane
/// and act on whichever row happened to be selected, which meant several controls quietly
/// referring to "the selected mod" from a distance.
/// </summary>
public partial class ModRowViewModel : ViewModelBase
{
    private readonly Func<ModRowViewModel, Task>? _loadReleases;
    private readonly Action<ModRowViewModel, string?>? _pin;
    private readonly Action<ModRowViewModel>? _remove;
    private readonly Action<ModRowViewModel>? _openPage;
    private readonly Action<ModRowViewModel>? _armed;
    private readonly Action<ModRowViewModel>? _update;

    /// <summary>Tells "the list was refilled" apart from "the user chose something".</summary>
    private bool _settingProgrammatically;

    public ModRowViewModel(
        PackMod mod,
        LockedMod? locked,
        Func<ModRowViewModel, Task>? loadReleases = null,
        Action<ModRowViewModel, string?>? pin = null,
        Action<ModRowViewModel>? remove = null,
        Action<ModRowViewModel>? openPage = null,
        Action<ModRowViewModel>? armed = null,
        Action<ModRowViewModel>? update = null,
        bool editable = true)
    {
        Editable = editable;
        Mod = mod;
        Locked = locked;
        _loadReleases = loadReleases;
        _pin = pin;
        _remove = remove;
        _openPage = openPage;
        _armed = armed;
        _update = update;

        // Shown before the list is fetched, so the row reads correctly from the start.
        _settingProgrammatically = true;
        SelectedRelease = mod.Version ?? PackDetailViewModel.TrackNewest;
        ReleaseChoices.Add(SelectedRelease);
        _settingProgrammatically = false;
    }

    public PackMod Mod { get; }

    public LockedMod? Locked { get; }

    public string ModId => Mod.ModId;

    // ---- dependencies ----

    /// <summary>
    /// True when nothing in the manifest named this mod — it is here because another mod
    /// requires it. Such a row is shown but not acted on: removing it while its dependent
    /// is still in the pack is incoherent, and the next sync would reinstate it anyway.
    /// </summary>
    public bool IsDependency => Locked?.RequiredBy is { Count: > 0 };

    /// <summary>The mod this row is indented under. Null for a mod the pack named.</summary>
    public string? RequiredByFirst => Locked?.RequiredBy?.FirstOrDefault();

    public string RequiredByNote =>
        IsDependency ? $"required by {string.Join(", ", Locked!.RequiredBy!)}" : "";

    /// <summary>
    /// Whether this row's controls are offered at all.
    ///
    /// False on a locked copy of somebody else's pack: the pin, the remove and the update
    /// are the three ways a row stops matching the author's. Pushed in rather than fixed at
    /// construction, because rows are built before the pack's share state is known and a
    /// value read once left every row of a followed pack believing it was editable.
    /// </summary>
    public bool Editable
    {
        get => _editable;
        set
        {
            if (_editable == value) return;

            _editable = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanChange));
        }
    }

    private bool _editable = true;

    public bool IsDirect => !IsDependency;

    /// <summary>The controls that alter the pack, as opposed to the ones that only look.</summary>
    public bool CanChange => IsDirect && Editable;

    /// <summary>
    /// Set the moment a mod is added, cleared when sync reports on it. A pack edit is
    /// instant but the download behind it is not, and a row that simply sat there with no
    /// installed version read as though nothing had happened.
    /// </summary>
    [ObservableProperty] public partial bool Downloading { get; set; }

    /// <summary>
    /// The mod's own name, once ModDB has been asked. Null until then — a manifest holds
    /// ids, so the id is all a row can honestly show at first.
    /// </summary>
    [ObservableProperty] public partial string? Name { get; set; }

    partial void OnNameChanged(string? value) => OnPropertyChanged(nameof(Title));

    /// <summary>What to call this mod: its name if known, otherwise its id.</summary>
    public string Title => string.IsNullOrWhiteSpace(Name) ? ModId : Name!;

    /// <summary>"1.3.0" when pinned, otherwise "latest".</summary>
    public string PinDisplay => Mod.Version ?? PackDetailViewModel.TrackNewest;

    public bool IsPinned => Mod.Version is not null;

    /// <summary>
    /// The version actually installed, per the lockfile. Meaningful again now that a
    /// launch cannot silently change it: this is what you are running.
    /// </summary>
    public string InstalledVersion => Locked?.Version ?? "";

    public bool HasInstalledVersion => Locked is not null;

    /// <summary>
    /// What this mod would move to if updated, once a check has run. Null when it is
    /// current, pinned, or nothing has been checked.
    /// </summary>
    [ObservableProperty] public partial string? UpdateAvailable { get; set; }

    partial void OnUpdateAvailableChanged(string? value)
    {
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(UpdateNote));
    }

    public bool HasUpdate => !string.IsNullOrWhiteSpace(UpdateAvailable);

    public string UpdateNote => $"→ {UpdateAvailable}";

    public string SideDisplay => Locked?.Side ?? "";

    /// <summary>
    /// Arrives after the row is drawn. A pack knows only mod ids, so finding an icon
    /// means asking ModDB what the mod looks like — cached, but never instant the first
    /// time, and never worth delaying the list for.
    /// </summary>
    [ObservableProperty] public partial Bitmap? Icon { get; set; }

    partial void OnIconChanged(Bitmap? value) => OnPropertyChanged(nameof(HasIcon));

    public bool HasIcon => Icon is not null;

    /// <summary>
    /// Flags a mod ModDB marks server-side, which in a client pack usually does nothing.
    /// </summary>
    public bool IsServerSide =>
        string.Equals(Locked?.Side, "server", StringComparison.OrdinalIgnoreCase);

    // ---- versions ----

    /// <summary>
    /// Holds only the current pin until the dropdown is opened. Fetching every row's
    /// releases when the pack is merely shown would be one ModDB call per mod, for
    /// something usually not being asked about.
    /// </summary>
    public ObservableCollection<string> ReleaseChoices { get; } = [];

    [ObservableProperty] public partial string? SelectedRelease { get; set; }
    [ObservableProperty] public partial bool LoadingReleases { get; set; }

    public bool ReleasesLoaded { get; private set; }

    /// <summary>Fetches this row's versions unless they are already here.</summary>
    public Task EnsureReleasesAsync() =>
        ReleasesLoaded || _loadReleases is null ? Task.CompletedTask : _loadReleases(this);

    /// <summary>Fills the dropdown without that counting as the user picking something.</summary>
    public void ShowChoices(IReadOnlyList<string> choices)
    {
        _settingProgrammatically = true;

        ReleaseChoices.Clear();
        foreach (var c in choices) ReleaseChoices.Add(c);

        var pinned = Mod.Version;
        SelectedRelease = pinned is not null && ReleaseChoices.Contains(pinned)
            ? pinned
            : PackDetailViewModel.TrackNewest;

        ReleasesLoaded = true;
        _settingProgrammatically = false;
    }

    partial void OnSelectedReleaseChanged(string? value)
    {
        if (_settingProgrammatically || value is null) return;

        // Choosing a version is the pin; there is no separate confirm step.
        _pin?.Invoke(this, value == PackDetailViewModel.TrackNewest ? null : value);
    }

    // ---- actions ----

    /// <summary>
    /// Removing is destructive and the button is a single character next to a dropdown,
    /// so it asks first — in the row, where it is obvious which mod is meant. The pack's
    /// own Delete works the same way.
    /// </summary>
    [ObservableProperty] public partial bool ConfirmingRemove { get; set; }

    public string RemovePrompt => $"Remove {ModId} from this pack?";

    [RelayCommand]
    private void RequestRemove()
    {
        ConfirmingRemove = true;
        _armed?.Invoke(this);
    }

    [RelayCommand]
    private void CancelRemove() => ConfirmingRemove = false;

    [RelayCommand]
    private void ConfirmRemove()
    {
        ConfirmingRemove = false;
        _remove?.Invoke(this);
    }

    [RelayCommand]
    private void OpenPage() => _openPage?.Invoke(this);

    [RelayCommand]
    private void Update() => _update?.Invoke(this);
}
