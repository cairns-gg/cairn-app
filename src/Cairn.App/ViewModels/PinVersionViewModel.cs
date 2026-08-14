using Cairn.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Cairn.Core.ModDb;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cairn.App.ViewModels;

/// <summary>
/// One release, described well enough to choose between it and the one above it.
/// </summary>
public sealed class ReleaseChoiceViewModel(
    string version, string? gameVersions, string? when, bool installed, bool isTrackNewest = false)
{
    public string Version { get; } = version;

    /// <summary>Which game versions it is marked for. The reason a bare version is not enough.</summary>
    public string GameVersions { get; } = gameVersions ?? "";

    public bool HasGameVersions => !string.IsNullOrEmpty(GameVersions);

    /// <summary>How long ago it was published, in words. Empty when ModDB did not say.</summary>
    public string When { get; } = when ?? "";

    public bool HasWhen => !string.IsNullOrEmpty(When);

    /// <summary>The one currently on disk, called out so "freeze what works" is one glance.</summary>
    public bool Installed { get; } = installed;

    public string Note => Installed ? Lang.Get("pin-installed-now") : "";

    /// <summary>
    /// The "follow this mod" row rather than a release.
    ///
    /// Kept in the same list because it is the alternative to every row below it, and a
    /// separate control for it is what the old dropdown did — where "latest" sat among the
    /// versions looking like one of them.
    /// </summary>
    public bool IsTrackNewest { get; } = isTrackNewest;
}

/// <summary>
/// Choosing which version of one mod a pack pins.
///
/// A window rather than a dropdown on the row. Thirty mods meant thirty controls that
/// looked editable for something done rarely and deliberately, and a 120px combo box could
/// only ever show a version number — which is not what decides the question. Here each
/// release can say which game versions it is marked for and when it was published, and
/// there is room to say what pinning means.
/// </summary>
public sealed partial class PinVersionViewModel : ViewModelBase
{
    public PinVersionViewModel(
        string modId,
        string? displayName,
        string? pinnedVersion,
        string? installedVersion,
        IReadOnlyList<ResolvedRelease> releases,
        string gameVersion)
    {
        ModId = modId;
        Title = string.IsNullOrWhiteSpace(displayName) ? modId : displayName!;
        GameVersion = gameVersion;

        Choices.Add(new ReleaseChoiceViewModel(
            PackDetailViewModel.TrackNewest,
            gameVersions: null,
            when: null,
            installed: false,
            isTrackNewest: true));

        foreach (var release in releases)
            Choices.Add(new ReleaseChoiceViewModel(
                release.ModVersion,
                Describe(release.GameVersions),
                Ago(release.Created),
                installed: string.Equals(release.ModVersion, installedVersion,
                    StringComparison.OrdinalIgnoreCase)));

        // Pre-selected in the order somebody actually wants it: what is already pinned,
        // else what is installed. Pinning is nearly always "freeze what works", so landing
        // on that row makes the common case a confirm rather than a hunt.
        Selected = Choices.FirstOrDefault(c => !c.IsTrackNewest
                       && string.Equals(c.Version, pinnedVersion, StringComparison.OrdinalIgnoreCase))
                   ?? Choices.FirstOrDefault(c => c.Installed)
                   ?? Choices.FirstOrDefault();
    }

    public string ModId { get; }
    public string Title { get; }
    public string GameVersion { get; }

    public ObservableCollection<ReleaseChoiceViewModel> Choices { get; } = [];

    [ObservableProperty] public partial ReleaseChoiceViewModel? Selected { get; set; }

    public bool HasReleases => Choices.Any(c => !c.IsTrackNewest);

    /// <summary>
    /// What the confirm button does, which is not always pinning.
    ///
    /// Choosing the follow row and pressing "Pin this version" would be the button saying
    /// the opposite of what it is about to do — and following is a legitimate thing to pick
    /// here, so the answer is to say so rather than to forbid it.
    /// </summary>
    public string ConfirmLabel =>
        Selected?.IsTrackNewest == true ? Lang.Get("pin-follow") : Lang.Get("pin-this-version");

    partial void OnSelectedChanged(ReleaseChoiceViewModel? value) =>
        OnPropertyChanged(nameof(ConfirmLabel));

    /// <summary>Said out loud, because an empty list otherwise reads as a failure to load.</summary>
    public string EmptyNote => Lang.Get("pin-no-release", ModId, GameVersion);

    /// <summary>
    /// The version to pin, or null to follow the mod instead. Read by the caller after the
    /// window closes with a yes.
    /// </summary>
    public string? Result => Selected is null || Selected.IsTrackNewest ? null : Selected.Version;

    /// <summary>
    /// "1.22.0 – 1.22.4", or the single version when there is one.
    ///
    /// A range rather than the full list: releases routinely carry five or six tags, and the
    /// question being answered is whether it covers the pack's version, not which exact
    /// patches the author ticked.
    /// </summary>
    private static string? Describe(IReadOnlyList<string>? gameVersions)
    {
        if (gameVersions is not { Count: > 0 }) return null;

        var ordered = gameVersions
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .OrderBy(v => v, Cairn.Core.GameVersionComparer.Ascending)
            .ToList();

        if (ordered.Count == 0) return null;

        return ordered.Count == 1
            ? Lang.Get("pin-for-version", ordered[0])
            : Lang.Get("pin-for-range", ordered[0], ordered[^1]);
    }

    /// <summary>
    /// "3 weeks ago". Empty when the date cannot be read, which is not worth a row saying so.
    /// </summary>
    private static string? Ago(string? created)
    {
        if (string.IsNullOrWhiteSpace(created)) return null;

        if (!DateTime.TryParse(created, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when))
            return null;

        var days = (DateTime.UtcNow - when).TotalDays;

        return days switch
        {
            < 0 => null,
            < 1 => Lang.Get("ago-today"),
            < 2 => Lang.Get("ago-yesterday"),
            < 14 => Lang.Plural("ago-days", (int)days, (int)days),
            < 60 => Lang.Plural("ago-weeks", (int)(days / 7), (int)(days / 7)),
            < 730 => Lang.Plural("ago-months", (int)(days / 30), (int)(days / 30)),
            _ => Lang.Plural("ago-years", (int)(days / 365), (int)(days / 365)),
        };
    }
}
