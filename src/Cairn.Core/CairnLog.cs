using System.Text;

namespace Cairn.Core;

/// <summary>
/// Cairn's own log on disk: everything the launcher says to somebody, and every failure it
/// catches, kept past the session that saw it.
///
/// The Log tab was the only record there was, and it lives in memory. A sync that refused a
/// mod, a launch that threw, a crash — each was gone by the time anybody thought to ask what
/// happened, which is usually after a restart and always after the banner that mentioned it
/// had been replaced by something vaguer (cairns-gg/cairn-app#8). A bug report then had
/// nothing in it but a screenshot of the vaguer thing.
///
/// One file for the whole launcher rather than one per pack. A game install, an import and a
/// crash belong to no pack, and a pack's own file would be deleted with the pack — which is
/// the moment its history is most likely to be wanted. Lines that do belong to a pack say
/// which, and <see cref="Tail"/> can pick them back out.
///
/// Nothing here throws. A log that can fail a launch is worse than no log.
/// </summary>
public static class CairnLog
{
    /// <summary>
    /// When the file is set aside for a fresh one. Large enough to hold several sessions of a
    /// big pack's syncs, small enough that reading it whole for a diagnostics report costs
    /// nothing worth noticing. One older file is kept beside it, so the bound is twice this.
    /// </summary>
    public const long MaxBytes = 1024 * 1024;

    /// <summary>
    /// Writes are serialised within the process. Two threads appending at once would
    /// interleave a stack trace with somebody else's line; two processes — the launcher and
    /// cairn-cli — still can, which is tolerable for a file that exists to be read by a
    /// person.
    /// </summary>
    private static readonly Lock Gate = new();

    /// <summary>One line, tagged with the pack it belongs to when there is one.</summary>
    public static void Write(string line, string? pack = null) => Append(Format(line, pack));

    /// <summary>
    /// A caught failure, in full. What reached the screen was <c>e.Message</c> at most; the
    /// type and the stack are what tell a maintainer which of the many things that can throw
    /// an <see cref="IOException"/> it was.
    /// </summary>
    public static void Error(string what, Exception e, string? pack = null) =>
        Append(Format($"{what}: {e}", pack));

    /// <summary>
    /// The last <paramref name="lines"/> entries, oldest first, as written. Given a pack, only
    /// that pack's entries and the ones that belong to no pack — a crash or a game install is
    /// part of any pack's story, and another pack's sync is not.
    /// </summary>
    public static IReadOnlyList<string> Tail(int lines, string? pack = null)
    {
        try
        {
            var path = CairnPaths.LogPath;
            if (!File.Exists(path)) return [];

            // Read shared, because the launcher may be appending to it while this reads.
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            var entries = Entries(reader.ReadToEnd());
            if (pack is not null) entries = entries.Where(e => BelongsTo(e, pack)).ToList();

            return entries.Count > lines ? entries[^lines..] : entries;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// A timestamp, the pack in brackets or a dash, then the text. Continuation lines — a stack
    /// trace, a multi-line message — are indented, so an entry is a line that does not start
    /// with a space and <see cref="Tail"/> keeps a trace with the line that introduced it.
    /// UTC, because these get pasted into issues read in other time zones.
    /// </summary>
    internal static string Format(string text, string? pack, DateTimeOffset? at = null)
    {
        var stamp = (at ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff'Z'");
        var tag = pack is null ? "-" : $"[{pack}]";

        var body = text.ReplaceLineEndings("\n").TrimEnd('\n').Replace("\n", "\n    ");
        return $"{stamp} {tag} {body}{Environment.NewLine}";
    }

    private static List<string> Entries(string text)
    {
        var entries = new List<string>();

        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.Length == 0) continue;

            if (line[0] == ' ' && entries.Count > 0) entries[^1] += "\n" + line;
            else entries.Add(line);
        }

        return entries;
    }

    private static bool BelongsTo(string entry, string pack)
    {
        // "<date> <time> <tag> …", split no further than the tag.
        var parts = entry.Split(' ', 4);
        if (parts.Length < 3) return false;

        return parts[2] == "-"
               || string.Equals(parts[2], $"[{pack}]", StringComparison.OrdinalIgnoreCase);
    }

    private static void Append(string entry)
    {
        try
        {
            lock (Gate)
            {
                // Never the thing that creates a root a pointer names. A root that is not
                // there is a disk that is not plugged in, and making the directory would put
                // a stray tree on the boot disk at the mount point — the failure CairnHome's
                // preflight exists to refuse. Anything else is created as every other write
                // under the root creates it.
                var home = CairnHome.Resolve();
                if (home.Source is HomeSource.Pointer && !Directory.Exists(home.Root)) return;

                // Read through CairnPaths on every write, never held: the root moves while
                // the launcher is running. Opened and closed per entry for the same reason —
                // a handle kept open would go on writing into the tree a move just deleted,
                // and on Windows would stop that delete outright.
                var path = CairnPaths.LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                Rotate(path);

                using var stream = new FileStream(
                    path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                var bytes = Encoding.UTF8.GetBytes(entry);
                stream.Write(bytes);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A full disk or a read-only home. Nowhere left to say so.
        }
    }

    private static void Rotate(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < MaxBytes) return;

        File.Move(path, path + ".1", overwrite: true);
    }
}
