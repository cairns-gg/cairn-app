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

    /// <summary>
    /// Unpacks an archive, deciding what it is from its first bytes rather than its name.
    ///
    /// The name is not available to be trusted here. Every caller downloads to
    /// <c>&lt;name&gt;.partial</c> and extracts that, so nothing reaching this method ends in
    /// ".zip" — and this used to test for exactly that, fall through, and hand a zip to
    /// GZipStream. On macOS and Linux every artifact is a tarball, so the fall-through was
    /// silently the right branch and the zip branch was dead code. On Windows, where the
    /// .NET runtime and SDK archives are the only zips Cairn downloads, it meant **no
    /// private runtime could ever be installed** — the failure being "the archive entry was
    /// compressed using an unsupported compression method", which is GZipStream's complaint
    /// about a PK header and reads like a corrupt download.
    ///
    /// Magic bytes rather than a repaired suffix check, because the suffix was never the
    /// thing being asked about: what has to be true is what the bytes are.
    /// </summary>
    public static async Task ExtractAsync(string archive, string destination, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destination);

        var kind = Identify(archive);

        if (kind == Format.Zip)
        {
            ZipFile.ExtractToDirectory(archive, destination, overwriteFiles: true);
            return;
        }

        if (kind == Format.Unknown)
            throw new InvalidDataException(Lang.Get("archive-unknown-format", Path.GetFileName(archive)));

        await using var fs = File.OpenRead(archive);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gz, destination, overwriteFiles: true, ct)
            .ConfigureAwait(false);
    }

    private enum Format { Unknown, Zip, GzipTar }

    /// <summary>
    /// What the first bytes say this is. Both signatures are fixed by their formats: a zip
    /// local file header is "PK\x03\x04", and a gzip member starts 0x1F 0x8B.
    /// </summary>
    private static Format Identify(string archive)
    {
        Span<byte> head = stackalloc byte[4];

        using (var fs = File.OpenRead(archive))
        {
            if (fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length)
                return Format.Unknown;
        }

        if (head[0] == 0x50 && head[1] == 0x4B && head[2] == 0x03 && head[3] == 0x04)
            return Format.Zip;

        return head[0] == 0x1F && head[1] == 0x8B ? Format.GzipTar : Format.Unknown;
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
