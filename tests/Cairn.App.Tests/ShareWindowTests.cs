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
/// The share window, which is mostly disclosure: what is about to be published, what of it
/// is a real server address, and what recipients will not be able to install. Its job is to
/// be read before Publish is pressed.
/// </summary>
[Collection(AvaloniaTests.Collection)]
public class ShareWindowTests
{
    private static PublishPlan Plan(
        int mods = 2, string? connect = null, bool missing = false, string? lockProblem = null)
    {
        var list = Enumerable.Range(1, mods)
            .Select(i => new PublishMod($"mod-{i:00}", "1.0.0", Pinned: false, OnModDb: true))
            .ToList();

        if (missing) list.Add(new PublishMod("homegrown", "1.1.0", Pinned: false, OnModDb: false));

        return new PublishPlan("anego", list, connect, lockProblem is null, lockProblem);
    }

    private static (ShareWindow Window, ShareViewModel Vm) Show(
        PublishPlan plan, PackLink? link = null, string? username = "dizzyd")
    {
        var vm = ShareViewModel.From(plan, "Anego Server", username, link);
        var window = new ShareWindow { DataContext = vm };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        return (window, vm);
    }

    private static Button Find(Visual root, string name) =>
        root.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);

    [AvaloniaFact]
    public void It_lists_what_would_be_published()
    {
        var (window, vm) = Show(Plan(mods: 3));

        Assert.Equal(3, vm.Mods.Count);
        Assert.Contains("Publishing 3 mods", vm.Summary);
        Assert.True(Find(window, "PublishButton").IsEnabled);
    }

    [AvaloniaFact]
    public void A_long_list_scrolls_rather_than_pushing_publish_out_of_reach()
    {
        var (window, _) = Show(Plan(mods: 60));

        // By name: the slug TextBox carries a ScrollViewer of its own.
        var list = window.GetVisualDescendants().OfType<ScrollViewer>()
            .Single(s => s.Name == "ModList");

        Assert.True(list.Extent.Height > list.Viewport.Height);

        // The reason this is a window rather than a panel in a tab.
        var publish = Find(window, "PublishButton");
        Assert.True(publish.Bounds.Bottom <= window.ClientSize.Height);
    }

    [AvaloniaFact]
    public void A_stale_lock_refuses_rather_than_warns()
    {
        var (window, vm) = Show(Plan(lockProblem: "Sync the pack first."));

        // Including the lock is the whole reproducibility claim, so a partial one is not
        // something to publish with a warning attached.
        Assert.False(vm.CanPublish);
        Assert.True(vm.CannotPublish);
        Assert.False(Find(window, "PublishButton").IsEnabled);
    }

    [AvaloniaFact]
    public void A_server_address_is_surfaced_and_kept_for_an_unlisted_pack()
    {
        var (_, vm) = Show(Plan(connect: "anego.example.com:42420"));

        Assert.True(vm.HasConnect);
        Assert.Contains("anego.example.com:42420", vm.ConnectWarning);

        // Unlisted is the default, and a pack handed to your own players is exactly when
        // the address is wanted.
        Assert.False(vm.IsPublic);
        Assert.False(vm.StripConnect);
    }

    [AvaloniaFact]
    public void Going_public_strips_the_server_address()
    {
        var (_, vm) = Show(Plan(connect: "anego.example.com:42420"));

        vm.IsPublic = true;

        // Moving a control from under the user, allowed only because it moves toward not
        // disclosing an address — the alternative is publishing your server to the browse
        // list by flipping one radio button.
        Assert.True(vm.StripConnect);
    }

    [AvaloniaFact]
    public void A_republish_keeps_the_choice_that_was_made_last_time()
    {
        var link = new PackLink
        {
            Role = PackRole.Author,
            Url = "https://cairns.gg/dizzyd/anego",
            Published = new PublishRecord { Visibility = "public", Connect = "included" },
        };

        var (_, vm) = Show(Plan(connect: "anego.example.com:42420"), link);

        // Including it on a public pack is unusual, which is exactly why it was a decision
        // and not ours to quietly undo.
        Assert.True(vm.IsPublic);
        Assert.False(vm.StripConnect);
        Assert.Equal("Publish changes", vm.PublishLabel);
        Assert.Equal("anego", vm.Slug);
    }

    [AvaloniaFact]
    public void Mods_that_are_not_on_ModDB_sort_to_the_top_and_are_named()
    {
        var (_, vm) = Show(Plan(mods: 3, missing: true));

        // Worst first: the reason to say no should not need scrolling to.
        Assert.Equal("homegrown", vm.Mods[0].ModId);
        Assert.True(vm.Mods[0].Missing);
        Assert.True(vm.AnythingUnresolvable);
        Assert.Contains("homegrown", vm.UnresolvableWarning);
    }

    [AvaloniaFact]
    public void The_url_preview_follows_the_slug()
    {
        var (_, vm) = Show(Plan());

        Assert.Equal("cairns.gg/dizzyd/anego", vm.UrlPreview);

        vm.Slug = "anego-hardcore";
        Assert.Equal("cairns.gg/dizzyd/anego-hardcore", vm.UrlPreview);
    }
}
