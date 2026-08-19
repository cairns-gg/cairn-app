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

    /// <summary>A pack already published, so there is a revision to compare against.</summary>
    private static PackLink Published() => new()
    {
        Role = PackRole.Author,
        Url = "https://cairns.gg/dizzyd/anego",
        Revision = 4,
        Published = new PublishRecord
        {
            Visibility = "public", Connect = "stripped", Fingerprint = "whatever",
        },
    };

    private static (ShareWindow Window, ShareViewModel Vm) Show(
        PublishPlan plan, PackLink? link = null, string? username = "dizzyd",
        Func<bool, string>? documentFor = null,
        PublishDelta? delta = null, bool deltaKnown = false)
    {
        var vm = ShareViewModel.From(
            plan, "Anego Server", username, link, documentFor, delta, deltaKnown);
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
    public void A_first_share_is_listed_and_its_server_address_is_surfaced_and_stripped()
    {
        var (_, vm) = Show(Plan(connect: "anego.example.com:42420"));

        Assert.True(vm.HasConnect);
        Assert.Contains("anego.example.com:42420", vm.ConnectWarning);

        // Listed, because the default used to be unlisted and what that produced was people
        // who had shared a pack and did not know nobody could find it. The address goes with
        // that choice rather than against it: public and stripped is the pair.
        Assert.True(vm.IsPublic);
        Assert.True(vm.StripConnect);
    }

    [AvaloniaFact]
    public void Going_unlisted_does_not_put_the_server_address_back_on_its_own()
    {
        var (_, vm) = Show(Plan(connect: "anego.example.com:42420"));

        vm.IsPublic = false;

        // Unlisted is the pack you hand to your own players, which is exactly when the
        // address is wanted — and it still has to be asked for. Nothing here ever moves
        // toward disclosing it: an unlisted pack's link gets pasted into a chat like any
        // other, so "I made it more private" must not quietly add a server address to what
        // gets published. Include is one tick away, beside the address it names.
        Assert.True(vm.StripConnect);
    }

    [AvaloniaFact]
    public void Going_public_strips_the_server_address()
    {
        var (_, vm) = Show(Plan(connect: "anego.example.com:42420"));

        // From an unlisted pack that had said to include it, which is the only way round
        // this can happen now that a first share is public and stripped.
        vm.IsPublic = false;
        vm.StripConnect = false;

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

    /// <summary>A pack already published unlisted, with its server address stripped.</summary>
    private static (PackLink Link, Func<bool, string> DocumentFor) AlreadyPublished(
        string document = """{"pack":"as it went"}""")
    {
        var link = new PackLink
        {
            Role = PackRole.Author,
            Url = "https://cairns.gg/dizzyd/anego",
            Revision = 3,
            Published = new PublishRecord
            {
                Fingerprint = PackLink.Fingerprint(document),
                Visibility = "unlisted",
                Connect = "stripped",
            },
        };

        return (link, _ => document);
    }

    [AvaloniaFact]
    public void An_unchanged_pack_cannot_be_published_again()
    {
        var (link, documentFor) = AlreadyPublished();
        var (window, vm) = Show(Plan(), link, documentFor: documentFor);

        // A revision differing from the last in nothing but its number tells every
        // follower there is an update and then has none for them.
        Assert.True(vm.NothingToPublish);
        Assert.False(vm.CanPublish);
        Assert.False(Find(window, "PublishButton").IsEnabled);
        Assert.Contains("revision 3", vm.UnchangedNote);
    }

    [AvaloniaFact]
    public void Going_public_is_a_change_even_when_the_document_is_identical()
    {
        var (link, documentFor) = AlreadyPublished();
        var (window, vm) = Show(Plan(), link, documentFor: documentFor);

        Assert.False(vm.CanPublish);

        // Which is why the window still opens on an unchanged pack: visibility is the
        // reason to come back to one, and blocking on the document alone would strand
        // somebody whose only remaining edit is this.
        vm.IsPublic = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.False(vm.NothingToPublish);
        Assert.True(vm.CanPublish);
        Assert.True(Find(window, "PublishButton").IsEnabled);
    }

    [AvaloniaFact]
    public void The_address_is_fixed_once_published()
    {
        var (link, documentFor) = AlreadyPublished();
        var (window, vm) = Show(Plan(), link, documentFor: documentFor);

        // On this server the URL *is* the pack, so publishing under a different slug does
        // not move it — it creates a second pack and leaves the first live under the same
        // name. Two identical-looking packs and no way to tell which is which.
        Assert.True(vm.SlugFixed);
        Assert.Contains("fixed once published", vm.SlugNote);

        var box = window.GetVisualDescendants().OfType<TextBox>()
            .First(b => (b.Text ?? "") == "anego");

        Assert.True(box.IsReadOnly);
    }

    [AvaloniaFact]
    public void A_pack_that_has_never_been_published_can_choose_its_address()
    {
        var (window, vm) = Show(Plan());

        Assert.False(vm.SlugFixed);
        Assert.Equal("", vm.SlugNote);

        Assert.False(window.GetVisualDescendants().OfType<TextBox>()
            .First(b => (b.Text ?? "") == "anego").IsReadOnly);
    }

    [AvaloniaFact]
    public void A_pack_that_has_actually_changed_publishes()
    {
        var (link, _) = AlreadyPublished();
        var (_, vm) = Show(Plan(), link, documentFor: _ => """{"pack":"edited since"}""");

        Assert.False(vm.NothingToPublish);
        Assert.True(vm.CanPublish);
        Assert.Equal("", vm.UnchangedNote);
    }

    [AvaloniaFact]
    public void Without_a_document_to_compare_the_check_allows_rather_than_blocks()
    {
        // Erring the other way would make a comparison that could not be made into a
        // refusal to publish at all.
        var (link, _) = AlreadyPublished();
        var (_, vm) = Show(Plan(), link);

        Assert.False(vm.NothingToPublish);
        Assert.True(vm.CanPublish);
    }

    /// <summary>
    /// What a pack carries besides its mods is on the last screen before it is sent.
    ///
    /// The mod list is right there; the settings and hotkeys are not visible anywhere else in
    /// the flow, and a pack's mod settings are exactly the thing an author is least sure has
    /// travelled — which is how a stale one went out unnoticed in the first place.
    /// </summary>
    /// <summary>
    /// On a first publish, where what the pack contains is the whole answer — after that the
    /// delta line says what moved, and the two together would repeat each other.
    /// </summary>
    [AvaloniaFact]
    public void The_window_says_what_travels_besides_the_mods()
    {
        var plan = Plan() with { ModConfigValues = 3, Keybinds = 2 };

        var (window, _) = Show(plan);

        var text = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Name == "CarriesText");

        Assert.True(text.IsEffectivelyVisible);
        Assert.Contains("3 mod settings", text.Text);
        Assert.Contains("2 hotkeys", text.Text);
    }

    [AvaloniaFact]
    public void And_says_nothing_for_a_pack_that_is_only_mods()
    {
        var (window, _) = Show(Plan());

        var text = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Name == "CarriesText");

        Assert.False(text.IsEffectivelyVisible);
    }

    /// <summary>
    /// A pack with a revision already at its address says what publishing would change about
    /// it, which is the question after the first publish — a pack its author has played for a
    /// month has moved in ways they will not remember.
    /// </summary>
    [AvaloniaFact]
    public void The_window_says_what_this_publish_would_change()
    {
        var delta = new PublishDelta(
            ModsAdded: 1, ModsRemoved: 0, ModsMoved: 5,
            SettingsChanged: 3, HotkeysChanged: 0,
            ConnectChanged: false, GameVersionFrom: null, GameVersionTo: "1.22.5");

        var (window, _) = Show(Plan(), Published(), delta: delta, deltaKnown: true);

        var text = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Name == "DeltaText");

        Assert.True(text.IsEffectivelyVisible);
        Assert.Contains("Since revision 4", text.Text);
        Assert.Contains("3 mod settings changed", text.Text);
    }

    /// <summary>
    /// A site that could not be asked says so. "Nothing has changed" is the one thing it must
    /// not be mistaken for on the screen where somebody decides whether to press Publish.
    /// </summary>
    [AvaloniaFact]
    public void A_site_that_could_not_be_reached_says_that_rather_than_nothing_changed()
    {
        var (window, _) = Show(Plan(), Published(), delta: null, deltaKnown: false);

        var text = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Name == "DeltaText");

        Assert.Contains("Could not reach", text.Text);
    }

    /// <summary>And a first publish has nothing to compare against, so it says nothing.</summary>
    [AvaloniaFact]
    public void A_first_publish_says_nothing_about_a_revision_that_does_not_exist()
    {
        var (window, _) = Show(Plan());

        var text = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Name == "DeltaText");

        Assert.False(text.IsEffectivelyVisible);
    }

    /// <summary>
    /// The summary never claims nothing has changed, because it is in no position to: the
    /// document decides that, it knows about the publish options too, and the unchanged note
    /// says it from there. A difference this line cannot name — a lockfile re-resolved to the
    /// same versions, say — is still a difference, and calling it nothing put that claim on
    /// the same screen as an enabled Publish button.
    /// </summary>
    [AvaloniaFact]
    public void A_change_the_summary_cannot_name_is_still_reported_as_a_change()
    {
        // The document differs from what was published, and none of it is anything the delta
        // itemises.
        var (window, vm) = Show(
            Plan(), Published(), documentFor: _ => "something else entirely",
            delta: new PublishDelta(0, 0, 0, 0, 0, false, null, "1.22.5"), deltaKnown: true);

        Assert.False(vm.NothingToPublish);

        var text = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Name == "DeltaText");

        Assert.Contains("Something has changed", text.Text);
    }

    /// <summary>
    /// And says nothing at all when the pack really is unchanged, leaving that to the note
    /// that already says so and disables the button.
    /// </summary>
    [AvaloniaFact]
    public void A_pack_that_really_has_not_changed_leaves_it_to_the_unchanged_note()
    {
        const string document = "the published document";

        var link = Published();
        link.Published!.Fingerprint = PackLink.Fingerprint(document);

        var (window, vm) = Show(
            Plan(), link, documentFor: _ => document,
            delta: new PublishDelta(0, 0, 0, 0, 0, false, null, "1.22.5"), deltaKnown: true);

        Assert.True(vm.NothingToPublish);

        Assert.False(window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Name == "DeltaText").IsEffectivelyVisible);
    }
}
