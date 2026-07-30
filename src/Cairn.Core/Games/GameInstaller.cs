using System.Security.Cryptography;

namespace Cairn.Core.Games;

public enum InstallPhase { Downloading, Verifying, Extracting, Finishing, Done }

public sealed record InstallProgress(InstallPhase Phase, long Done, long Total, string Detail)
{
    public double? Fraction => Total > 0 ? (double)Done / Total : null;
}

public sealed class GameInstallException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Downloads a game release and unpacks it into the store.
///
/// Nothing here is quarantined: com.apple.quarantine is applied by browsers and
/// LaunchServices, not by HTTP clients, so a Cairn-installed game avoids the "damaged
/// and can't be opened" failure that hits a manually downloaded tarball on macOS.
/// </summary>
public sealed class GameInstaller(HttpClient http, GameStore store)
{
    public async Task<GameInstall> InstallAsync(
        GameRelease release,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!release.CanInstall)
            throw new GameInstallException(
                $"{release.Version} for {release.Platform} ships as '{release.Artifact.FileName}', "
                + "which Cairn does not know how to install. Install it manually, then point "
                + "Cairn at the resulting install.");

        // The Windows client is an installer rather than a tarball; it is run, not unpacked.
        if (release.IsWindowsInstaller)
        {
            if (!OperatingSystem.IsWindows())
                throw new GameInstallException(
                    $"{release.Artifact.FileName} is a Windows installer and can only be run on Windows.");

            return await InstallFromInstallerAsync(release, progress, ct).ConfigureAwait(false);
        }

        var url = release.Artifact.DownloadUrl
                  ?? throw new GameInstallException($"No download URL for {release.Version}.");

        var target = store.InstallDir(release.Version);
        if (Directory.Exists(target) && GameInstall.TryAt(target) is { } existing) return existing;

        Directory.CreateDirectory(store.Root);
        var archive = Path.Combine(store.Root, release.Artifact.FileName + ".partial");
        var staging = target + ".staging";

        try
        {
            await DownloadAsync(url, archive, release.Artifact.FileSize, progress, ct).ConfigureAwait(false);
            await VerifyAsync(archive, release.Artifact.Md5, progress, ct).ConfigureAwait(false);

            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);

            progress?.Report(new InstallProgress(InstallPhase.Extracting, 0, 0, release.Artifact.FileName));

            // Unpacking 600 MB is not instant either, and reports nothing while it runs.
            using (var watch = new CancellationTokenSource())
            {
                var watching = DirectoryGrowth.ReportAsync(
                    staging, $"unpacking Vintage Story {release.Version}", progress, watch.Token);

                try
                {
                    await ArchiveExtractor.ExtractAsync(archive, staging, ct).ConfigureAwait(false);
                }
                finally
                {
                    await watch.CancelAsync().ConfigureAwait(false);
                    await watching.ConfigureAwait(false);
                }
            }

            progress?.Report(new InstallProgress(InstallPhase.Finishing, 0, 0, "arranging files"));
            Flatten(staging);
            MakeExecutable(staging);

            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(staging, target);
        }
        catch
        {
            if (Directory.Exists(staging)) TryDelete(staging);
            throw;
        }
        finally
        {
            if (File.Exists(archive)) TryDeleteFile(archive);
        }

        var install = GameInstall.TryAt(target)
                      ?? throw new GameInstallException(
                          $"Unpacked {release.Version} but {target} does not look like a game install.");

        progress?.Report(new InstallProgress(InstallPhase.Done, 1, 1, install.Version));
        return install;
    }

    /// <summary>
    /// Downloads the Windows installer and runs it into the store, so a managed version
    /// sits beside the player's own install rather than replacing it.
    ///
    /// Unlike the tarball path there is no staging directory: Inno Setup records the
    /// chosen directory, and moving the tree afterwards would leave it describing a place
    /// that no longer exists. A failed install is cleaned up instead.
    /// </summary>
    private async Task<GameInstall> InstallFromInstallerAsync(
        GameRelease release, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var url = release.Artifact.DownloadUrl
                  ?? throw new GameInstallException($"No download URL for {release.Version}.");

        var target = store.InstallDir(release.Version);
        if (Directory.Exists(target) && GameInstall.TryAt(target) is { } existing) return existing;

        Directory.CreateDirectory(store.Root);
        var installer = Path.Combine(store.Root, release.Artifact.FileName + ".partial");
        var log = Path.Combine(store.Root, $"install-{release.Version}.log");

        try
        {
            await DownloadAsync(url, installer, release.Artifact.FileSize, progress, ct).ConfigureAwait(false);
            await VerifyAsync(installer, release.Artifact.Md5, progress, ct).ConfigureAwait(false);

            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.CreateDirectory(target);

            progress?.Report(new InstallProgress(
                InstallPhase.Extracting, 0, 0, $"running {release.Artifact.FileName}"));

            // /VERYSILENT means the installer reports nothing at all for several minutes,
            // so the tree it is writing is the only available sign of life.
            using var watch = new CancellationTokenSource();
            var watching = DirectoryGrowth.ReportAsync(
                target, $"installing Vintage Story {release.Version}", progress, watch.Token);

            // The installer rewrites two things that belong to the player rather than to
            // this copy: their Add/Remove Programs entry and their desktop shortcut.
            var uninstallEntry = WindowsGameInstaller.UninstallEntry.Capture();
            var desktopShortcuts = WindowsGameInstaller.DesktopShortcuts.Capture();
            try
            {
                await WindowsGameInstaller.RunAsync(installer, target, log, ct).ConfigureAwait(false);
            }
            finally
            {
                // Stopped before restoring, so a last poll cannot land on top of whatever
                // the caller reports next.
                await watch.CancelAsync().ConfigureAwait(false);
                await watching.ConfigureAwait(false);

                desktopShortcuts.Restore();
                uninstallEntry.Restore();
            }
        }
        catch
        {
            if (Directory.Exists(target)) TryDelete(target);
            throw;
        }
        finally
        {
            if (File.Exists(installer)) TryDeleteFile(installer);
        }

        var install = GameInstall.TryAt(target)
                      ?? throw new GameInstallException(
                          $"Installed {release.Version} but {target} does not look like a game "
                          + $"install. The installer's log is at {log}.");

        // Only kept for a failure worth reporting.
        TryDeleteFile(log);

        progress?.Report(new InstallProgress(InstallPhase.Done, 1, 1, install.Version));
        return install;
    }

    private async Task DownloadAsync(
        string url, string destination, string displaySize,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        using var response = await http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // filesize in the manifest is a display string ("613.5 MB"), so real progress has
        // to come from Content-Length.
        var total = response.Content.Headers.ContentLength ?? 0;

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var sink = File.Create(destination);

        var buffer = new byte[1 << 20];
        long done = 0;
        var lastReport = 0L;

        while (true)
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;

            await sink.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;

            // Report every few MB rather than every chunk, so a UI bound to this is not
            // flooded with thousands of updates.
            if (done - lastReport >= 4 << 20)
            {
                lastReport = done;
                progress?.Report(new InstallProgress(InstallPhase.Downloading, done, total, displaySize));
            }
        }

        progress?.Report(new InstallProgress(InstallPhase.Downloading, done, total, displaySize));
    }

    private static async Task VerifyAsync(
        string path, string expectedMd5, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expectedMd5)) return;

        progress?.Report(new InstallProgress(InstallPhase.Verifying, 0, 0, "checking md5"));

        await using var fs = File.OpenRead(path);
        var hash = await MD5.HashDataAsync(fs, ct).ConfigureAwait(false);
        var actual = Convert.ToHexStringLower(hash);

        if (!string.Equals(actual, expectedMd5.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new GameInstallException(
                $"Download is corrupt: md5 {actual} does not match the published {expectedMd5}.");
    }

    /// <summary>
    /// Some archives wrap everything in a single top-level directory and some do not.
    /// If the extracted tree is one directory with no game executable beside it, lift its
    /// contents up a level so the install layout is predictable either way.
    /// </summary>
    private static void Flatten(string root)
    {
        if (GameInstall.TryAt(root) is not null) return;

        var entries = Directory.GetFileSystemEntries(root);
        if (entries.Length != 1 || !Directory.Exists(entries[0])) return;

        var inner = entries[0];

        foreach (var path in Directory.GetFileSystemEntries(inner))
        {
            var moved = Path.Combine(root, Path.GetFileName(path));
            if (Directory.Exists(path)) Directory.Move(path, moved);
            else File.Move(path, moved);
        }

        Directory.Delete(inner, recursive: true);
    }

    private static void MakeExecutable(string root)
    {
        foreach (var name in new[] { "Vintagestory", "VintagestoryServer" })
            ArchiveExtractor.EnsureExecutable(Path.Combine(root, name));
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    private static void TryDeleteFile(string file)
    {
        try { File.Delete(file); } catch (IOException) { }
    }
}
