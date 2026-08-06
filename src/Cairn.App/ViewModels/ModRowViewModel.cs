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
    private readonly Func<ModRowViewModel, Task>? _choosePin;
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
        _choosePin = loadReleases;
        _pin = pin;
        _remove = remove;
        _openPage = openPage;
        _armed = armed;
        _update = update;

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
    /// The version, shown only while pinned.
    ///
    /// Nothing when it is not: an unpinned row is the ordinary state of most mods, and
    /// thirty rows each saying "Pin…" is thirty invitations to do something rare. The
    /// faded pin is enough to say the control is there, and the tooltip says what it does.
    /// </summary>
    public string PinLabel => Mod.Version ?? "";

    /// <summary>Faded when nothing is pinned, so the state reads without being laboured.</summary>
    public double PinOpacity => IsPinned ? 1.0 : 0.4;

    /// <summary>
    /// Re-reads everything derived from the pin.
    ///
    /// Needed because the pin lives on the manifest entry rather than on this row, and
    /// changing it there is invisible here. The old combo box hid this by holding its own
    /// selection — the row agreed with the manifest because the user had just set both.
    /// </summary>
    public void PinChanged()
    {
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(PinDisplay));
        OnPropertyChanged(nameof(PinLabel));
        OnPropertyChanged(nameof(PinOpacity));
        OnPropertyChanged(nameof(PinTip));
    }

    public string PinTip => IsPinned
        ? $"Pinned to {Mod.Version}. Click to stop pinning and follow this mod again."
        : "Pin this mod to a version, so it stays put when you update the others.";

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

    /// <summary>True while the release list for this row is being fetched.</summary>
    [ObservableProperty] public partial bool LoadingReleases { get; set; }

    /// <summary>
    /// Pins this mod, or removes the pin if it has one.
    ///
    /// One control with two meanings, because pinned is one bit and a control per state is
    /// a control too many. Unpinned, it asks which version — through the caller, which owns
    /// the window and the network; pinned, it simply unpins, since "stop holding this
    /// still" needs no dialogue to describe it.
    ///
    /// Replaced a combo box on every row. Thirty mods meant thirty controls that looked
    /// editable for something done rarely, and choosing from it pinned immediately with no
    /// confirm — a version number in 120 pixels being all it could ever say about the
    /// choice.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task TogglePin()
    {
        if (IsPinned)
        {
            _pin?.Invoke(this, null);
            return;
        }

        if (_choosePin is not null) await _choosePin(this);
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
