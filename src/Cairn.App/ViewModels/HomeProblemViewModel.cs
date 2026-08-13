namespace Cairn.App.ViewModels;

/// <summary>
/// Shown instead of the launcher when Cairn cannot reach where its files are kept.
///
/// The alternative was starting anyway on the default root, which would be empty — and an
/// empty launcher does not read as "the disk holding your packs is not plugged in", it reads
/// as "everything is gone". The next thing it offers is downloading the game again, beside
/// data that is perfectly fine and merely unreachable.
///
/// So: say which path, say nothing has been touched, and offer the only two things that
/// honestly help. Reconnecting the disk and starting again is the one that keeps the packs;
/// going back to the default is the one that admits defeat without pretending the data was
/// lost, and it says where the data still is.
/// </summary>
public sealed class HomeProblemViewModel(string problem, string pointsAt)
{
    public string Problem { get; } = problem;

    /// <summary>Where the setting says the files are — still true, and still where they are.</summary>
    public string PointsAt { get; } = pointsAt;

    public string Reassurance { get; } =
        "Nothing has been read, written or deleted. Your packs are wherever that path leads, "
        + "and will be there again when it does.";
}
