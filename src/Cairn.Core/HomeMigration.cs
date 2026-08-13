using Cairn.Core.Packs;

namespace Cairn.Core;

/// <summary>What a walk of the tree found at one path.</summary>
public enum EntryKind
{
    Directory,
    File,

    /// <summary>
    /// A symbolic link, recreated as one rather than followed. Following would flatten a
    /// macOS .app bundle into copies of its own contents and break the signature — the same
    /// failure plain <c>zip</c> causes, recorded in the README — and would silently duplicate
    /// gigabytes for anybody who had already worked around a full disk by symlinking
    /// <c>games</c> somewhere else, which is exactly who moves their root.
    /// </summary>
    Link,
}

/// <param name="Bytes">Total size of the files to copy. Links and directories count nothing.</param>
/// <param name="Problem">Why this cannot go ahead, or null. Checked before anything is written.</param>
public sealed record MovePlan(
    string From, string To, int Files, int Links, long Bytes, string? Problem)
{
    public bool CanMove => Problem is null;
}

public sealed record MoveProgress(int Files, int FilesTotal, long Bytes, long BytesTotal, string Current);

/// <param name="Rewritten">Packs whose recorded install directory was moved with them.</param>
/// <param name="OldRoot">Still on disk and still full. Deleting it is the caller's to offer.</param>
/// <param name="KeepInOldRoot">
/// A file inside the old root that must survive it being cleared out, or null.
///
/// The pointer lives at the default location, so moving away from the default leaves it
/// sitting inside the directory being abandoned. Somebody told to delete the old copy will
/// reach for the obvious thing, and taking the pointer with it sends Cairn back to a default
/// root that is now empty — the move undone by the tidying up, with the data still on the
/// other disk and nothing looking at it.
/// </param>
public sealed record MoveResult(
    int Files, int Links, long Bytes, int Rewritten, string OldRoot, string? KeepInOldRoot);

public sealed class MoveFailed(string message) : Exception(message);

/// <summary>
/// Moving everything Cairn keeps from one place to another.
///
/// Copy, verify, repoint, and leave the old copy alone. Never a rename: the whole point is
/// to cross a volume boundary, where <c>Directory.Move</c> fails — <c>OptimumProvisioner</c>
/// already hit that and says so. And never a delete: tens of gigabytes are not worth
/// trusting to one unverified pass, so what to do with the old copy is a separate decision
/// made after this one is known to have worked.
///
/// The order is the safety property. The pointer moves last, so a failure at any earlier
/// step leaves the original root live and untouched; there is no window in which Cairn is
/// pointed at a tree that is still being written.
/// </summary>
public static class HomeMigration
{
    /// <summary>Whether the move can go ahead, and what it would cost.</summary>
    public static MovePlan Plan(string to) =>
        Plan(CairnPaths.Root, to, Environment.GetEnvironmentVariable("CAIRN_HOME"), FreeSpace);

    /// <param name="environment">CAIRN_HOME's value, or null. Set means the pointer would be ignored.</param>
    /// <param name="freeSpace">Bytes available where a path leads, or null when it cannot be told.</param>
    public static MovePlan Plan(
        string from, string to, string? environment, Func<string, long?> freeSpace)
    {
        from = Path.GetFullPath(from);

        MovePlan No(string problem) => new(from, to, 0, 0, 0, problem);

        // Writing a pointer that is then ignored is the worst outcome available: it looks
        // like it worked, nothing changes, and the reason is invisible.
        if (!string.IsNullOrWhiteSpace(environment))
            return No("CAIRN_HOME is set and wins over the pointer file, so moving the data "
                      + "would not change where Cairn looks. Unset it first.");

        if (!Path.IsPathFullyQualified(to))
            return No($"{to} is a relative path; it has to be absolute.");

        to = Path.GetFullPath(to);

        if (!Directory.Exists(from)) return No($"there is nothing at {from} to move");

        if (PathsEqual(from, to)) return No($"{to} is already where Cairn keeps its state");

        // Either nesting is a mess rather than a copy: into a subdirectory of itself never
        // terminates, and the reverse leaves the old tree sitting inside the new root.
        if (Contains(from, to)) return No($"{to} is inside {from}");
        if (Contains(to, from)) return No($"{to} contains {from}");

        if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any())
            return No($"{to} is not empty. Moving into it would mix Cairn's state with "
                      + "whatever is already there — give it a directory of its own.");

        // The last directory is made, the ones above it are not. A mistyped volume is the
        // reason: /Volumes/Bigdsik/cairn would otherwise be created on the boot disk, which
        // is the silent-wrong-place failure this whole feature exists to avoid — and it
        // would look like it had worked right up until the disk filled.
        var parent = Path.GetDirectoryName(to);
        if (!Directory.Exists(to) && (parent is null || !Directory.Exists(parent)))
            return No($"{parent ?? to} is not there, so {to} cannot be created. "
                      + "Only the last directory is created — check the path above it.");

        // A server holding a socket is a server with files open, on a root about to be
        // copied out from under it.
        var running = RunningServers(from);
        if (running.Count > 0)
            return No($"a server is running ({string.Join(", ", running)}). Stop it first — "
                      + "copying a root while something is writing to it copies half of it.");

        var (files, links, bytes) = Measure(from);

        // The parent when the target does not exist yet: the volume is what matters and the
        // directory is about to be made on it.
        var available = freeSpace(Directory.Exists(to) ? to : parent!);
        if (available is { } free && free < bytes)
            return No($"{Describe(bytes)} to copy and only {Describe(free)} free at {to}");

        return new MovePlan(from, to, files, links, bytes, null);
    }

    /// <summary>
    /// Does it. Copies, checks what arrived, moves the recorded install paths with it, and
    /// only then repoints Cairn at the result.
    /// </summary>
    /// <param name="repoint">
    /// What makes the move take effect, defaulting to writing the pointer file. Injected
    /// because the default writes to the running user's real home directory: a test that
    /// called it would repoint the developer's own Cairn at a temporary directory and then
    /// delete it, leaving their launcher refusing to start.
    /// </param>
    public static MoveResult Move(
        MovePlan plan,
        IProgress<MoveProgress>? progress = null,
        CancellationToken ct = default,
        Action<string>? repoint = null)
    {
        if (!plan.CanMove) throw new MoveFailed(plan.Problem!);

        Directory.CreateDirectory(plan.To);

        var files = 0;
        var links = 0;
        var bytes = 0L;

        foreach (var entry in Walk(plan.From, ct))
        {
            var target = Path.Combine(plan.To, entry.Relative);

            switch (entry.Kind)
            {
                case EntryKind.Directory:
                    Directory.CreateDirectory(target);
                    break;

                case EntryKind.Link:
                    // Recreated pointing where the original pointed, relative target and
                    // all. Resolving it to somewhere absolute would quietly rewrite what
                    // the link means.
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (entry.IsDirectoryLink) Directory.CreateSymbolicLink(target, entry.LinkTarget!);
                    else File.CreateSymbolicLink(target, entry.LinkTarget!);
                    links++;
                    break;

                case EntryKind.File:
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                    // File.Copy carries the Unix mode across, which was checked rather than
                    // assumed — without it every game binary would arrive without its
                    // executable bit and nothing would launch.
                    File.Copy(entry.Full, target, overwrite: true);
                    files++;
                    bytes += entry.Length;
                    progress?.Report(new MoveProgress(
                        files, plan.Files, bytes, plan.Bytes, entry.Relative));
                    break;
            }
        }

        Verify(plan, ct);

        var rewritten = RewriteInstallDirectories(plan.From, plan.To);

        // Last, and only now. Everything above can fail without the old root ceasing to be
        // the live one.
        (repoint ?? CairnHome.SetPointer)(plan.To);

        // Only when moving away from the default, which is the ordinary case and the one
        // where clearing out the old directory would take the pointer with it.
        var keep = Contains(plan.From, CairnHome.PointerPath) ? CairnHome.PointerPath : null;

        return new MoveResult(files, links, bytes, rewritten, plan.From, keep);
    }

    /// <summary>
    /// That what arrived is what left. Length and presence per file rather than hashes: the
    /// mods have SHA-256 in the lockfile already and sync checks them, so hashing tens of
    /// gigabytes here would double the wait to re-answer a question something else asks
    /// anyway. What this catches is the failure that actually happens — a file that did not
    /// arrive, or arrived truncated, because a disk filled or something had it open.
    /// </summary>
    private static void Verify(MovePlan plan, CancellationToken ct)
    {
        foreach (var entry in Walk(plan.From, ct))
        {
            var target = Path.Combine(plan.To, entry.Relative);

            switch (entry.Kind)
            {
                case EntryKind.Directory when !Directory.Exists(target):
                    throw new MoveFailed($"{target} did not arrive; nothing has been repointed");

                case EntryKind.Link when new FileInfo(target).LinkTarget is null:
                    throw new MoveFailed($"{target} did not arrive as a link; nothing has been repointed");

                case EntryKind.File:
                    var copied = new FileInfo(target);
                    if (!copied.Exists)
                        throw new MoveFailed($"{target} did not arrive; nothing has been repointed");
                    if (copied.Length != entry.Length)
                        throw new MoveFailed(
                            $"{target} is {copied.Length} bytes and should be {entry.Length}; "
                            + "nothing has been repointed");
                    break;
            }
        }
    }

    /// <summary>
    /// A pack can name an install directory to launch with — an absolute path, under the old
    /// root, which after a move points at a copy Cairn no longer reads. Rewritten in the new
    /// tree by path rather than through PackStore, because PackStore resolves against
    /// CairnPaths.Root and the pointer has deliberately not moved yet, so it would rewrite
    /// the copy being left behind.
    /// </summary>
    private static int RewriteInstallDirectories(string from, string to)
    {
        var packs = Path.Combine(to, "packs");
        if (!Directory.Exists(packs)) return 0;

        var rewritten = 0;

        foreach (var dir in Directory.EnumerateDirectories(packs))
        {
            var path = Path.Combine(dir, "local.json");
            if (!File.Exists(path)) continue;

            var state = PackLocalState.Load(path);
            if (state.InstallDirectory is not { } install) continue;
            if (!Contains(from, install)) continue;

            state.InstallDirectory = Path.Combine(to, Path.GetRelativePath(from, install));
            state.Save(path);
            rewritten++;
        }

        return rewritten;
    }

    private static (int Files, int Links, long Bytes) Measure(string root)
    {
        var files = 0;
        var links = 0;
        var bytes = 0L;

        foreach (var entry in Walk(root, CancellationToken.None))
        {
            if (entry.Kind is EntryKind.Link) links++;
            else if (entry.Kind is EntryKind.File) { files++; bytes += entry.Length; }
        }

        return (files, links, bytes);
    }

    private readonly record struct Entry(
        string Full, string Relative, EntryKind Kind, long Length, string? LinkTarget, bool IsDirectoryLink);

    /// <summary>
    /// Every entry under <paramref name="root"/>, depth first, never following a link.
    ///
    /// Hand-rolled rather than EnumerateFiles with AllDirectories, which walks straight
    /// through a symlinked directory as though it were a real one — copying what is on the
    /// other side, and looping forever if it points back inside.
    /// </summary>
    private static IEnumerable<Entry> Walk(string root, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            foreach (var path in Directory.EnumerateFileSystemEntries(stack.Pop()))
            {
                var info = new FileInfo(path);
                var relative = Path.GetRelativePath(root, path);

                if (info.LinkTarget is { } target)
                {
                    yield return new Entry(path, relative, EntryKind.Link, 0, target,
                        info.Attributes.HasFlag(FileAttributes.Directory));
                    continue;
                }

                if (info.Attributes.HasFlag(FileAttributes.Directory))
                {
                    yield return new Entry(path, relative, EntryKind.Directory, 0, null, false);
                    stack.Push(path);
                    continue;
                }

                yield return new Entry(path, relative, EntryKind.File, info.Length, null, false);
            }
        }
    }

    /// <summary>Packs with a live server console socket under this root.</summary>
    private static List<string> RunningServers(string root)
    {
        var dir = Path.Combine(root, "run");
        if (!Directory.Exists(dir)) return [];

        return Directory.EnumerateFiles(dir, "*.sock")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToList();
    }

    /// <summary>
    /// Bytes free on the volume a path leads to, or null when that cannot be told — in which
    /// case the move goes ahead and finds out, since not knowing is not a reason to refuse.
    /// </summary>
    private static long? FreeSpace(string path)
    {
        try
        {
            // On Unix DriveInfo takes the path and resolves the volume containing it, and
            // GetPathRoot would answer "/" for everything — the boot disk, which is exactly
            // the volume being moved away from. Windows wants the root and rejects the rest.
            var full = Path.GetFullPath(path);
            var name = OperatingSystem.IsWindows() ? Path.GetPathRoot(full) ?? full : full;

            return new DriveInfo(name).AvailableFreeSpace;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Trim(a), Trim(b), PathComparison);

    /// <summary>Whether <paramref name="inner"/> is the same as or underneath <paramref name="outer"/>.</summary>
    private static bool Contains(string outer, string inner)
    {
        var o = Trim(Path.GetFullPath(outer));
        var i = Trim(Path.GetFullPath(inner));

        // The separator matters: without it /data/cairn-old reads as inside /data/cairn.
        return string.Equals(o, i, PathComparison)
               || i.StartsWith(o + Path.DirectorySeparatorChar, PathComparison);
    }

    private static string Trim(string path) => path.TrimEnd(Path.DirectorySeparatorChar);

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static string Describe(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F0} KB",
        _ => $"{bytes} bytes",
    };
}
