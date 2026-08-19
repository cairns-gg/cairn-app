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

        // Listed, on a first publish. It was unlisted, on the reasoning that the quieter
        // choice is the safer default — and what that produced was authors who had shared a
        // pack and did not know that nobody could find it. A preselected radio above a
        // single button is not a decision anybody makes; it is one they accept.
        //
        // The privacy given up is smaller than the wording suggests. An unlisted pack's URL
        // is public either way — being unlisted is being absent from browse, not from its
        // own address — so what this changes is whether a pack can be discovered, not
        // whether it can be reached. The part that is genuinely sensitive is the server
        // address, and defaulting to public strips that rather than exposing it.
        //
        // A pack already published keeps whatever was chosen for it, here and below.
        IsPublic = _published is null || _published.Visibility == "public";

        // A pack handed to your own players is exactly when the server address is wanted,
        // and a public one almost never is — so a first publish strips it for public and
        // keeps it for unlisted. A re-publish keeps whatever was chosen last time.
        StripConnect = _published is not null
            ? _published.Connect == "stripped"
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
        OnPropertyChanged(nameof(DeltaLine));
        OnPropertyChanged(nameof(ShowDelta));
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

    /// <summary>
    /// The rest of what the pack carries — see <see cref="PublishPlan.Carries"/>. The mod
    /// list is on screen; the settings and hotkeys are not, and this is the last screen
    /// before they are sent.
    /// </summary>
    public string Carries => Plan.Carries();

    /// <summary>
    /// Shown on a first publish and not after. Once there is a revision to compare against,
    /// what changed is the more useful of the two and the two together repeat each other —
    /// "it carries 3 mod settings" above "3 mod settings changed" is one fact twice.
    /// </summary>
    public bool CarriesAnything => Plan.CarriesAnything && !AlreadyPublished;

    /// <summary>
    /// What this publish would change about the revision already at the pack's address, or
    /// null on a first publish — where there is nothing to compare against and the list of
    /// what the pack contains is the whole answer.
    ///
    /// Set by the caller, which is the half that can reach the network. Null also covers not
    /// having been able to ask, which <see cref="DeltaLine"/> says rather than passing off as
    /// nothing having changed.
    /// </summary>
    public PublishDelta? Delta { get; init; }

    /// <summary>Whether the site answered at all. See <see cref="DeltaLine"/>.</summary>
    public bool DeltaKnown { get; init; }

    public bool ShowDelta => AlreadyPublished && DeltaLine.Length > 0;

    /// <summary>
    /// The line above the mod list, on a pack that has been published before.
    ///
    /// Three states, and the third is why this is not just a summary of the delta: a site
    /// that could not be reached has to say so, because "nothing has changed" is the one
    /// thing it must not be mistaken for on the screen where somebody decides whether to
    /// press Publish.
    /// </summary>
    public string DeltaLine
    {
        get
        {
            if (!DeltaKnown) return Lang.Get("publish-delta-unknown", Revision);

            if (Delta is { Anything: true } delta)
                return Lang.Get("publish-delta-since", Revision, delta.Describe());

            // Not "nothing has changed", which this is in no position to say: the document
            // is what decides that, it knows about the publish options as well, and
            // UnchangedNote says it from there. This line names what it can see, and a
            // difference it cannot name — a lockfile re-resolved to the same versions, say —
            // is still a difference. Claiming otherwise put "nothing has changed" on the
            // same screen as an enabled Publish button.
            return NothingToPublish ? "" : Lang.Get("publish-delta-something", Revision);
        }
    }

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

    /// <param name="delta">
    /// What this publish would change about the revision on the site, or null when there is
    /// none to compare against or the site could not be asked — <paramref name="deltaKnown"/>
    /// is what tells those apart.
    /// </param>
    public static ShareViewModel From(
        PublishPlan plan, string packName, string? username, PackLink? link,
        Func<bool, string>? documentFor = null,
        PublishDelta? delta = null, bool deltaKnown = false) =>
        new(plan, packName, username, link, documentFor)
        {
            Delta = delta,
            DeltaKnown = deltaKnown,

            // Worst first, the same habit as the version-change dialog: the reason to say
            // no should not need scrolling to.
            Mods = [.. plan.Mods
                .OrderBy(m => m.OnModDb ? 1 : 0)
                .ThenBy(m => m.ModId, System.StringComparer.OrdinalIgnoreCase)
                .Select(m => new PublishModViewModel(m))],
        };
}
