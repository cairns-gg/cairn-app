using Cairn.Core;
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

    public string Note => Missing ? Lang.Get("share-not-on-moddb") : Pinned ? Lang.Get("share-pinned") : "";
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
    private readonly PublishRecord? _published;
    private readonly Func<bool, string>? _documentFor;
    private readonly string _publishedSlug;

    /// <param name="documentFor">
    /// What publishing would send, given whether the server address is stripped — a
    /// function rather than a string because that choice is made in this window, and the
    /// two documents differ. Compared against what went last time so an unchanged pack
    /// cannot be published again. Null skips the check, erring toward allowing rather than
    /// blocking on a comparison that could not be made.
    /// </param>
    public ShareViewModel(
        PublishPlan plan, string packName, string? username, PackLink? link,
        Func<bool, string>? documentFor = null)
    {
        Plan = plan;
        PackName = packName;
        Username = username;
        _published = link?.Published;
        _documentFor = documentFor;

        Slug = link?.Url is { Length: > 0 } url
            ? url[(url.LastIndexOf('/') + 1)..]
            : plan.PackId;

        _publishedSlug = Slug;

        AlreadyPublished = link?.Published is not null;
        Revision = link?.Revision ?? 0;
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

    public string SignedInAs => IsSignedIn ? Lang.Get("share-signed-in-as", Username) : "";

    public bool AlreadyPublished { get; }

    /// <summary>The revision already published, so "nothing has changed" can name it.</summary>
    public int Revision { get; }

    /// <summary>
    /// The URL cannot be edited once published, because on this server the URL *is* the
    /// pack. Publishing the same pack under a different slug does not move it — it creates
    /// a second pack, and leaves the first one sitting there under the same name, which is
    /// how you end up with two identical-looking packs and no idea which is live.
    /// </summary>
    public bool SlugFixed => AlreadyPublished;

    public string SlugNote => SlugFixed ? Lang.Get("share-slug-fixed") : "";

    public IReadOnlyList<PublishModViewModel> Mods { get; init; } = [];

    // ---- the choices ----

    [ObservableProperty] public partial string Slug { get; set; }

    [ObservableProperty] public partial bool IsPublic { get; set; }

    /// <summary>Leave the pack's server address out of what gets published.</summary>
    [ObservableProperty] public partial bool StripConnect { get; set; }

    partial void OnSlugChanged(string value)
    {
        OnPropertyChanged(nameof(UrlPreview));
        RecheckWhetherAnythingChanged();
    }

    partial void OnStripConnectChanged(bool value) => RecheckWhetherAnythingChanged();

    private void RecheckWhetherAnythingChanged()
    {
        OnPropertyChanged(nameof(NothingToPublish));
        OnPropertyChanged(nameof(UnchangedNote));
        OnPropertyChanged(nameof(CanPublish));
    }

    /// <summary>
    /// True when this would publish a revision identical to the one already up.
    ///
    /// The window still opens on an unchanged pack, because the choices in it — who can
    /// see it, whether the server address goes — are the reason to come back to a pack
    /// that has not otherwise changed. Changing one of those makes this false again. What
    /// is refused is only the empty case: a new revision differing from its predecessor in
    /// nothing but its number, which tells every follower there is an update and then has
    /// none for them.
    /// </summary>
    public bool NothingToPublish =>
        _published is not null
        && _documentFor is not null
        && Slug == _publishedSlug
        && !_published.WouldChange(_documentFor(StripConnect), IsPublic, StripConnect);

    public string UnchangedNote => NothingToPublish
        ? Lang.Get("share-unchanged", Revision)
        : "";

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

        RecheckWhetherAnythingChanged();
    }

    public string UrlPreview => $"cairns.gg/{Username ?? "you"}/{Slug}";

    // ---- what the window says ----

    public string Title => Lang.Get("share-title", PackName);

    public string Summary => Plan.Summary();

    public string PublishLabel => AlreadyPublished ? Lang.Get("share-publish-changes") : Lang.Get("share-publish");

    public bool HasConnect => Plan.HasConnect;

    public string ConnectWarning => Lang.Get("share-connect-warning", Plan.Connect);

    public bool AnythingUnresolvable => Plan.AnythingUnresolvable;

    public string UnresolvableWarning => Plan.UnresolvableWarning();

    public bool CannotPublish => !Plan.CanPublish;

    public string LockProblem => Plan.LockProblem ?? "";

    /// <summary>
    /// False while the lockfile does not cover the manifest — publishing a pack whose lock
    /// is stale would advertise reproducibility it cannot deliver, so that refuses rather
    /// than warns — and false when this would send a revision identical to the last.
    /// </summary>
    public bool CanPublish => Plan.CanPublish && !NothingToPublish;

    public static ShareViewModel From(
        PublishPlan plan, string packName, string? username, PackLink? link,
        Func<bool, string>? documentFor = null) =>
        new(plan, packName, username, link, documentFor)
        {
            // Worst first, the same habit as the version-change dialog: the reason to say
            // no should not need scrolling to.
            Mods = [.. plan.Mods
                .OrderBy(m => m.OnModDb ? 1 : 0)
                .ThenBy(m => m.ModId, System.StringComparer.OrdinalIgnoreCase)
                .Select(m => new PublishModViewModel(m))],
        };
}
