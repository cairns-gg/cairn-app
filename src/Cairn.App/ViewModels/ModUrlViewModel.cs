using Cairn.Core;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Cairn.Core.Packs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cairn.App.ViewModels;

/// <summary>
/// Naming the address a mod is fetched from — for a mod being added, or one already in
/// the pack whose author has moved it.
///
/// A window rather than a bare confirmation, because the address is something a person
/// types and gets wrong, and the only way to find out is to fetch it. So the window has
/// two steps: <see cref="CheckCommand"/> fetches the file and says what it is — the name
/// and version out of its own <c>modinfo.json</c>, and the host it came from — and the
/// confirm button stays off until the address on screen is the one that was checked.
/// Nothing is written until the caller reads <see cref="Result"/> after a yes.
///
/// The rule about what an address may be is Core's (<see cref="ModUrl"/>); this only
/// asks it, and shows the answer where it can be acted on.
/// </summary>
/// <param name="fetch">
/// Fetches an address and reads the zip. Injected rather than an HttpClient, so a test can
/// hand it a file without a server and the window never learns how the fetch is done.
/// </param>
/// <param name="modId">
/// The mod whose address is being changed, or null when one is being added. A change must
/// still be that mod: an address serving a different one is a mistake to report, not a
/// rename to perform silently.
/// </param>
public sealed partial class ModUrlViewModel(
    Func<string, CancellationToken, Task<ModUrlInspection>> fetch,
    string? address = null,
    string? modId = null,
    string? displayName = null) : ViewModelBase
{
    /// <summary>The address as it stood when it was last checked, or null before any check.</summary>
    private string? _checked;

    public bool IsChange => modId is not null;

    public string Title => IsChange
        ? Lang.Get("modurl-title-change", string.IsNullOrWhiteSpace(displayName) ? modId : displayName)
        : Lang.Get("modurl-title-add");

    public string Hint => Lang.Get("modurl-hint");

    [ObservableProperty] public partial string Address { get; set; } = address ?? "";

    partial void OnAddressChanged(string value)
    {
        // A checked address that has since been edited is no longer checked. The result
        // stays on screen so what was found is not lost by a stray keystroke, but the
        // confirm button goes off until the new text has been fetched too.
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(CanCheck));
        CheckCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty] public partial bool Checking { get; set; }

    partial void OnCheckingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(CanConfirm));
        CheckCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Why the last check failed, or null. Cleared by the next one.</summary>
    [ObservableProperty] public partial string? Problem { get; set; }

    partial void OnProblemChanged(string? value) => OnPropertyChanged(nameof(HasProblem));

    public bool HasProblem => !string.IsNullOrWhiteSpace(Problem);

    /// <summary>What the last successful check found.</summary>
    [ObservableProperty] public partial ModUrlInspection? Found { get; set; }

    partial void OnFoundChanged(ModUrlInspection? value)
    {
        OnPropertyChanged(nameof(HasFound));
        OnPropertyChanged(nameof(FoundNote));
        OnPropertyChanged(nameof(TrustNote));
        OnPropertyChanged(nameof(CanConfirm));
    }

    public bool HasFound => Found is not null;

    /// <summary>"Anego Tweaks 1.0.0 from files.example".</summary>
    public string FoundNote => Found is null || _checked is null
        ? ""
        : Lang.Get("modurl-found", Found.Describe(), ModUrl.Host(_checked));

    /// <summary>
    /// Said beside what was found, and only then. For most hosts it is that nobody
    /// moderates them, and the person pasting the address is the only one who can say
    /// whether one is trusted. For a ModDB link it is what ModDB said instead: a release
    /// it lists is added as a ModDB mod and the window says so before the button is
    /// pressed, since that is a different thing to be adding; a file it does not list yet
    /// is the case the link form exists for, and says how it will be followed up.
    ///
    /// Only when adding. Changing an address keeps the entry an address whatever ModDB
    /// says, so the listing has no bearing on it.
    /// </summary>
    public string TrustNote
    {
        get
        {
            if (_checked is null || Found is null) return "";

            if (!IsChange)
            {
                switch (Found.Listing)
                {
                    case ModDbListing.Listed when Found.IsListed:
                        return Lang.Get("modurl-listed", Found.ListedVersion);
                    case ModDbListing.Unlisted:
                        return Lang.Get("modurl-unlisted");
                    case ModDbListing.Unknown:
                        return Lang.Get("modurl-moddb-unknown");
                }
            }

            return Lang.Get("modurl-trust", ModUrl.Host(_checked));
        }
    }

    public bool CanCheck => !Checking && ModUrl.LooksLikeUrl(Address);

    /// <summary>Only once the address on screen is the one that was fetched and found to be a mod.</summary>
    public bool CanConfirm =>
        !Checking && Found is not null && _checked is not null
        && string.Equals(_checked, Address.Trim(), StringComparison.Ordinal);

    public string ConfirmLabel => IsChange ? Lang.Get("modurl-use") : Lang.Get("modurl-add");

    /// <summary>The address to record, or null when nothing checked out. Read after a yes.</summary>
    public string? Result => CanConfirm ? _checked : null;

    /// <summary>The mod id the checked file declares. Null until a check succeeds.</summary>
    public string? ModId => Found?.ModId;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task Check()
    {
        var url = Address.Trim();
        Problem = null;

        if (ModUrl.Problem(url) is { } bad)
        {
            Problem = Lang.Get("mods-url-bad", bad);
            return;
        }

        Checking = true;
        try
        {
            ModUrlInspection found;
            try
            {
                found = await fetch(url, CancellationToken.None);
            }
            catch (Exception e) when (e is HttpRequestException or IOException)
            {
                Problem = Lang.Get("mods-url-unreachable", ModUrl.Host(url), e.Message);
                return;
            }

            if (!found.IsMod)
            {
                Problem = Lang.Get("mods-url-not-a-mod", ModUrl.Host(url), found.Problem);
                return;
            }

            if (modId is not null && !string.Equals(found.ModId, modId, StringComparison.OrdinalIgnoreCase))
            {
                Problem = Lang.Get("modurl-wrong-mod", found.ModId, modId);
                return;
            }

            _checked = url;
            Found = found;
        }
        finally
        {
            Checking = false;
        }
    }
}
