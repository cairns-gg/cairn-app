using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Cairn.App.ViewModels;
using Cairn.App.Views;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.App.Tests;

/// <summary>
/// A mod the pack names by address, as the launcher draws it and as the launcher adds and
/// moves it.
///
/// The rule is Core's — <see cref="ModUrl"/> — and these are about the things only a
/// window can get wrong: a row that draws such a mod like any other, hiding the one fact
/// about it worth a glance; a button that is not there; and an address window whose
/// answer is not written back to the pack.
/// </summary>
[Collection(AvaloniaTests.Collection)]
public class ModUrlRowTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "cairn-modurl-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly string? _previous = Environment.GetEnvironmentVariable("CAIRN_HOME");

    public ModUrlRowTests()
    {
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("CAIRN_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CAIRN_HOME", _previous);
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    private const string Address = "https://files.example/anegotweaks.zip";

    private PackStore Store => new(Path.Combine(_home, "packs"));

    private (MainWindow Window, MainViewModel Vm) Open(OfflineHandler? handler = null)
    {
        var vm = new MainViewModel(handler ?? new OfflineHandler());
        var window = new MainWindow { DataContext = vm };
        window.Show();

        vm.Confirm = null;
        vm.ConfirmVersionChange = null;
        vm.ConfirmImport = null;

        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        return (window, vm);
    }

    /// <summary>
    /// Stands in for the window: presses Check, then answers yes when something checked out.
    /// Keeps the view model so a test can read what the window would have shown.
    /// </summary>
    private static Func<ModUrlViewModel, Task<bool>> Window(Action<ModUrlViewModel>? seen = null, bool agree = true) =>
        async choice =>
        {
            seen?.Invoke(choice);
            await choice.CheckCommand.ExecuteAsync(null);
            return agree && choice.Result is not null;
        };

    /// <summary>A genuine mod zip, so the add can read what it is.</summary>
    private static byte[] Zip(string modId, string name, string version)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("modinfo.json").Open());
            writer.Write($"{{\"type\":\"content\",\"modid\":\"{modId}\",\"name\":\"{name}\",\"version\":\"{version}\"}}");
        }

        return buffer.ToArray();
    }

    private void PackWithOneOfEach()
    {
        Store.Create("anego", "1.22.5");
        var manifest = Store.Load("anego");
        manifest.Mods.Add(new PackMod { ModId = "anegotweaks", Url = Address });
        manifest.Mods.Add(new PackMod { ModId = "carryon" });
        Store.Save(manifest);

        new PackLock
        {
            GameVersion = "1.22.5",
            Mods =
            [
                new LockedMod { ModId = "anegotweaks", Version = "1.0.0", FileName = "anegotweaks.zip", Url = Address, FromUrl = true, Sha256 = "ab" },
                new LockedMod { ModId = "carryon", Version = "1.8.0", FileName = "carryon_1.8.0.zip", Sha256 = "cd" },
            ],
        }.Save(Store.LockPath("anego"));
    }

    private static Dictionary<string, Button> Buttons(Visual root) =>
        root.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Content is string)
            .GroupBy(b => (string)b.Content!)
            .ToDictionary(g => g.Key, g => g.First());

    [AvaloniaFact]
    public void A_mod_named_by_address_says_where_it_is_from_and_offers_an_address_instead_of_a_pin()
    {
        PackWithOneOfEach();

        var (window, vm) = Open();

        var rows = vm.Detail!.Mods.ToDictionary(r => r.ModId);
        Assert.True(rows["anegotweaks"].IsFromUrl);
        Assert.False(rows["anegotweaks"].CanPin);
        Assert.True(rows["anegotweaks"].CanChangeUrl);
        Assert.False(rows["anegotweaks"].HasPage);
        Assert.True(rows["carryon"].CanPin);
        Assert.False(rows["carryon"].CanChangeUrl);
        Assert.True(rows["carryon"].HasPage);

        // And drawn that way: the host on the row, Address… where the pin would be, and no
        // View beside it.
        var notes = window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Text == "from files.example").ToList();
        Assert.Single(notes);
        Assert.True(notes[0].IsEffectivelyVisible);

        var pins = window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Name == "PinButton").ToList();
        Assert.Equal(2, pins.Count);
        Assert.Single(pins, p => p.IsEffectivelyVisible);

        var addresses = window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Name == "AddressButton").ToList();
        Assert.Equal(2, addresses.Count);
        Assert.Single(addresses, a => a.IsEffectivelyVisible);

        var views = window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Content is "View").ToList();
        Assert.Equal(2, views.Count);
        Assert.Single(views, v => v.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void A_followed_pack_does_not_offer_the_address()
    {
        var manifest = new PackManifest
        {
            Id = "anego",
            GameVersion = "1.22.5",
            Mods = [new PackMod { ModId = "anegotweaks", Url = Address }],
        };

        Store.Import(
            new PackBundle { Pack = manifest, CanonicalUrl = "https://cairns.gg/dizzyd/anego", Revision = 1 },
            sourceUrl: "https://cairns.gg/dizzyd/anego");

        var (window, vm) = Open();

        Assert.False(vm.Detail!.CanEditMods);
        Assert.False(vm.Detail.Mods.Single().CanChangeUrl);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(),
            b => b.Name == "AddressButton" && b.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task A_link_in_the_box_turns_search_into_add_and_the_window_adds_what_it_found()
    {
        Store.Create("anego", "1.22.5");

        var handler = new OfflineHandler();
        handler.ServeBytes("/anegotweaks.zip", Zip("anegotweaks", "Anego Tweaks", "1.0.0"));

        var (window, vm) = Open(handler);

        ModUrlViewModel? shown = null;
        vm.ChooseModUrl = Window(c => shown = c);

        // The button says what it is about to do, before it is pressed.
        Assert.True(Buttons(window)["Search"].IsEffectivelyVisible);
        vm.Detail!.SearchText = Address;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(Buttons(window)["Add from link"].IsEffectivelyVisible);

        await vm.Detail.SearchCommand.ExecuteAsync(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // The window opened on the link, and said what the zip said it was and where it
        // would come from.
        Assert.NotNull(shown);
        Assert.False(shown!.IsChange);
        Assert.Equal(Address, shown.Address);
        Assert.Equal("Anego Tweaks 1.0.0 from files.example", shown.FoundNote);
        Assert.Contains("files.example", shown.TrustNote);

        var mod = Assert.Single(Store.Load("anego").Mods);
        Assert.Equal("anegotweaks", mod.ModId);
        Assert.Equal(Address, mod.Url);

        // And the box is clear again, so the button is Search once more.
        Assert.Equal("", vm.Detail.SearchText);
        Assert.Empty(vm.Detail.SearchHits);

        // Nothing went to ModDB to find out.
        Assert.DoesNotContain(handler.Urls, u => u.Contains("mods.vintagestory.at"));

        // Adding syncs, so the row settles before the test tears its directory down.
        for (var i = 0; i < 100 && Store.LoadLock("anego") is null; i++)
        {
            await Task.Delay(50);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        Assert.True(Store.LoadLock("anego")?.Mods.Single().FromUrl);
    }

    [AvaloniaFact]
    public async Task Cancelling_the_window_adds_nothing_and_keeps_the_link_in_the_box()
    {
        Store.Create("anego", "1.22.5");

        var handler = new OfflineHandler();
        handler.ServeBytes("/anegotweaks.zip", Zip("anegotweaks", "Anego Tweaks", "1.0.0"));

        var (_, vm) = Open(handler);
        vm.ChooseModUrl = Window(agree: false);

        vm.Detail!.SearchText = Address;
        await vm.Detail.SearchCommand.ExecuteAsync(null);

        Assert.Empty(Store.Load("anego").Mods);
        Assert.Empty(vm.Detail.SearchHits);
        Assert.Equal(Address, vm.Detail.SearchText);
    }

    [AvaloniaFact]
    public async Task Changing_the_address_writes_the_new_one_and_refuses_a_different_mod()
    {
        PackWithOneOfEach();

        const string moved = "https://files.example/anegotweaks_1.1.0.zip";
        var handler = new OfflineHandler();
        handler.ServeBytes("/anegotweaks_1.1.0.zip", Zip("anegotweaks", "Anego Tweaks", "1.1.0"));
        handler.ServeBytes("/other.zip", Zip("other", "Other", "1.0.0"));

        var (_, vm) = Open(handler);
        var row = vm.Detail!.Mods.Single(r => r.ModId == "anegotweaks");

        // The wrong mod at the new address: the window says so and nothing is written.
        ModUrlViewModel? shown = null;
        vm.ChooseModUrl = Window(c =>
        {
            shown = c;
            c.Address = "https://files.example/other.zip";
        });

        await row.ChangeUrlCommand.ExecuteAsync(null);

        Assert.True(shown!.IsChange);
        Assert.Contains("not 'anegotweaks'", shown.Problem);
        Assert.Null(shown.Result);
        Assert.Equal(Address, Store.Load("anego").Mods.Single(m => m.ModId == "anegotweaks").Url);

        // The right mod at the new address: written, and the row now says so.
        vm.ChooseModUrl = Window(c =>
        {
            Assert.Equal(Address, c.Address);   // prefilled with where it is fetched from now
            c.Address = moved;
        });

        await row.ChangeUrlCommand.ExecuteAsync(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(moved, Store.Load("anego").Mods.Single(m => m.ModId == "anegotweaks").Url);

        for (var i = 0; i < 100 && Store.LoadLock("anego")?.Mods.FirstOrDefault(m => m.ModId == "anegotweaks")?.Version != "1.1.0"; i++)
        {
            await Task.Delay(50);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal("1.1.0", Store.LoadLock("anego")!.Mods.Single(m => m.ModId == "anegotweaks").Version);
    }

    [AvaloniaFact]
    public async Task A_mod_listed_on_ModDB_since_it_was_added_by_link_is_followed_from_there_once_taken()
    {
        const string link = "https://mods.vintagestory.at/download/118768/augur_0.1.1.zip";
        var zip = Zip("augur", "Augur", "0.1.1");
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(zip));

        Store.Create("anego", "1.22.5");
        var manifest = Store.Load("anego");
        manifest.Mods.Add(new PackMod { ModId = "augur", Url = link });
        Store.Save(manifest);

        // Installed from the link, as a sync would have left it.
        File.WriteAllBytes(Path.Combine(Store.ModsDir("anego"), "augur_0.1.1.zip"), zip);
        new PackLock
        {
            GameVersion = "1.22.5",
            Mods = [new LockedMod { ModId = "augur", Version = "0.1.1", FileName = "augur_0.1.1.zip", Url = link, FromUrl = true, FileId = 118768, Sha256 = sha }],
        }.Save(Store.LockPath("anego"));

        // ModDB has listed it since, serving the same file as its one release.
        var handler = new OfflineHandler();
        handler.ServeBytes("augur_0.1.1.zip", zip);
        handler.ServeAlways("/api/mod/augur", """
            {"statuscode":"200","mod":{"modid":9,"assetid":10,"name":"Augur","urlalias":"augur","side":"universal",
             "releases":[{"releaseid":7,"fileid":118768,"modidstr":"augur","modversion":"0.1.1",
                          "filename":"augur_0.1.1.zip","mainfile":"https://moddbcdn.vintagestory.at/augur_0.1.1.zip","tags":["1.22.5"]}]}}
            """);

        var (_, vm) = Open(handler);

        await vm.Detail!.CheckUpdatesCommand.ExecuteAsync(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var row = vm.Detail.Mods.Single();
        Assert.True(row.HasUpdate);
        Assert.Contains("now listed on ModDB", row.UpdateAvailable);

        row.UpdateCommand.Execute(null);

        for (var i = 0; i < 100 && Store.Load("anego").Mods.Single().Url is not null; i++)
        {
            await Task.Delay(50);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        // The address is off the entry, and the lock now describes a ModDB release.
        var taken = Store.Load("anego").Mods.Single();
        Assert.Null(taken.Url);
        Assert.Null(taken.Version);

        for (var i = 0; i < 100 && Store.LoadLock("anego")?.Mods.SingleOrDefault()?.FromUrl != false; i++)
        {
            await Task.Delay(50);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        var locked = Store.LoadLock("anego")!.Mods.Single();
        Assert.False(locked.FromUrl);
        Assert.Equal(7, locked.ReleaseId);
        Assert.False(vm.Detail.Mods.Single().HasUpdate);
    }
}
