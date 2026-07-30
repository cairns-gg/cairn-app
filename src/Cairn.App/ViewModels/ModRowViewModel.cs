using CommunityToolkit.Mvvm.ComponentModel;
using Cairn.Core.Packs;

namespace Cairn.App.ViewModels;

/// <summary>
/// One mod inside a pack: what the manifest asks for, plus what the lockfile says is
/// actually on disk.
/// </summary>
public partial class ModRowViewModel : ViewModelBase
{
    public ModRowViewModel(PackMod mod, LockedMod? locked)
    {
        Mod = mod;
        Locked = locked;
    }

    public PackMod Mod { get; }

    public LockedMod? Locked { get; }

    public string ModId => Mod.ModId;

    /// <summary>"1.3.0" when pinned, otherwise "newest".</summary>
    public string PinDisplay => Mod.Version ?? "newest";

    public bool IsPinned => Mod.Version is not null;

    public string InstalledDisplay => Locked?.Version ?? "not installed";

    public bool IsInstalled => Locked is not null;

    public string SideDisplay => Locked?.Side ?? "";

    /// <summary>
    /// Flags a mod ModDB marks server-side, which in a client pack usually does nothing.
    /// </summary>
    public bool IsServerSide =>
        string.Equals(Locked?.Side, "server", StringComparison.OrdinalIgnoreCase);
}
