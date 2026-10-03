namespace Cairn.App;

/// <summary>
/// Which packs have something changing their Mods directory or lockfile, keyed by pack id —
/// a sync, an update, a revision being taken, a publish, Play getting ready.
///
/// Owned by MainViewModel for the reason <see cref="RunningGames"/> is: PackDetailViewModel
/// is rebuilt every time the selection changes, and the work it starts is not. The busy flag
/// used to live on the pane, so clicking away from a pack mid-download and back again built
/// a fresh pane that believed nothing was happening — Play was enabled, and pressing it
/// started a second sync over the first, both snapshotting and rewriting the same lock. And
/// nothing on the pane itself held two of its own commands apart: Update on two rows ran two
/// whole-pack syncs at once.
///
/// One holder per pack. Whatever wants to change a pack asks <see cref="TryBegin"/> and does
/// nothing if it is refused, and every pane for the pack — old or new — reads
/// <see cref="IsBusy"/> from here. Used only on the UI thread, which is where every command
/// that asks runs and where its awaits resume, so it needs no lock of its own.
///
/// Also the answer to "is any pack being changed?", which a move of Cairn's whole home has to
/// ask before it starts — see PreferencesViewModel.
/// </summary>
public sealed class PackWork
{
    private readonly HashSet<string> _busy = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised with the pack whose state moved.</summary>
    public event Action<string>? Changed;

    public bool IsBusy(string packId) => _busy.Contains(packId);

    public bool AnyBusy => _busy.Count > 0;

    /// <summary>
    /// The pack, for as long as the returned hold is not disposed — or null when something
    /// else already has it, in which case the caller does nothing.
    /// </summary>
    public IDisposable? TryBegin(string packId)
    {
        if (!_busy.Add(packId)) return null;

        Changed?.Invoke(packId);
        return new Hold(this, packId);
    }

    private void End(string packId)
    {
        if (_busy.Remove(packId)) Changed?.Invoke(packId);
    }

    private sealed class Hold(PackWork owner, string packId) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            owner.End(packId);
        }
    }
}
