using Cairn.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using Cairn.Core.Packs;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cairn.App.ViewModels;

/// <summary>
/// One mod's fate in an update, as a row.
///
/// Observable because two of the kinds are questions: the checkbox writes straight through
/// to the plan, so what is on screen and what Apply would do cannot drift apart.
/// </summary>
public sealed partial class ModChangeViewModel(ModChange change) : ObservableObject
{
    public ModChange Change { get; } = change;

    public string ModId => Change.ModId;
    public string Note => Change.Describe();
    public bool IsChoice => Change.IsChoice;

    /// <summary>
    /// Set while the whole plan is being reset, which does not consult the answers. Leaving
    /// the controls on screen would invite somebody to answer a question about to be
    /// ignored — and the row itself still belongs in the list, because it is still a
    /// difference the reset is about to resolve.
    /// </summary>
    public bool Suppressed
    {
        get => _suppressed;
        set
        {
            if (_suppressed == value) return;

            _suppressed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowChoice));
            OnPropertyChanged(nameof(CanSilence));
        }
    }

    private bool _suppressed;

    public bool ShowChoice => IsChoice && !Suppressed;

    /// <summary>
    /// A word for the outcome, so the list reads down its left edge rather than by parsing
    /// every note — the same habit as the version-change dialog.
    /// </summary>
    public string Label => Change.Kind switch
    {
        ModChangeKind.Added => Lang.Get("packupdate-adds"),
        ModChangeKind.Removed => Lang.Get("packupdate-removes"),
        ModChangeKind.Repinned => Lang.Get("packupdate-moves"),
        ModChangeKind.Relocked => Lang.Get("packupdate-updates"),
        ModChangeKind.DroppedByYou => Lang.Get("packupdate-you-removed"),
        ModChangeKind.PinConflict => Lang.Get("packupdate-you-pinned"),
        ModChangeKind.Yours => Lang.Get("packupdate-yours"),
        _ => "",
    };

    /// <summary>Only the questions are coloured; the rest is just what an update does.</summary>
    public bool Warns => Change.Kind == ModChangeKind.DroppedByYou;

    public bool Take
    {
        get => Change.Take;
        set
        {
            if (Change.Take == value) return;

            Change.Take = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ChoiceLabel));
            OnPropertyChanged(nameof(CanSilence));
        }
    }

    /// <summary>
    /// Says what the checkbox means in this row's own terms. "Take theirs" is meaningless
    /// against a mod you removed, where the question is whether to put it back.
    /// </summary>
    public string ChoiceLabel => Change.Kind switch
    {
        ModChangeKind.DroppedByYou =>
            Take ? Lang.Get("packupdate-put-back") : Lang.Get("packupdate-leave-out"),
        ModChangeKind.PinConflict => Take
            ? Lang.Get("packupdate-use-theirs", Change.Theirs ?? Lang.Get("packupdate-newest"))
            : Lang.Get("packupdate-keep-yours", Change.Mine ?? Lang.Get("packupdate-newest")),
        _ => "",
    };

    /// <summary>
    /// Offered only where the difference is permanent. A pin conflict resolves itself the
    /// moment either side moves; a mod you removed from a pack that still ships it stays
    /// true for ever, and is the only one worth being able to silence.
    ///
    /// Hidden once "put it back" is ticked, because there is then nothing left to ask.
    /// </summary>
    public bool CanSilence => Change.CanSilence && !Take && !Suppressed;

    public bool Silence
    {
        get => Change.Silence;
        set
        {
            if (Change.Silence == value) return;

            Change.Silence = value;
            OnPropertyChanged();
        }
    }
}

/// <summary>
/// A checked, uncommitted pack update.
///
/// Its existence is the confirmation state, exactly as VersionChangeViewModel's is: built
/// by the check, thrown away by Apply or Cancel, so nothing lands that was not looked at.
/// </summary>
public sealed partial class PackUpdateViewModel(
    PackUpdatePlan plan, string packName, IReadOnlyList<string>? worlds = null)
    : ObservableObject
{
    public PackUpdatePlan Plan { get; } = plan;

    public string PackName { get; } = packName;

    /// <summary>
    /// Worlds under this pack's data path. Only read to say what a reset would be doing to
    /// them — see <see cref="ResetWarning"/>.
    /// </summary>
    private readonly IReadOnlyList<string> _worlds = worlds ?? [];

    /// <summary>
    /// Take the author's pack exactly, discarding this copy's changes.
    ///
    /// Off, and deliberately not a shortcut anybody falls into: it is the one answer here
    /// that removes mods nobody said to remove.
    /// </summary>
    public bool Reset
    {
        get => Plan.Reset;
        set
        {
            if (Plan.Reset == value) return;

            Plan.Reset = value;

            // The rows carry the combined condition, because a control shows only when the
            // row is a question and the plan is still asking.
            foreach (var row in Changes) row.Suppressed = value;

            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowChoices));
            OnPropertyChanged(nameof(ResetRemovesAnything));
            OnPropertyChanged(nameof(ResetWarning));
            OnPropertyChanged(nameof(ApplyLabel));
        }
    }

    /// <summary>
    /// The questions are not asked while resetting, because a reset does not consult them.
    /// Leaving them on screen would invite somebody to answer a question that is about to
    /// be ignored.
    /// </summary>
    public bool ShowChoices => !Reset;

    public bool ResetRemovesAnything => Plan.ResetRemovesAnything;

    /// <summary>
    /// What a reset would take out, and what that costs.
    ///
    /// The mods are named because "your changes" is not something anybody can weigh. The
    /// worlds are named because a Vintage Story save holds blocks and items from the mods
    /// that built it — removing one from a pack a world was made in is a change to the
    /// save, not to a list, and it is the half of this nobody expects.
    /// </summary>
    public string ResetWarning
    {
        get
        {
            var going = Plan.RemovedByReset.ToList();
            if (going.Count == 0) return "";

            var text = Lang.Plural("packupdate-removes-mods", going.Count, going.Count,
                string.Join(", ", going.Take(6)) + (going.Count > 6 ? ", …" : ""));

            if (_worlds.Count > 0)
                text += " " + Lang.Plural("packupdate-world-warning", _worlds.Count, _worlds.Count,
                    string.Join(", ", _worlds.Take(3)) + (_worlds.Count > 3 ? ", …" : ""));

            return text;
        }
    }

    public IReadOnlyList<ModChangeViewModel> Changes { get; } =
        // Questions first: the thing needing an answer should not need scrolling to. Then
        // the author's changes, then what is merely yours.
        [.. plan.Changes
            .OrderBy(c => c.Kind switch
            {
                ModChangeKind.DroppedByYou => 0,
                ModChangeKind.PinConflict => 1,
                ModChangeKind.Added => 2,
                ModChangeKind.Removed => 3,
                ModChangeKind.Repinned => 4,
                ModChangeKind.Relocked => 5,
                _ => 6,
            })
            .ThenBy(c => c.ModId, StringComparer.OrdinalIgnoreCase)
            .Select(c => new ModChangeViewModel(c))];

    public string Summary => Plan.Summary();

    public string ApplyLabel => Reset
        ? Lang.Get("packupdate-apply-reset", Plan.ToRevision)
        : Lang.Get("packupdate-apply-update", Plan.ToRevision);

    public bool HasChanges => Changes.Count > 0;

    public bool HasChoices => Plan.Choices.Any();

    public string ChoiceNote =>
        Lang.Plural("packupdate-choices", Plan.Choices.Count(), Plan.Choices.Count());

    public bool GameVersionChanges => Plan.GameVersionChanges;

    public string GameVersionNote =>
        Lang.Get("packupdate-game-version", Plan.PreviousGameVersion, Plan.GameVersion);

    /// <summary>
    /// Said plainly, because the alternative reading — "it worked out what you changed" —
    /// is the one somebody would act on.
    /// </summary>
    public bool IsBlind => !Plan.HasBase;

    public string BlindNote => Lang.Get("packupdate-blind");
}
