using System.Collections.Generic;
using System.Linq;
using Cairn.Core.Packs;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cairn.App.ViewModels;

/// <summary>One mod as it would be published, as a row.</summary>
public sealed class PublishModViewModel(PublishMod mod)
{
    public PublishMod Mod { get; } = mod;

    public string ModId => Mod.ModId;
    public string Version => Mod.Version ?? "";
    public bool Pinned => Mod.Pinned;
    public bool Missing => !Mod.OnModDb;

    public string Note => Missing ? "not on ModDB" : Pinned ? "pinned" : "";
}

/// <summary>
/// A checked, unsent publish.
///
/// Mostly disclosure rather than configuration: the only real choices are the slug, who
/// can see it, and whether the pack's server address goes with it. Everything else on the
/// screen is there to be read before the button is pressed.
/// </summary>
public sealed partial class ShareViewModel : ViewModelBase
{
    public ShareViewModel(PublishPlan plan, string packName, string? username, PackLink? link)
    {
        Plan = plan;
        PackName = packName;
        Username = username;

        Slug = link?.Url is { Length: > 0 } url
            ? url[(url.LastIndexOf('/') + 1)..]
            : plan.PackId;

        AlreadyPublished = link?.Published is not null;
        IsPublic = link?.Published?.Visibility == "public";

        // A pack handed to your own players is exactly when the server address is wanted,
        // and a public one almost never is — so a first publish strips it for public and
        // keeps it for unlisted. A re-publish keeps whatever was chosen last time.
        StripConnect = link?.Published is { } published
            ? published.Connect == "stripped"
            : IsPublic;
    }

    public PublishPlan Plan { get; }

    public string PackName { get; }

    /// <summary>Null when nobody is signed in, which is what the sign-in step is for.</summary>
    public string? Username { get; }

    public bool IsSignedIn => !string.IsNullOrWhiteSpace(Username);

    public string SignedInAs => IsSignedIn ? $"Signed in as {Username}" : "";

    public bool AlreadyPublished { get; }

    public IReadOnlyList<PublishModViewModel> Mods { get; init; } = [];

    // ---- the choices ----

    [ObservableProperty] public partial string Slug { get; set; }

    [ObservableProperty] public partial bool IsPublic { get; set; }

    /// <summary>Leave the pack's server address out of what gets published.</summary>
    [ObservableProperty] public partial bool StripConnect { get; set; }

    partial void OnSlugChanged(string value) => OnPropertyChanged(nameof(UrlPreview));

    /// <summary>
    /// Turning a pack public strips its server address, unless this pack was already
    /// published with the address in — in which case the choice was made deliberately and
    /// is not ours to undo.
    ///
    /// Moving another control from under the user is normally wrong. It is allowed here
    /// because it only ever moves toward not disclosing an address, and the alternative is
    /// someone flipping to Public and publishing their server to the browse list.
    /// </summary>
    partial void OnIsPublicChanged(bool value)
    {
        if (value && !AlreadyPublished) StripConnect = true;
    }

    public string UrlPreview => $"cairns.gg/{Username ?? "you"}/{Slug}";

    // ---- what the window says ----

    public string Title => $"Share \"{PackName}\"";

    public string Summary => Plan.Summary();

    public string PublishLabel => AlreadyPublished ? "Publish changes" : "Publish";

    public bool HasConnect => Plan.HasConnect;

    public string ConnectWarning =>
        $"This pack carries the server address {Plan.Connect}.";

    public bool AnythingUnresolvable => Plan.AnythingUnresolvable;

    public string UnresolvableWarning => Plan.UnresolvableWarning();

    public bool CannotPublish => !Plan.CanPublish;

    public string LockProblem => Plan.LockProblem ?? "";

    /// <summary>
    /// False while the lockfile does not cover the manifest. Publishing a pack whose lock
    /// is stale would advertise reproducibility it cannot deliver, so this refuses rather
    /// than warns.
    /// </summary>
    public bool CanPublish => Plan.CanPublish;

    public static ShareViewModel From(
        PublishPlan plan, string packName, string? username, PackLink? link) =>
        new(plan, packName, username, link)
        {
            // Worst first, the same habit as the version-change dialog: the reason to say
            // no should not need scrolling to.
            Mods = [.. plan.Mods
                .OrderBy(m => m.OnModDb ? 1 : 0)
                .ThenBy(m => m.ModId, System.StringComparer.OrdinalIgnoreCase)
                .Select(m => new PublishModViewModel(m))],
        };
}
