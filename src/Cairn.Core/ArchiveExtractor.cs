using System.Formats.Tar;
using System.IO.Compression;

namespace Cairn.Core;

/// <summary>
/// Unpacks the archive formats Cairn downloads, with no external tar or unzip process.
/// </summary>
public static class ArchiveExtractor
{
    public static bool IsSupported(string fileName) =>
        fileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    public static async Task ExtractAsync(string archive, string destination, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destination);

        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archive, destination, overwriteFiles: true);
            return;
        }

        await using var fs = File.OpenRead(archive);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gz, destination, overwriteFiles: true, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Restores the executable bit on a file. TarFile carries unix modes through, but a
    /// zip does not, and an unset bit fails confusingly at launch rather than at install.
    /// </summary>
    public static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path)) return;

        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path,
            mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }
}
