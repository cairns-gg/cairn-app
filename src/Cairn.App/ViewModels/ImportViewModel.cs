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
/// Pure disclosure apart from two choices — what to call it locally, and whether to
/// reproduce the author's exact versions.
/// </summary>
public sealed partial class ImportViewModel : ViewModelBase
{
    private readonly Func<string, bool> _idTaken;

    public ImportViewModel(PackBundle bundle, string source, Func<string, bool> idTaken)
    {
        Bundle = bundle;
        _idTaken = idTaken;

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

    /// <summary>Where it was fetched from, which is the part a person can judge.</summary>
    public string Source { get; }

    public string Provenance => PublishedBy is { Length: > 0 } who
        ? $"by {who} · from {Source}"
        : $"from {Source}";

    public IReadOnlyList<ImportModViewModel> Mods { get; }

    public bool HasLock { get; }

    public string Summary =>
        $"{Mods.Count} mod{(Mods.Count == 1 ? "" : "s")} · game {GameVersion}";

    /// <summary>
    /// Without a lock there is nothing to reproduce, and the toggle would be a lie — sync
    /// resolves newest-compatible whatever it is set to.
    /// </summary>
    public string VersionNote => HasLock
        ? "Exact versions the author tested, checked against their checksums."
        : "This pack carries no lockfile, so sync will resolve the newest compatible releases.";

    // ---- the choices ----

    [ObservableProperty] public partial string AsId { get; set; }

    /// <summary>Off only deliberately: the whole value of a shared pack is that it matches.</summary>
    [ObservableProperty] public partial bool Reproduce { get; set; } = true;

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

    public bool CanAdd => !HasIdConflict;

    /// <summary>
    /// The host alone, because that is the part worth reading. A full URL puts the domain
    /// in the middle of a long string, which is exactly where a misleading one hides.
    /// </summary>
    private static string HostOf(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}"
            : source;
}
