using System.Collections.Generic;
using System.Linq;
using Cairn.Core.Packs;

namespace Cairn.App.ViewModels;

/// <summary>One mod's fate, as a row.</summary>
public sealed class ModVerdictViewModel(ModVerdict verdict)
{
    public ModVerdict Verdict { get; } = verdict;

    public string ModId => Verdict.ModId;
    public string Note => Verdict.Note;

    public bool Breaks => Verdict.Breaks;
    public bool Warns => Verdict.Warns || Verdict.Unknown;

    /// <summary>
    /// A word for the outcome, so the list can be read down its left edge rather than by
    /// parsing every note.
    /// </summary>
    public string Label => Verdict.Outcome switch
    {
        ModOutcome.Unchanged => "keeps",
        ModOutcome.Moves => "updates",
        ModOutcome.Approximate => "untested",
        ModOutcome.Unavailable => "breaks",
        ModOutcome.PinUnavailable => "pin fails",
        _ => "unknown",
    };
}

/// <summary>
/// A checked, uncommitted game-version change.
///
/// Its existence is the confirmation state: this object is built by the check and thrown
/// away by Apply or Cancel, so nothing can be applied that was not first looked at.
/// </summary>
public sealed class VersionChangeViewModel(VersionChangePlan plan)
{
    public VersionChangePlan Plan { get; } = plan;

    public IReadOnlyList<ModVerdictViewModel> Mods { get; } =
        // Worst first: the reason to say no should not need scrolling to.
        [.. plan.Mods
            .OrderBy(m => m.Breaks ? 0 : m.Unknown ? 1 : m.Warns ? 2 : m.Changes ? 3 : 4)
            .ThenBy(m => m.ModId, System.StringComparer.OrdinalIgnoreCase)
            .Select(m => new ModVerdictViewModel(m))];

    public string Summary => Plan.Summary();

    public string ApplyLabel => $"Change to {Plan.To}";

    public bool AnythingBreaks => Plan.AnythingBreaks;
    public bool IsIncomplete => Plan.IsIncomplete;
    public bool RisksWorlds => Plan.RisksWorlds;

    public bool HasMods => Mods.Count > 0;

    public string WorldWarning =>
        $"This pack has {Plan.Worlds.Count} world{(Plan.Worlds.Count == 1 ? "" : "s")} "
        + $"({string.Join(", ", Plan.Worlds.Take(3))}"
        + $"{(Plan.Worlds.Count > 3 ? ", …" : "")}). "
        + "Vintage Story upgrades a save when a newer build opens it, and will not open one "
        + $"saved by a build newer than {Plan.To}. Back them up before going back.";

    /// <summary>
    /// Said plainly because the alternative reading — "checked, nothing wrong" — is the
    /// one that gets acted on.
    /// </summary>
    public string IncompleteWarning =>
        $"{Plan.Unchecked.Count()} mod{(Plan.Unchecked.Count() == 1 ? "" : "s")} could not be "
        + "checked, so this is not the whole picture.";

    public string BreakWarning =>
        $"{Plan.Breaking.Count()} mod{(Plan.Breaking.Count() == 1 ? "" : "s")} "
        + $"ha{(Plan.Breaking.Count() == 1 ? "s" : "ve")} nothing published for {Plan.To}. "
        + "Applying leaves them out of the pack until they are updated or removed.";
}
