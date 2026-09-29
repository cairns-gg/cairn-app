using Cairn.Core.ModDb;

namespace Cairn.Core.Packs;

/// <summary>A mod publishing moved from ModDB onto its download link, because ModDB stopped listing it.</summary>
public sealed record ReaddressedMod(string ModId, string Version, string Url, bool WasDependency);

/// <summary>
/// Mods ModDB has stopped listing, carried on by the address of the file this copy has.
///
/// ModDB answers a mod that was unpublished, or locked by a moderator, the same way it
/// answers one that never existed — <c>200 {"statuscode":"404"}</c> — while its download
/// links go on serving the files. A pack naming such a mod by id cannot be installed by
/// anybody who does not already have it: a follower's lock arrives with no locations
/// (<see cref="PackLock.ClearResolvedLocations"/>), so the only thing it can do is ask
/// ModDB, and ModDB says no. xskillsfork and the xlibfork it needs are the case that found
/// this, and put a dedicated server that had never installed them into a restart loop.
///
/// The author's copy is the one place the problem can be fixed, because it is the one
/// place a download address for the mod is trusted: its lock was written by this machine
/// from ModDB's own answers, and imported locks never keep theirs. So publishing moves
/// each such mod onto <see cref="PackMod.Url"/> — the document a recipient is shown and
/// the only place an address is ever believed from — pointing at the file this copy
/// installed, and the lock keeps its hash, so every copy gets exactly those bytes or
/// refuses. Dependencies too: the manifest never named xlibfork, and it needed moving as
/// much as the mod that wanted it.
///
/// The author's own manifest, not just the published copy. A pack whose file says one
/// thing while what went up says another is a pack that publishes differently from what
/// its author can see; and the address entry is not a dead end. Mods by address already
/// ask, on an update check, whether ModDB lists them again, and follow it from there if
/// it does — see PackSyncer.ChangedAtUrlAsync — which is why the address written is the
/// site's own download link with the file id in it rather than whichever CDN URL the
/// lock happened to hold.
///
/// This does rewrite a settled pack's lock by the act of sharing it, which publishing
/// otherwise refuses to do. It changes where a file comes from and nothing about which
/// file: the version and hash are the ones already installed, and the next sync finds
/// the zip in place and leaves it.
/// </summary>
public static class UnlistedMods
{
    /// <summary>
    /// Moves every mod ModDB no longer lists onto the address of the file this copy has,
    /// saving the manifest and lock when anything moved, and says which.
    ///
    /// Asks ModDB about every lock entry — dependencies included — that it resolved from
    /// there. That is one request per mod on a pack nothing has synced for ten minutes and
    /// almost none on one just synced; see <see cref="ModDbClient.ExistsAsync"/>. A ModDB
    /// that cannot be reached moves nothing: not being able to ask is not an answer.
    /// </summary>
    /// <param name="manifest">
    /// The caller's own, changed in place. The launcher holds one manifest per open pack and
    /// saves it on every edit, so a copy read afresh here and saved behind its back would be
    /// written over by the next change somebody made on the Mods tab.
    /// </param>
    public static async Task<IReadOnlyList<ReaddressedMod>> ReaddressAsync(
        PackStore store, PackManifest manifest, ModDbClient moddb, CancellationToken ct = default)
    {
        var id = manifest.Id;
        var locked = store.LoadLock(id);

        // A lock for another game version describes files chosen for that one, and the sync
        // that is about to replace them is what should decide, not this.
        if (locked is null
            || !string.Equals(locked.GameVersion, manifest.GameVersion, StringComparison.OrdinalIgnoreCase))
            return [];

        var moved = new List<ReaddressedMod>();

        foreach (var entry in locked.Mods)
        {
            ct.ThrowIfCancellationRequested();

            if (Address(entry) is not { } url) continue;

            var named = manifest.Mods.FirstOrDefault(
                m => string.Equals(m.ModId, entry.ModId, StringComparison.OrdinalIgnoreCase));

            if (named is { IsFromUrl: true }) continue;

            bool listed;
            try
            {
                listed = await moddb.ExistsAsync(entry.ModId, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is ModDbException or HttpRequestException)
            {
                continue;
            }

            if (listed) continue;

            if (named is null)
            {
                // Where it goes in the list matters to nobody, and appending keeps the
                // manifest's diff to the one line that is new.
                manifest.Mods.Add(new PackMod { ModId = entry.ModId, Url = url });
            }
            else
            {
                // A pin is meaningless beside an address — the manifest refuses the pair —
                // and so is an acceptance, which is about ModDB's version tags. What the
                // pin held is still held: by the hash, to exactly the bytes installed.
                named.Url = url;
                named.Version = null;
                named.AcceptedFor = null;
            }

            entry.Url = url;
            entry.FromUrl = true;
            entry.ReleaseId = 0;

            moved.Add(new ReaddressedMod(entry.ModId, entry.Version, url, WasDependency: named is null));
        }

        if (moved.Count == 0) return moved;

        store.Save(manifest);
        locked.Save(store.LockPath(id));
        return moved;
    }

    /// <summary>
    /// The address to carry an entry by, or null when it is not one this can move.
    ///
    /// Only an entry this machine resolved from ModDB and hashed: one already by address
    /// has nothing to move, and one without a hash could not hold a recipient to the file.
    /// The site's download link when the lock knows the file id — the form ModDbUrls
    /// recognises, which is what lets an update check notice the mod being listed again —
    /// and otherwise the URL ModDB gave, provided it is still on one of ModDB's hosts.
    /// </summary>
    private static string? Address(LockedMod entry)
    {
        if (entry.FromUrl || entry.Sha256.Length == 0 || entry.FileName.Length == 0) return null;

        if (!ModDbUrls.IsKnownDownloadHost(entry.Url)) return null;

        return entry.FileId > 0
            ? $"{ModDbUrls.Site}/download/{entry.FileId}/{Uri.EscapeDataString(entry.FileName)}"
            : entry.Url;
    }
}
