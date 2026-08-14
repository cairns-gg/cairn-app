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
/// <param name="OldRoot">Where it came from, now removed unless <paramref name="RemovalProblem"/> says otherwise.</param>
/// <param name="Freed">Bytes the old copy gave back.</param>
/// <param name="RemovalProblem">
/// Why the old copy is still there, or null. Not a failure of the move: by the time this can
/// happen everything has arrived, been checked and been repointed, so the right answer is to
/// say the space was not reclaimed rather than to pretend the move did not happen.
/// </param>
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
    int Files, int Links, long Bytes, int Rewritten, string OldRoot,
    string? KeepInOldRoot, long Freed, string? RemovalProblem);

public sealed class MoveFailed(string message) : Exception(message);

/// <summary>
/// Moving everything Cairn keeps from one place to another.
///
/// Copy, verify, repoint, remove. Never a rename: the whole point is to cross a volume
/// boundary, where <c>Directory.Move</c> fails — <c>OptimumProvisioner</c> already hit that
/// and says so.
///
/// The order is the safety property. The pointer moves second to last, so a failure at any
/// earlier step leaves the original root live and untouched and there is no window in which
/// Cairn is pointed at a tree still being written. The original goes only after that, when
/// it is no longer the live one and every file has been checked.
///
/// This did stop after the repoint, leaving the old copy for somebody to deal with. It was
/// the wrong shape: whoever asks for this is out of disk space, and answering with two
/// copies and a note about where the second one is has not moved anything. One decision,
/// taken once, does the whole of it.
///
/// What "checked" means is worth being exact about, since the delete rests on it: every file
/// is confirmed present at its full length. Not hashed — the mods carry SHA-256 in the
/// lockfile and sync verifies them anyway, and hashing tens of gigabytes would double the
/// wait to re-answer a question something else already asks. That catches the failures that
/// happen: a disk filling, a file held open, a permission refused. It would not catch a
/// silent corruption that preserved the length, which no ordinary copy tool catches either.
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
            return No(Lang.Get("move-env-wins"));

        if (!Path.IsPathFullyQualified(to))
            return No($"{to} is a relative path; it has to be absolute.");

        to = Path.GetFullPath(to);

        if (!Directory.Exists(from)) return No(Lang.Get("move-nothing-at", from));

        if (PathsEqual(from, to)) return No($"{to} is already where Cairn keeps its state");

        // Either nesting is a mess rather than a copy: into a subdirectory of itself never
        // terminates, and the reverse leaves the old tree sitting inside the new root.
        if (Contains(from, to)) return No($"{to} is inside {from}");
        if (Contains(to, from)) return No($"{to} contains {from}");

        // The pointer does not count as an occupant. Moving away from the default leaves it
        // behind in the directory just emptied, so it is the one thing standing between
        // somebody and moving back — and it is Cairn's own bookkeeping, not "whatever is
        // already there". Refusing over it makes the trip one-way for no reason.
        if (Directory.Exists(to)
            && Directory.EnumerateFileSystemEntries(to)
                .Any(e => !PathsEqual(e, CairnHome.PointerPath)))
            return No(Lang.Get("move-not-empty", to));

        // The last directory is made, the ones above it are not. A mistyped volume is the
        // reason: /Volumes/Bigdsik/cairn would otherwise be created on the boot disk, which
        // is the silent-wrong-place failure this whole feature exists to avoid — and it
        // would look like it had worked right up until the disk filled.
        var parent = Path.GetDirectoryName(to);
        if (!Directory.Exists(to) && (parent is null || !Directory.Exists(parent)))
            return No(Lang.Get("move-parent-missing", parent ?? to, to));

        // A server holding a socket is a server with files open, on a root about to be
        // copied out from under it.
        var running = RunningServers(from);
        if (running.Count > 0)
            return No(Lang.Get("move-server-running", string.Join(", ", running)));

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

        // And then the original goes, which is what makes this a move. Somebody doing this
        // is out of disk space; stopping here would leave them with two of everything and
        // less room than they started with, having agreed to a move.
        //
        // After the repoint, never before: until that line above, the old root is the live
        // one. Everything here has already arrived and been checked file by file.
        var freed = 0L;
        string? problem = null;

        try
        {
            freed = DeleteOldRoot(plan.From, keep, ct);
        }
        catch (Exception e) when (e is MoveFailed or IOException or UnauthorizedAccessException)
        {
            // The move happened. Reporting this as a failure would send somebody looking for
            // data that is exactly where it should be — what is wrong is that the space was
            // not given back, which is a different sentence.
            problem = e.Message;
        }

        return new MoveResult(files, links, bytes, rewritten, plan.From, keep, freed, problem);
    }

    /// <summary>
    /// Removes what was left behind, once the copy has been proven.
    ///
    /// The other half of a move. Copying and repointing is the safe part and it is not the
    /// whole job: somebody moves 40 GB off a disk because the disk is full, and stopping
    /// after the copy leaves them with two copies and less room than they started with.
    ///
    /// Everything except <paramref name="keep"/>, which is the pointer file when the old
    /// root was the default — deleting that would send Cairn back to a default root that is
    /// now empty, undoing the move by way of tidying up. Kept, so the directory survives
    /// holding one line of text.
    /// </summary>
    /// <returns>Bytes removed.</returns>
    public static long DeleteOldRoot(string oldRoot, string? keep, CancellationToken ct = default)
    {
        // First, and unconditionally. This is called with a path that is supposed to be no
        // longer the live root, and being wrong about that deletes everything Cairn has —
        // so the refusal must not depend on anything else being true first.
        //
        // It used to sit behind the existence check below, which made it conditional on the
        // live root happening to exist. That is nearly always the case and was never the
        // case on a CI runner, where nobody has run Cairn: the guard silently did not apply,
        // and the test proving it applied passed only because the developer's own ~/.cairn
        // was there.
        if (PathsEqual(oldRoot, CairnPaths.Root))
            throw new MoveFailed(Lang.Get("move-already-here", oldRoot));

        if (!Directory.Exists(oldRoot)) return 0;

        var freed = 0L;

        foreach (var entry in Directory.EnumerateFileSystemEntries(oldRoot))
        {
            ct.ThrowIfCancellationRequested();

            if (keep is not null && PathsEqual(entry, keep)) continue;

            var info = new FileInfo(entry);

            // A link is unlinked, never followed — deleting through one would take what it
            // points at, which is somewhere else entirely and not ours to remove.
            if (info.LinkTarget is not null) { File.Delete(entry); continue; }

            if (info.Attributes.HasFlag(FileAttributes.Directory))
            {
                freed += Measure(entry).Bytes;
                Directory.Delete(entry, recursive: true);
            }
            else
            {
                freed += info.Length;
                File.Delete(entry);
            }
        }

        // Gone entirely when there was nothing to keep; otherwise it stays, holding the one
        // file that now points Cairn at where everything went.
        if (!Directory.EnumerateFileSystemEntries(oldRoot).Any()) Directory.Delete(oldRoot);

        return freed;
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
                    throw new MoveFailed(Lang.Get("move-missing", target));

                case EntryKind.Link when new FileInfo(target).LinkTarget is null:
                    throw new MoveFailed(Lang.Get("move-missing-link", target));

                case EntryKind.File:
                    var copied = new FileInfo(target);
                    if (!copied.Exists)
                        throw new MoveFailed(Lang.Get("move-missing", target));
                    if (copied.Length != entry.Length)
                        throw new MoveFailed(Lang.Get("move-wrong-size", target, copied.Length, entry.Length));
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
