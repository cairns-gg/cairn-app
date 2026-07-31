using Cairn.Core.Packs;
using Cairn.Core.Runtime;

namespace Cairn.Core.Games;

/// <summary>One thing cleanup would delete.</summary>
public sealed record CleanupTarget(string Label, string Directory, long Bytes);

/// <summary>
/// What cleanup would remove, worked out without deleting anything.
///
/// Installing a game version per pack adds up fast — 600 MB each, and a pack retargeted
/// twice leaves two behind that nothing points at any more. Nothing here is irreplaceable:
/// every version is re-downloadable and Play fetches whatever a pack needs. That is what
/// makes this safe to offer, and it is still shown in full before anything happens.
/// </summary>
public sealed record CleanupPlan(
    IReadOnlyList<CleanupTarget> Versions,
    IReadOnlyList<CleanupTarget> Runtimes,
    IReadOnlyList<string> Kept,
    string? Blocked = null)
{
    public bool AnythingToDo => Versions.Count > 0 || Runtimes.Count > 0;

    /// <summary>Set when the question could not be answered, as distinct from "nothing to do".</summary>
    public bool IsBlocked => Blocked is not null;

    public long TotalBytes => Versions.Sum(v => v.Bytes) + Runtimes.Sum(r => r.Bytes);

    /// <summary>The lines a confirmation shows: every item, named, with its size.</summary>
    public IReadOnlyList<string> Describe()
    {
        var lines = new List<string>();

        foreach (var v in Versions)
            lines.Add($"Vintage Story {v.Label} ({Bytes.Human(v.Bytes)})");

        foreach (var r in Runtimes)
            lines.Add($".NET {r.Label} ({Bytes.Human(r.Bytes)}) — nothing left needs it");

        return lines;
    }
}

public static class GameCleanup
{
    /// <summary>
    /// Game versions no pack targets, plus any private .NET runtime left with nothing to
    /// run — removing a version and leaving its runtime behind is not much of a cleanup.
    ///
    /// Only ever installs Cairn made. An install found on the machine is not Cairn's to
    /// delete, and GameStore.ListInstalled is the only thing consulted here.
    /// </summary>
    public static CleanupPlan Plan(GameStore games, RuntimeStore runtimes, PackStore packs)
    {
        var (used, unreadable) = VersionsUsedBy(packs);

        // A pack whose manifest will not load might need any version at all. Sweeping on
        // that basis could delete the one thing it was about to launch, so this refuses to
        // guess rather than quietly treating the pack as needing nothing.
        if (unreadable.Count > 0)
            return new CleanupPlan([], [], [],
                Blocked: $"Cannot read {string.Join(", ", unreadable)}, so what is unused is unknown.");

        return Plan(games, runtimes, used);
    }

    public static CleanupPlan Plan(
        GameStore games, RuntimeStore runtimes, IEnumerable<string> keepVersions)
    {
        var keep = new HashSet<string>(keepVersions, StringComparer.OrdinalIgnoreCase);

        var installed = games.ListInstalled().ToList();
        var kept = installed.Where(i => keep.Contains(i.Version)).ToList();

        var versions = installed
            .Where(i => !keep.Contains(i.Version))
            .Select(i => new CleanupTarget(i.Version, i.Directory, DirectoryGrowth.Measure(i.Directory)))
            .ToList();

        var orphaned = runtimes.ListInstalled()
            // Kept rather than deleted-from: a runtime is worth keeping if anything that
            // survives could use it. Over-keeping costs disk; over-deleting costs a launch.
            .Where(r => !kept.Any(i => Serves(r, i)))
            .Select(r => new CleanupTarget(
                Path.GetFileName(r.Root), r.Root, DirectoryGrowth.Measure(r.Root)))
            .ToList();

        return new CleanupPlan(versions, orphaned, [.. kept.Select(i => i.Version)]);
    }

    private static bool Serves(DotnetRuntime runtime, GameInstall install) =>
        runtime.Satisfies(install.RequiredFramework)
        && (runtime.Arch == install.Architecture || runtime.Arch == ExecutableArch.Unknown);

    /// <summary>Every game version some pack points at, and the packs that could not be read.</summary>
    private static (List<string> Used, List<string> Unreadable) VersionsUsedBy(PackStore packs)
    {
        List<string> used = [];
        List<string> unreadable = [];

        foreach (var id in packs.ListIds())
        {
            try
            {
                used.Add(packs.Load(id).GameVersion);
            }
            catch (Exception e) when (e is IOException or InvalidDataException
                                          or System.Text.Json.JsonException)
            {
                unreadable.Add(id);
            }
        }

        return (used, unreadable);
    }
}
