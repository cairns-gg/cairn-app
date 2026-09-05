using System.Net.Http;
using Cairn.App.ViewModels;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.App.Tests;

/// <summary>
/// The address window, as a view model: it confirms only what it has fetched, and it
/// says why when it will not.
/// </summary>
public class ModUrlDialogTests
{
    private static ModUrlInspection Mod(string modId = "anegotweaks", string version = "1.0.0") =>
        new(modId, "Anego Tweaks", version, "ab", null);

    private static Func<string, CancellationToken, Task<ModUrlInspection>> Serving(
        Func<string, ModUrlInspection> answer) =>
        (url, _) => Task.FromResult(answer(url));

    [Fact]
    public async Task Confirms_only_the_address_it_checked()
    {
        var vm = new ModUrlViewModel(Serving(_ => Mod()));

        Assert.False(vm.CanCheck);
        Assert.False(vm.CanConfirm);

        vm.Address = "https://files.example/mod.zip";
        Assert.True(vm.CanCheck);
        Assert.False(vm.CanConfirm);

        await vm.CheckCommand.ExecuteAsync(null);
        Assert.True(vm.CanConfirm);
        Assert.Equal("https://files.example/mod.zip", vm.Result);
        Assert.Equal("anegotweaks", vm.ModId);
        Assert.Equal("Anego Tweaks 1.0.0 from files.example", vm.FoundNote);

        // Edit the address and the answer no longer applies to what is on screen.
        vm.Address = "https://files.example/mod2.zip";
        Assert.False(vm.CanConfirm);
        Assert.Null(vm.Result);
    }

    [Fact]
    public async Task A_plaintext_address_is_refused_without_fetching()
    {
        var fetched = 0;
        var vm = new ModUrlViewModel((_, _) => { fetched++; return Task.FromResult(Mod()); })
        {
            Address = "http://files.example/mod.zip",
        };

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(0, fetched);
        Assert.Contains("https", vm.Problem);
        Assert.False(vm.CanConfirm);
    }

    [Fact]
    public async Task A_host_that_cannot_be_reached_and_a_file_that_is_not_a_mod_each_say_so()
    {
        var down = new ModUrlViewModel((_, _) => throw new HttpRequestException("boom"))
        {
            Address = "https://files.example/mod.zip",
        };
        await down.CheckCommand.ExecuteAsync(null);
        Assert.Contains("Could not fetch files.example", down.Problem);

        var page = new ModUrlViewModel(Serving(_ => new ModUrlInspection(null, null, null, "ab", "not a zip")))
        {
            Address = "https://files.example/mod.zip",
        };
        await page.CheckCommand.ExecuteAsync(null);
        Assert.Contains("is not a mod", page.Problem);
        Assert.False(page.CanConfirm);
    }

    [Fact]
    public async Task Changing_an_address_insists_on_the_same_mod()
    {
        var vm = new ModUrlViewModel(Serving(_ => Mod("other")),
            address: "https://files.example/old.zip", modId: "anegotweaks", displayName: "Anego Tweaks");

        Assert.True(vm.IsChange);
        Assert.Contains("Anego Tweaks", vm.Title);
        Assert.Equal("https://files.example/old.zip", vm.Address);

        vm.Address = "https://files.example/new.zip";
        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Contains("'other', not 'anegotweaks'", vm.Problem);
        Assert.False(vm.CanConfirm);
    }

    [Fact]
    public async Task A_ModDB_link_says_what_ModDB_said_instead_of_the_trust_note()
    {
        const string link = "https://mods.vintagestory.at/download/118768/augur_0.1.1.zip";

        var listed = new ModUrlViewModel(Serving(_ => Mod("augur", "0.1.1") with
        {
            FileId = 118768, Listing = ModDbListing.Listed, ListedVersion = "0.1.1",
        })) { Address = link };
        await listed.CheckCommand.ExecuteAsync(null);
        Assert.Contains("Listed on ModDB as 0.1.1", listed.TrustNote);
        Assert.Contains("pinned", listed.TrustNote);

        var unlisted = new ModUrlViewModel(Serving(_ => Mod("augur", "0.1.1") with
        {
            FileId = 118768, Listing = ModDbListing.Unlisted,
        })) { Address = link };
        await unlisted.CheckCommand.ExecuteAsync(null);
        Assert.Contains("not listed yet", unlisted.TrustNote);

        // Changing an address keeps it an address, so the listing is not the point there.
        var change = new ModUrlViewModel(Serving(_ => Mod("augur", "0.1.1") with
        {
            FileId = 118768, Listing = ModDbListing.Listed, ListedVersion = "0.1.1",
        }), address: "https://files.example/old.zip", modId: "augur") { Address = link };
        await change.CheckCommand.ExecuteAsync(null);
        Assert.Contains("Nobody moderates", change.TrustNote);
    }
}
