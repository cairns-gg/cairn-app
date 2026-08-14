using Cairn.Core;
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
        ModOutcome.Unchanged => Lang.Get("verdict-keeps"),
        ModOutcome.Moves => Lang.Get("verdict-updates"),
        ModOutcome.Approximate => Lang.Get("verdict-untested"),
        ModOutcome.Unavailable => Lang.Get("verdict-breaks"),
        ModOutcome.PinUnavailable => Lang.Get("verdict-pin-fails"),
        _ => Lang.Get("verdict-unknown"),
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

    public string ApplyLabel => Lang.Get("versionchange-apply", Plan.To);

    public bool AnythingBreaks => Plan.AnythingBreaks;
    public bool IsIncomplete => Plan.IsIncomplete;
    public bool RisksWorlds => Plan.RisksWorlds;

    public bool HasMods => Mods.Count > 0;

    public string WorldWarning => Lang.Plural(
        "versionchange-world-warning", Plan.Worlds.Count, Plan.Worlds.Count,
        string.Join(", ", Plan.Worlds.Take(3)) + (Plan.Worlds.Count > 3 ? ", …" : ""),
        Plan.To);

    /// <summary>
    /// Said plainly because the alternative reading — "checked, nothing wrong" — is the
    /// one that gets acted on.
    /// </summary>
    public string IncompleteWarning =>
        Lang.Plural("versionchange-incomplete", Plan.Unchecked.Count(), Plan.Unchecked.Count());

    // Two inflections in one sentence — the noun and the verb agreeing with it — which
    // is exactly the shape that cannot survive being assembled from fragments.
    public string BreakWarning =>
        Lang.Plural("versionchange-breaking", Plan.Breaking.Count(), Plan.Breaking.Count(), Plan.To);
}
