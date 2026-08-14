using Cairn.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cairn.Core.Hotkeys;

namespace Cairn.App.ViewModels;

/// <summary>
/// One hotkey in the pack, as a row you can rebind.
///
/// The pack's value and the mod's default are kept apart all the way through. A row showing
/// only "P" cannot answer the question the author actually has — is that what the mod ships,
/// or what I chose? — and that question is the whole reason for the tab.
/// </summary>
public partial class HotkeyRowViewModel : ViewModelBase
{
    private readonly HotkeyEntry _entry;
    private readonly Action _changed;
    private readonly Action<HotkeyRowViewModel>? _arming;

    /// <param name="arming">
    /// Told before this row starts waiting for a key, so the tab can stop whichever row was
    /// waiting before. A keyboard has one focus and so does a capture.
    /// </param>
    public HotkeyRowViewModel(
        HotkeyEntry entry, KeyBinding? packBinding, Action changed,
        Action<HotkeyRowViewModel>? arming = null)
    {
        _entry = entry;
        _changed = changed;
        _arming = arming;
        Binding = packBinding;
    }

    public string Code => _entry.Code;

    /// <summary>The mod's label where it gave one, and the code where it gave a lang key.</summary>
    public string Display => _entry.Display;

    /// <summary>Which mod brought it, so a clash names two files rather than two codes.</summary>
    public string Source => _entry.Source;

    public bool IsGame => _entry.IsGame;

    /// <summary>
    /// Movement and the mouse buttons: the controls a player's hands know without looking.
    ///
    /// Marked and held back rather than forbidden. A pack that quietly moves the jump key
    /// of everyone who installs it has overstepped, but an author who means to move one has
    /// a reason — a mod wanting G is a real problem, and G is Sit down. So the row says what
    /// it is and the button asks to be unlocked first, which makes the change a decision
    /// instead of a slip.
    /// </summary>
    public bool IsPlayerControl => _entry.IsPlayerControl;

    /// <summary>"movement control", "mouse button", or empty for an ordinary hotkey.</summary>
    public string ControlLabel => _entry.ControlLabel;

    /// <summary>Unlocked for this session by somebody who meant it. Never saved.</summary>
    [ObservableProperty] public partial bool Unlocked { get; set; }

    public bool CanEdit => !IsPlayerControl || Unlocked;

    public bool ShowUnlock => IsPlayerControl && !Unlocked;

    /// <summary>Shown once it is unlocked, so the row still reads as something to be careful with.</summary>
    public bool ShowControlLabel => IsPlayerControl;

    partial void OnUnlockedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(ShowUnlock));
        CaptureCommand.NotifyCanExecuteChanged();
        UnbindCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Says out loud that this one is the player's, and then allows it. Deliberately per
    /// row and not remembered: the next pack, and this pack tomorrow, start locked again.
    /// </summary>
    [RelayCommand]
    private void Unlock() => Unlocked = true;

    /// <summary>What the mod ships, or empty where the scan could not read it.</summary>
    public string DefaultText => _entry.Default?.ToString() ?? "—";

    public KeyBinding? Default => _entry.Default;

    /// <summary>What the pack says, if it says anything. Null means "leave the mod's own".</summary>
    [ObservableProperty] public partial KeyBinding? Binding { get; set; }

    /// <summary>The binding that will actually be in force: the pack's, else the mod's.</summary>
    public KeyBinding? Effective => Binding ?? _entry.Default;

    public string EffectiveText => Effective?.ToString() ?? "—";

    public bool IsOverridden => Binding is not null;

    /// <summary>Set by the tab when this row shares its key with another one.</summary>
    [ObservableProperty] public partial bool Clashes { get; set; }

    /// <summary>Which other hotkeys are on the same key, for the row to name them.</summary>
    [ObservableProperty] public partial string ClashesWith { get; set; } = "";

    /// <summary>
    /// Shares a held key — Shift, Ctrl, Alt — with another hotkey, which is how the game is
    /// designed rather than a problem. Named on the row so a shared key is not a mystery,
    /// and kept out of the conflict count so the real ones stay findable.
    /// </summary>
    [ObservableProperty] public partial bool SharesHeldKey { get; set; }

    partial void OnSharesHeldKeyChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowClash));
        OnPropertyChanged(nameof(ShowSharedKey));
    }

    public bool ShowSharedKey => SharesHeldKey && !string.IsNullOrEmpty(ClashesWith);

    public string SharedKeyLine => Lang.Get("hotkeys-held-alongside", ClashesWith);

    /// <summary>True while this row is waiting for a keypress to bind.</summary>
    [ObservableProperty] public partial bool Capturing { get; set; }

    public string ButtonLabel => Capturing ? Lang.Get("hotkeys-press-a-key") : EffectiveText;

    partial void OnBindingChanged(KeyBinding? value)
    {
        OnPropertyChanged(nameof(Effective));
        OnPropertyChanged(nameof(EffectiveText));
        OnPropertyChanged(nameof(IsOverridden));
        OnPropertyChanged(nameof(IsUnbound));
        OnPropertyChanged(nameof(ButtonLabel));
        ClearCommand.NotifyCanExecuteChanged();
        _changed();
    }

    partial void OnCapturingChanged(bool value) => OnPropertyChanged(nameof(ButtonLabel));

    partial void OnClashesChanged(bool value) => OnPropertyChanged(nameof(ShowClash));

    partial void OnClashesWithChanged(string value)
    {
        OnPropertyChanged(nameof(ShowClash));
        OnPropertyChanged(nameof(ShowSharedKey));
        OnPropertyChanged(nameof(SharedKeyLine));
    }

    public bool ShowClash => Clashes && !string.IsNullOrEmpty(ClashesWith);

    /// <summary>
    /// Puts the row into capture mode. The keypress itself arrives from the window, which
    /// is the only thing that sees one — a view model has no keyboard.
    ///
    /// The tab is told first so it can stop any other row waiting. Two armed rows is not a
    /// harmless duplicate: the key goes to whichever the tab finds first, which is list
    /// order and not the one somebody just asked for, and the other sits on "Press a key…"
    /// until they change tab.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Capture()
    {
        _arming?.Invoke(this);
        Capturing = true;
    }

    /// <summary>
    /// Drops the pack's binding so the mod's own default applies again. Distinct from
    /// binding it to the default value: the pack then says nothing about this hotkey, and a
    /// mod that changes its default later is followed rather than pinned to today's.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsOverridden))]
    private void Clear() => Binding = null;

    /// <summary>
    /// Puts the hotkey on no key at all.
    ///
    /// The third answer, and the one a pack of twenty mods needs most: five of them want P,
    /// and for four the honest resolution is not another key but none. Distinct from Reset,
    /// which hands the hotkey back to whatever its mod ships — this is a decision that it
    /// should not fire, and it travels with the pack like any other binding.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Unbind() => Binding = KeyBinding.Unbound;

    public bool IsUnbound => Binding?.IsUnbound == true;
}
