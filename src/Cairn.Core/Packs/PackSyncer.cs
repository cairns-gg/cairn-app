using System.Security.Cryptography;
using Cairn.Core.ModDb;

namespace Cairn.Core.Packs;

public enum SyncAction { Unchanged, Downloaded, Updated, Removed, Failed, Warned }

public sealed record SyncStep(SyncAction Action, string ModId, string Detail);

public sealed record SyncReport(List<SyncStep> Steps, PackLock Lock)
{
    public bool Failed => Steps.Any(s => s.Action == SyncAction.Failed);
    public IEnumerable<SyncStep> Warnings => Steps.Where(s => s.Action == SyncAction.Warned);
}

/// <summary>
/// Brings a directory of mod zips in line with a pack manifest.
///
/// Mods are left as .zip — Vintage Story loads zipped mods directly (ModLoader.CollectMods
/// takes a FileSystemInfo), so there is never a reason to unpack them.
/// </summary>
public sealed class PackSyncer(ModDbClient moddb, HttpClient http)
{
    /// <param name="modsDir">Directory handed to the game via --addModPath.</param>
    public async Task<SyncReport> SyncAsync(
        PackManifest manifest,
        string modsDir,
        string lockPath,
        IProgress<SyncStep>? progress = null,
        CancellationToken ct = default)
    {
        var problems = manifest.Validate().ToList();
        if (problems.Count > 0)
            throw new InvalidDataException("Pack manifest is invalid:\n  " + string.Join("\n  ", problems));

        Directory.CreateDirectory(modsDir);

        var steps = new List<SyncStep>();
        var previous = PackLock.Load(lockPath);
        var newLock = new PackLock { GameVersion = manifest.GameVersion };

        void Record(SyncStep step)
        {
            steps.Add(step);
            progress?.Report(step);
        }

        foreach (var want in manifest.Mods)
        {
            ct.ThrowIfCancellationRequested();

            ResolvedRelease? release;
            try
            {
                release = await moddb.ResolveAsync(want.ModId, manifest.GameVersion, want.Version, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is ModDbException or HttpRequestException)
            {
                Record(new SyncStep(SyncAction.Failed, want.ModId, e.Message));
                continue;
            }

            if (release is null)
            {
                Record(new SyncStep(SyncAction.Failed, want.ModId,
                    $"no release marked for game {manifest.GameVersion}"));
                continue;
            }

            if (release.Quality == MatchQuality.SameMinor)
                Record(new SyncStep(SyncAction.Warned, want.ModId,
                    $"{release.ModVersion} is not marked for {manifest.GameVersion} exactly, "
                    + "only for another release in that minor series"));

            if (string.Equals(release.Side, "server", StringComparison.OrdinalIgnoreCase))
                Record(new SyncStep(SyncAction.Warned, want.ModId,
                    "ModDB marks this as server-side; installing it client-side may do nothing"));

            var target = Path.Combine(modsDir, release.FileName);
            var prior = previous?.Mods.FirstOrDefault(
                m => string.Equals(m.ModId, release.ModId, StringComparison.OrdinalIgnoreCase));

            var locked = new LockedMod
            {
                ModId = release.ModId,
                Version = release.ModVersion,
                FileName = release.FileName,
                Url = release.Url,
                ReleaseId = release.ReleaseId,
                FileId = release.FileId,
                Side = release.Side,
            };

            var upToDate = File.Exists(target)
                           && prior is not null
                           && prior.Version == release.ModVersion
                           && prior.Sha256.Length > 0
                           && await Sha256Async(target, ct).ConfigureAwait(false) == prior.Sha256;

            if (upToDate)
            {
                locked.Sha256 = prior!.Sha256;
                newLock.Mods.Add(locked);
                Record(new SyncStep(SyncAction.Unchanged, release.ModId, release.ModVersion));
                continue;
            }

            try
            {
                await DownloadAsync(release.Url, target, ct).ConfigureAwait(false);
                locked.Sha256 = await Sha256Async(target, ct).ConfigureAwait(false);

                // When a lock already pins this exact version — most importantly one that
                // arrived with a shared pack — the bytes must match. Otherwise the pack is
                // not reproducing what its author had.
                if (prior is not null
                    && prior.Version == release.ModVersion
                    && prior.Sha256.Length > 0
                    && !string.Equals(prior.Sha256, locked.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(target);
                    Record(new SyncStep(SyncAction.Failed, release.ModId,
                        $"{release.ModVersion} does not match the locked checksum — refusing it"));
                    continue;
                }

                newLock.Mods.Add(locked);

                var action = prior is null ? SyncAction.Downloaded : SyncAction.Updated;
                var detail = prior is null || prior.Version == release.ModVersion
                    ? release.ModVersion
                    : $"{prior.Version} -> {release.ModVersion}";
                Record(new SyncStep(action, release.ModId, detail));
            }
            catch (Exception e) when (e is HttpRequestException or IOException)
            {
                Record(new SyncStep(SyncAction.Failed, release.ModId, e.Message));
            }
        }

        // Anything in the directory we did not just account for is no longer part of
        // the pack. Only touch .zip files so a hand-dropped folder mod is left alone.
        var keep = newLock.Mods.Select(m => m.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stray in Directory.EnumerateFiles(modsDir, "*.zip"))
        {
            if (keep.Contains(Path.GetFileName(stray))) continue;
            File.Delete(stray);
            Record(new SyncStep(SyncAction.Removed, Path.GetFileNameWithoutExtension(stray), "no longer in pack"));
        }

        newLock.Save(lockPath);
        return new SyncReport(steps, newLock);
    }

    private async Task DownloadAsync(string url, string target, CancellationToken ct)
    {
        // Download beside the target then move, so an interrupted sync never leaves a
        // truncated zip that the game would try to load.
        var tmp = target + ".partial";
        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = File.Create(tmp))
            {
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }

            File.Move(tmp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var s = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(s, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
