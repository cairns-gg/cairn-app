namespace Cairn.Core.Launch;

/// <summary>
/// The logs Vintage Story writes under its data path.
///
/// Cairn's own log records what Cairn did — sync steps, provisioning, the launch itself —
/// which says nothing about why the game closed on startup or why a mod did not load. That
/// answer is in the game's log, and packs have their own data path, so each pack's logs are
/// its own.
/// </summary>
public sealed class GameLogs(string dataPath)
{
    /// <summary>The one that carries mod loading and startup failures.</summary>
    public const string ClientMain = "client-main.log";

    /// <summary>Singleplayer runs an internal server, and its side of a failure lands here.</summary>
    public const string ServerMain = "server-main.log";

    public string Directory => Path.Combine(dataPath, "Logs");

    public bool Exists => System.IO.Directory.Exists(Directory);

    public string PathTo(string file) => Path.Combine(Directory, file);

    /// <summary>Log files present, newest first — that is the order they are worth reading in.</summary>
    public IReadOnlyList<string> Files()
    {
        try
        {
            if (!Exists) return [];

            return new DirectoryInfo(Directory).GetFiles("*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.Name)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The last <paramref name="lines"/> lines of a log.
    ///
    /// Read with FileShare.ReadWrite because the game holds its logs open while running —
    /// the interesting case is precisely a game that is still up, or that has just fallen
    /// over with the file still locked.
    /// </summary>
    public IReadOnlyList<string> Tail(string file = ClientMain, int lines = 200)
    {
        var kept = new Queue<string>(lines);

        try
        {
            var path = PathTo(file);
            if (!File.Exists(path)) return [];

            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            while (reader.ReadLine() is { } line)
            {
                kept.Enqueue(line);
                if (kept.Count > lines) kept.Dequeue();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return [.. kept];
    }

    /// <summary>
    /// Only the lines the game itself flagged as a problem, across the client and server
    /// logs, oldest first.
    ///
    /// Consecutive repeats are collapsed: a failing render call logs the same line dozens
    /// of times a second, and forty copies of it would push the actual cause out of view.
    /// </summary>
    public IReadOnlyList<string> Problems(int max = 40)
    {
        var found = new List<string>();

        foreach (var file in new[] { ClientMain, ServerMain })
        {
            var lines = Tail(file, lines: 2000).Where(IsProblem).ToList();
            if (lines.Count == 0) continue;

            var label = file == ClientMain ? "client" : "server";
            string? previous = null;
            var repeats = 0;

            foreach (var line in lines)
            {
                var message = Message(line);
                if (message == previous)
                {
                    repeats++;
                    continue;
                }

                if (repeats > 0) found.Add($"  ({repeats} more like the previous line)");
                repeats = 0;
                previous = message;
                found.Add($"[{label}] {line}");
            }

            if (repeats > 0) found.Add($"  ({repeats} more like the previous line)");
        }

        // The tail is what matters: the last failure before it stopped.
        return found.Count <= max ? found : found[^max..];
    }

    /// <summary>
    /// Whether the game marked this line as a problem. Its lines look like
    /// "29.7.2026 20:03:04 [Error] …", so the level is matched with its brackets rather
    /// than by searching for the bare word — mod names and messages contain "error" too.
    /// </summary>
    private static bool IsProblem(string line) =>
        line.Contains("[Error]", StringComparison.Ordinal)
        || line.Contains("[Fatal]", StringComparison.Ordinal)
        || line.Contains("[Warning]", StringComparison.Ordinal);

    /// <summary>The line without its timestamp, so repeats compare equal.</summary>
    private static string Message(string line)
    {
        var bracket = line.IndexOf('[');
        return bracket < 0 ? line : line[bracket..];
    }
}
