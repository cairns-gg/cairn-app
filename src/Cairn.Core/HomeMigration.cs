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
/// Why the old copy is still there, wholly or in part, or null. Not a failure of the move:
/// it can only happen after the commit point, so it says the space was not reclaimed.
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
/// The order is the safety property. The repoint is the commit point: a failure or a cancel
/// before it leaves the original root live and untouched, and there is no window in which
/// Cairn is pointed at a tree still being written. After it the move has happened, whatever
/// follows. The original is removed last, once it is no longer live and every file has been
/// checked — removed rather than left, because whoever asks for this is out of disk space,
/// and two copies with a note about the second has not moved anything.
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

        // A root that is not there yet is an empty one, not a refusal.
        //
        // Nothing creates it until Cairn writes something — a setting changed, a pack made,
        // a ModDB page browsed — so a fresh install has none. That is precisely the moment
        // somebody who cares where their files go opens Preferences and says where, and they
        // were told there was nothing to move, as though choosing required something to have
        // been put in the wrong place first.
        var exists = Directory.Exists(from);

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

        var (files, links, bytes) = exists ? Measure(from) : (0, 0, 0L);

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

        // Nothing to copy and nothing to check, which is a whole move for a root that does
        // not exist yet: what makes it take effect is the repoint below, and that is the
        // same line either way. Walk would throw on the missing directory rather than
        // yielding nothing, and it is reached twice — here and in Verify.
        foreach (var entry in Directory.Exists(plan.From) ? Walk(plan.From, ct) : [])
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
                    try
                    {
                        File.Copy(entry.Full, target, overwrite: true);
                    }
                    catch (IOException) when (IsLog(entry.Relative))
                    {
                        // Rotated away between the walk and the copy, or caught mid-append.
                        // Not worth a failed move: see Verify.
                        continue;
                    }
                    files++;
                    bytes += entry.Length;
                    progress?.Report(new MoveProgress(
                        files, plan.Files, bytes, plan.Bytes, entry.Relative));
                    break;
            }
        }

        Verify(plan, ct);

        var rewritten = RewriteInstallDirectories(plan.From, plan.To);

        // The commit point. Everything above can fail, or be cancelled, and leave the old
        // root live and untouched; from here on the new one is live, and nothing that
        // happens afterwards may be reported as the move not having happened.
        (repoint ?? CairnHome.SetPointer)(plan.To);

        var keep = PointerToKeep(plan.From);
        var freed = 0L;
        string? problem = null;

        try
        {
            freed = DeleteOldRoot(plan.From, keep, ct);
        }
        catch (Exception e) when (e is MoveFailed or IOException or UnauthorizedAccessException)
        {
            problem = e.Message;
        }
        catch (OperationCanceledException)
        {
            // Stopping is still honoured, but as a clean-up stopped part-way: answered as a
            // cancelled move, the caller told somebody to delete the destination — the live
            // root, by then — as a part-copy.
            problem = Lang.Get("move-cleanup-cancelled");
        }

        return new MoveResult(files, links, bytes, rewritten, plan.From, keep, freed, problem);
    }

    /// <summary>
    /// Removes the old root once it is no longer the live one — the last step of a move, and
    /// <c>cairn-cli home discard</c> for one whose last step did not finish.
    ///
    /// Everything except <paramref name="keep"/>, the pointer when the old root was the
    /// default: deleting it would send Cairn back to a default root that is now empty.
    /// Refuses anything <see cref="DiscardProblem(string)"/> objects to.
    /// </summary>
    /// <returns>Bytes removed.</returns>
    public static long DeleteOldRoot(string oldRoot, string? keep, CancellationToken ct = default)
    {
        // First, and unconditionally — ahead of the existence check, where it once sat and
        // so silently did not apply on a machine with no live root yet, which is every CI
        // runner.
        if (DiscardProblem(oldRoot, CairnPaths.Root, CairnHome.PointerPath) is { } problem)
            throw new MoveFailed(problem);

        if (!Directory.Exists(oldRoot)) return 0;

        var freed = 0L;

        foreach (var entry in Directory.EnumerateFileSystemEntries(oldRoot))
        {
            ct.ThrowIfCancellationRequested();

            if (keep is not null && SamePlace(entry, keep)) continue;

            var info = new FileInfo(entry);

            // A link is unlinked, never followed — deleting through one would take what it
            // points at, which is somewhere else entirely and not ours to remove.
            //
            // Which call does the unlinking is a platform question wearing a portable API's
            // clothes: File.Delete removes a symlink of either kind on Unix, and Windows
            // refuses one that points at a directory, wanting Directory.Delete. Asked of the
            // entry's own attributes rather than of the operating system, because the
            // attribute is there on both and the distinction is real on both.
            //
            // Directory.Delete without recursion on a link removes the link and not what is
            // behind it, which is the same promise File.Delete makes here.
            if (info.LinkTarget is not null)
            {
                if (info.Attributes.HasFlag(FileAttributes.Directory)) Directory.Delete(entry);
                else File.Delete(entry);

                continue;
            }

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
        if (!Directory.Exists(plan.From)) return;

        foreach (var entry in Walk(plan.From, ct))
        {
            // Except Cairn's own log, which goes on being written while the move runs — the
            // move itself is something worth logging — so the original has grown past the
            // copy, or been rotated out from under it, by the time anything checks. A copy
            // short of the last few lines loses nothing that matters, and refusing a move over
            // it would make moving impossible whenever anything at all was happening.
            if (IsLog(entry.Relative)) continue;

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

    /// <summary>
    /// Why <paramref name="oldRoot"/> must not be deleted, or null when it may be. Both front
    /// ends ask this one question, and <see cref="DeleteOldRoot"/> asks it again itself.
    ///
    /// Containing, not only being. Equality was the whole check once, and a root moved to
    /// <c>/data/old/cairn</c> then discarded as <c>/data/old</c> passed it — the confirmation
    /// said the live root was not touched, and a recursive delete of its parent took it.
    ///
    /// The pointer as well, for the same shape of mistake: it is kept only as a direct child,
    /// so an old root holding it deeper down — a home directory holding <c>~/.cairn</c> — would
    /// take it, and Cairn would wake up in an empty default root.
    /// </summary>
    public static string? DiscardProblem(string oldRoot) =>
        DiscardProblem(oldRoot, CairnPaths.Root, CairnHome.PointerPath);

    /// <param name="liveRoot">The root Cairn is reading, injected for testing.</param>
    /// <param name="pointer">Where the pointer file is, whether or not it exists.</param>
    public static string? DiscardProblem(string oldRoot, string liveRoot, string pointer)
    {
        if (SamePlace(oldRoot, liveRoot)) return Lang.Get("move-already-here", oldRoot);
        if (Holds(oldRoot, liveRoot)) return Lang.Get("move-holds-live", oldRoot, liveRoot);

        if (File.Exists(pointer) && Holds(oldRoot, pointer) && PointerToKeep(oldRoot, pointer) is null)
            return Lang.Get("move-holds-pointer", oldRoot, pointer);

        return null;
    }

    /// <summary>
    /// The pointer file, when it sits directly in <paramref name="oldRoot"/> and so has to
    /// survive the discard — see <see cref="DeleteOldRoot"/>. Otherwise null.
    /// </summary>
    public static string? PointerToKeep(string oldRoot) => PointerToKeep(oldRoot, CairnHome.PointerPath);

    private static string? PointerToKeep(string oldRoot, string pointer) =>
        File.Exists(pointer) && Path.GetDirectoryName(Path.GetFullPath(pointer)) is { } dir
                             && SamePlace(dir, oldRoot)
            ? pointer
            : null;

    /// <summary>
    /// The same directory, however it was spelled. Links along either path are followed, and
    /// case is ignored on every platform.
    ///
    /// Both choices err the same way, which is the only way this question can afford to err.
    /// macOS volumes are case-insensitive by default while .NET compares paths ordinally
    /// there, and <c>/tmp</c> is a link to <c>/private/tmp</c>; either alias made the live root
    /// look like somewhere else. Ignoring case on Linux can call two different directories
    /// the same one, which costs a refusal and a retyped path — the opposite mistake costs
    /// everything Cairn has.
    /// </summary>
    private static bool SamePlace(string a, string b) =>
        string.Equals(Resolved(a), Resolved(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Strictly underneath, by the same rules as <see cref="SamePlace"/>.</summary>
    private static bool Holds(string outer, string inner)
    {
        // A root keeps its separator through the trim — "/" stays "/" — and appending
        // another would make a prefix nothing starts with, so "/" would hold nothing.
        var o = Resolved(outer);
        if (!o.EndsWith(Path.DirectorySeparatorChar)) o += Path.DirectorySeparatorChar;

        return Resolved(inner).StartsWith(o, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The path with every link along it resolved, component by component. A component that
    /// does not exist is kept as written: the live root may not have been created yet, and a
    /// path that is not there yet is still a place it could be.
    /// </summary>
    private static string Resolved(string path, int depth = 0)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(full) ?? "";
        var current = root;

        foreach (var part in full[root.Length..].Split(
                     Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, part);

            FileSystemInfo? target = null;
            try
            {
                target = new FileInfo(next).ResolveLinkTarget(returnFinalTarget: true);
            }
            catch (IOException) { /* a broken or looping link: compare it as written */ }

            // A link's target can itself run through links in its parents, which
            // returnFinalTarget does not undo; resolved again, a little way, for those.
            current = target is null ? next
                : depth < 8 ? Resolved(target.FullName, depth + 1)
                : target.FullName;
        }

        return Path.TrimEndingDirectorySeparator(current);
    }

    /// <summary>Under <see cref="CairnPaths.LogsRoot"/>, given a path relative to the root.</summary>
    private static bool IsLog(string relative) =>
        relative.Split(Path.DirectorySeparatorChar)[0] == CairnPaths.LogsDirName;

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
