using System;
using System.Collections.Generic;
using System.Linq;
using Cairn.Core.Packs;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cairn.App.ViewModels;

/// <summary>One mod the pack would bring, as a row.</summary>
public sealed class ImportModViewModel(
    string modId, string? version, bool fromLock, bool dependency = false)
{
    public string ModId { get; } = modId;

    /// <summary>Blank means the pack names no version, so sync resolves the newest.</summary>
    public string Version { get; } = version ?? "";

    public bool Exact { get; } = fromLock;

    /// <summary>In the lock but not the manifest: something a mod asked for in turn.</summary>
    public bool Dependency { get; } = dependency;

    public string Note => Dependency ? "dependency"
        : Exact ? ""
        : Version.Length > 0 ? "asked for" : "newest";
}

/// <summary>
/// What a pack would bring, shown before it is taken on.
///
/// This exists because following a link used to drop a URL into a text box and leave the
/// person to press a button on it — which asks them to approve something they have not
/// been shown. A link can come from anywhere, so the answer is not to trust it less but to
/// say plainly what is in it: who published it, from where, and every mod and version it
/// would install.
///
/// Pure disclosure apart from two choices — what to call it locally, and whether this copy
/// follows the author or starts a pack of your own.
/// </summary>
public sealed partial class ImportViewModel : ViewModelBase
{
    private readonly Func<string, bool> _idTaken;

    /// <param name="fetched">
    /// Whether Cairn fetched this from <paramref name="source"/> itself, as opposed to
    /// reading it out of a file or a pasted blob. It decides which address a follow would
    /// use and whether one may be preselected — see <see cref="FollowNote"/>.
    /// </param>
    public ImportViewModel(
        PackBundle bundle, string source, Func<string, bool> idTaken, bool fetched = true)
    {
        Bundle = bundle;
        _idTaken = idTaken;
        Fetched = fetched;

        // Through PageUrl on both paths, because this is the address that will be written
        // down and it has to be the one somebody was shown. PackStore.Import normalises
        // whatever it is given the same way, so showing the document's raw claim here
        // meant the dialog could name one address and the link record another — only for
        // a claim ending in .json, which no real pack has, but the two must not be capable
        // of disagreeing at all.
        FollowUrl = PackUpdateCheck.PageUrl(fetched ? source : bundle.CanonicalUrl ?? "");

        // Preselected only where the address is one Cairn watched this arrive from.
        // Leaving it unanswered for a file is the point rather than an oversight: the only
        // address on offer there is the document's own word, and a preselected "follow" is
        // that word being acted on with a person's consent standing in front of it.
        Follow = fetched ? true : null;

        var manifest = bundle.Pack!;

        PackName = manifest.Name is { Length: > 0 } name ? name : manifest.Id;
        Description = manifest.Description;
        GameVersion = manifest.GameVersion;
        Connect = manifest.Connect;
        PublishedBy = bundle.PublishedBy;
        Source = HostOf(source);

        // The lock is the author's tested set; the manifest is only what they asked for.
        // The two differ by more than versions — a mod pulled in to satisfy a dependency
        // is in the lock and not the manifest — so a list built from the manifest alone
        // undercounts what would actually be installed, which is the question this screen
        // is being asked.
        var locked = bundle.Lock?.Mods
            .ToDictionary(m => m.ModId, m => m.Version, StringComparer.OrdinalIgnoreCase);

        var asked = new HashSet<string>(
            manifest.Mods.Select(m => m.ModId), StringComparer.OrdinalIgnoreCase);

        Mods = manifest.Mods
            .Select(m => locked is not null && locked.TryGetValue(m.ModId, out var exact)
                ? new ImportModViewModel(m.ModId, exact, fromLock: true)
                : new ImportModViewModel(m.ModId, m.Version, fromLock: false))
            .Concat((bundle.Lock?.Mods ?? [])
                .Where(l => !asked.Contains(l.ModId))
                .Select(l => new ImportModViewModel(l.ModId, l.Version, fromLock: true, dependency: true)))
            .OrderBy(m => m.ModId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        HasLock = bundle.Lock is not null;
        AsId = manifest.Id;
    }

    public PackBundle Bundle { get; }

    public string PackName { get; }

    /// <summary>
    /// The author's own words, and the only thing on this screen that says what the pack
    /// is *for*. The mod list says what is in it; a list of mods is not an answer to
    /// "should I want this".
    /// </summary>
    public string? Description { get; }

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public string GameVersion { get; }

    /// <summary>The pack's own server, if it has one. Worth saying out loud — see below.</summary>
    public string? Connect { get; }

    public bool HasConnect => !string.IsNullOrWhiteSpace(Connect);

    /// <summary>
    /// A pack that carries a server address will launch straight into somebody's server.
    /// That is usually the point, and it is still not something to discover afterwards.
    /// </summary>
    public string ConnectNote => $"Launches into {Connect}";

    public string? PublishedBy { get; }

    /// <summary>
    /// The host this arrived from, or — for a file — the host it claims to have come from.
    /// Only the first of those is something a person can judge, which is why
    /// <see cref="Provenance"/> does not phrase them the same way.
    /// </summary>
    public string Source { get; }

    /// <summary>
    /// Who published it and where from, and whether either of those was checked.
    ///
    /// Fetched, both are facts about an exchange that happened: Cairn asked that address
    /// and this is what came back. Out of a file nothing was asked and nothing was
    /// checked — <c>publishedBy</c> and <c>canonicalUrl</c> are two strings inside a
    /// document anybody can write, and they were being rendered in the same unqualified
    /// sentence as the fetched case, in the one line somebody reads to decide whether a
    /// link they were sent is worth trusting. The values are still worth showing;
    /// presenting a claim as an observation is the part that misleads.
    ///
    /// The follow choice below says a version of this too, but only about the address, and
    /// only when there is a choice to make. This line is always there.
    /// </summary>
    public string Provenance
    {
        get
        {
            var by = PublishedBy is { Length: > 0 } who ? $"by {who} · " : "";

            return Fetched ? $"{by}from {Source}" : $"the file says: {by}from {Source}";
        }
    }

    public IReadOnlyList<ImportModViewModel> Mods { get; }

    public bool HasLock { get; }

    public string Summary =>
        $"{Mods.Count} mod{(Mods.Count == 1 ? "" : "s")} · game {GameVersion}";

    /// <summary>
    /// What the pack will install, stated rather than offered.
    ///
    /// There used to be a toggle here for taking the mod list without the author's
    /// versions. It is gone: a lock exists so that a shared pack reproduces, and inviting
    /// somebody to discard it at the moment they take the pack on offers them the one
    /// outcome nobody wants — a pack that resembles the author's and is not it. The CLI
    /// keeps --loose for the rare case, where asking for it is deliberate.
    /// </summary>
    public string VersionNote => HasLock
        ? "Installs the exact versions the author tested, checked against their checksums."
        : "This pack carries no lockfile, so sync will resolve the newest compatible releases.";

    // ---- the choices ----

    [ObservableProperty] public partial string AsId { get; set; }

    // ---- following, or starting your own ----

    /// <summary>Whether Cairn fetched this itself. See the constructor.</summary>
    public bool Fetched { get; }

    /// <summary>
    /// The address a follow would check back with: the one this was fetched from, or —
    /// for a file — the one the document names itself. Null when it names none.
    /// </summary>
    public string? FollowUrl { get; }

    /// <summary>
    /// Whether there is anything to decide. A document that came off nobody's server has
    /// no owner to follow, so the question would be noise.
    /// </summary>
    public bool CanChooseFollow => Bundle.IsPublished && FollowUrl is { Length: > 0 };

    /// <summary>
    /// Null until answered. Tri-state rather than a bool because "not yet said" and "no"
    /// are different answers, and a file's default must be the first.
    /// </summary>
    [ObservableProperty] public partial bool? Follow { get; set; }

    /// <summary>
    /// Where following would go, and how much Cairn actually knows about it.
    ///
    /// The distinction is the whole reason the choice exists. An address Cairn fetched
    /// from is one it watched this document arrive from; an address inside a file is that
    /// file's claim about itself, which nothing has checked and which would otherwise
    /// decide where this machine checks back for ever.
    /// </summary>
    public string FollowNote => Fetched
        ? $"Keep in step with {FollowUrl}, where this came from."
        : $"This file says it comes from {FollowUrl}. Cairn has not checked that — "
          + "following takes the file's word for it.";

    public string ForkNote =>
        "Start a pack of your own from it. Nothing checks back, and it is yours to "
        + "change, publish or share.";

    /// <summary>
    /// The two radio buttons, as plain bools.
    ///
    /// A tri-state cannot drive <c>IsChecked</c> directly without a converter that has to
    /// guess what unchecking means, and in a group the uncheck of one arrives around the
    /// check of the other — so "neither" and "the other one" become order-dependent. Two
    /// derived properties have no such ambiguity: both false is the unanswered state, and
    /// only a check ever writes.
    /// </summary>
    public bool FollowChosen
    {
        get => Follow == true;
        set { if (value) Follow = true; }
    }

    public bool ForkChosen
    {
        get => Follow == false;
        set { if (value) Follow = false; }
    }

    partial void OnFollowChanged(bool? value)
    {
        OnPropertyChanged(nameof(FollowChosen));
        OnPropertyChanged(nameof(ForkChosen));
        OnPropertyChanged(nameof(NeedsFollowAnswer));
        OnPropertyChanged(nameof(CanAdd));
    }

    /// <summary>What <see cref="PackStore.Import"/> is told, once somebody has said.</summary>
    public ImportIntent Intent => Follow == true ? ImportIntent.Follow : ImportIntent.Fork;

    partial void OnAsIdChanged(string value)
    {
        OnPropertyChanged(nameof(IdConflict));
        OnPropertyChanged(nameof(HasIdConflict));
        OnPropertyChanged(nameof(CanAdd));
    }

    /// <summary>
    /// Caught here rather than at the end. Import refuses an id already in use, and
    /// finding that out after saying yes — with the dialog gone and an error in its place
    /// — is the worst moment to learn the one thing that was fixable on the form.
    /// </summary>
    public string? IdConflict
    {
        get
        {
            var id = (AsId ?? "").Trim();

            if (id.Length == 0) return "Give it a name to install under.";

            return _idTaken(id) ? $"You already have a pack called '{id}'." : null;
        }
    }

    public bool HasIdConflict => IdConflict is not null;

    /// <summary>
    /// Blocked until an unpreselected choice has been made. Only a file reaches this: a
    /// fetched document starts on "follow", which is what it has always done.
    /// </summary>
    public bool NeedsFollowAnswer => CanChooseFollow && Follow is null;

    public bool CanAdd => !HasIdConflict && !NeedsFollowAnswer;

    /// <summary>
    /// The host alone, because that is the part worth reading. A full URL puts the domain
    /// in the middle of a long string, which is exactly where a misleading one hides.
    /// </summary>
    private static string HostOf(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}"
            : source;
}
