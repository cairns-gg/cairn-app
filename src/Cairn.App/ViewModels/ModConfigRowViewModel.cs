using CommunityToolkit.Mvvm.ComponentModel;
using Cairn.Core.Launch;

namespace Cairn.App.ViewModels;

/// <summary>
/// One mod setting, as a row the author can tick to carry in the pack.
///
/// A tick and not an editor. The value shown is the one in the file, which the author set
/// the way they were always going to — in game, or in the mod's own settings screen, where
/// they can see what it does. A launcher offering a second place to type a number would be
/// asking somebody to tune a mod through a text box with no idea of its range or its units,
/// and would then have to answer what happens when the two disagree. So this asks one
/// question: does this value travel with the pack?
/// </summary>
public partial class ModConfigRowViewModel : ViewModelBase
{
    private readonly ModConfigSetting _setting;
    private readonly Action _changed;

    public ModConfigRowViewModel(ModConfigSetting setting, Action changed)
    {
        _setting = setting;
        _changed = changed;
        Carried = setting.IsCarried;
    }

    public ModConfigSetting Setting => _setting;

    /// <summary>The file it lives in, which is how an author recognises the mod.</summary>
    public string File => _setting.File;

    /// <summary>The key, dotted where it sits inside a section.</summary>
    public string Key => _setting.Key;

    public string CurrentText => _setting.CurrentText;

    /// <summary>
    /// What the mod first wrote, kept beside the current value for the same reason the
    /// Hotkeys tab keeps a mod's default beside the pack's binding: "is that what it ships,
    /// or what I picked?" is the question the tab exists to answer.
    /// </summary>
    public string BaselineText => _setting.BaselineText;

    /// <summary>Changed from what the mod first wrote — which is to say, worth a look.</summary>
    public bool IsChanged => _setting.IsChanged;

    /// <summary>
    /// Shown only where there is something to compare against. A file first seen before
    /// baselines existed has none, and "was —" on every row would read as though the mod
    /// had shipped nothing.
    /// </summary>
    public bool ShowBaseline => _setting.HasBaseline && _setting.IsChanged;

    /// <summary>
    /// The pack names this key and the file no longer has it, which is what a mod renaming a
    /// setting looks like from here. Flagged rather than dropped: unticking it is a decision,
    /// and quietly removing it from a shared document is not.
    /// </summary>
    public bool IsOrphan => _setting.Current is null;

    public string OrphanLine => $"no mod in this pack has {Key} any more";

    /// <summary>Travels with the pack. The whole of what this row decides.</summary>
    [ObservableProperty] public partial bool Carried { get; set; }

    partial void OnCarriedChanged(bool value) => _changed();
}
