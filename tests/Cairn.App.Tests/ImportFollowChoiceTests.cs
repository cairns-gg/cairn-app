using System.Linq;
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
/// The choice between following somebody's pack and starting your own from it.
///
/// Rendered rather than poked at, because the whole security value of the choice is that
/// the address is on screen when it is made: a view model that holds the right string and
/// a window that never shows it would pass an assertion and mislead a person. So these
/// check the text is in the visual tree, and that the button cannot be pressed until a
/// file's question has actually been answered.
/// </summary>
[Collection(AvaloniaTests.Collection)]
public class ImportFollowChoiceTests
{
    private static PackBundle Published() => PackBundle.Parse("""
        {
          "formatVersion": 1,
          "pack": { "id": "theirs", "gameVersion": "1.22.5",
                    "mods": [ { "modid": "glassview" } ] },
          "publishedBy": "someone-else",
          "canonicalUrl": "https://cairns.gg/someone-else/theirs",
          "revision": 4
        }
        """);

    private static (ImportWindow Window, ImportViewModel Vm) Show(bool fetched)
    {
        var vm = new ImportViewModel(
            Published(),
            fetched ? "https://cairns.gg/someone-else/theirs.json" : "theirs.json",
            _ => false,
            fetched);

        var window = new ImportWindow { DataContext = vm };
        window.Show();

        return (window, vm);
    }

    private static string AllText(Visual root) =>
        string.Join(" ", root.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text ?? ""));

    [AvaloniaFact]
    public void A_file_preselects_neither_and_will_not_add_until_asked()
    {
        var (window, vm) = Show(fetched: false);

        Assert.Null(vm.Follow);
        Assert.False(vm.FollowChosen);
        Assert.False(vm.ForkChosen);

        var add = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "AddButton");
        Assert.False(add.IsEnabled);

        // Answering either way releases it.
        vm.ForkChosen = true;
        Assert.Equal(ImportIntent.Fork, vm.Intent);
        Assert.True(vm.CanAdd);
    }

    [AvaloniaFact]
    public void A_file_says_the_address_is_only_the_files_own_word()
    {
        var (window, _) = Show(fetched: false);
        var text = AllText(window);

        // The address itself, and the fact that nothing has checked it. Following takes
        // the file's word, and that is the part somebody has to be able to weigh.
        Assert.Contains("https://cairns.gg/someone-else/theirs", text);
        Assert.Contains("Cairn has not checked that", text);
    }

    [AvaloniaFact]
    public void A_fetched_pack_starts_on_follow_and_says_where_from()
    {
        var (window, vm) = Show(fetched: true);

        Assert.True(vm.FollowChosen);
        Assert.Equal(ImportIntent.Follow, vm.Intent);
        Assert.True(vm.CanAdd);

        // The ".json" the machine fetched is not the address a person reads.
        Assert.Contains("Keep in step with https://cairns.gg/someone-else/theirs,",
            AllText(window));
    }

    [AvaloniaFact]
    public void Choosing_to_make_it_yours_is_offered_on_a_fetched_pack_too()
    {
        var (_, vm) = Show(fetched: true);

        vm.ForkChosen = true;

        Assert.False(vm.FollowChosen);
        Assert.Equal(ImportIntent.Fork, vm.Intent);
    }

    [AvaloniaFact]
    public void A_pack_nobody_published_is_not_asked_about()
    {
        var vm = new ImportViewModel(
            PackBundle.Parse("""
                {"formatVersion":1,
                 "pack":{"id":"handed-over","gameVersion":"1.22.5","mods":[{"modid":"glassview"}]}}
                """),
            "handed-over.json", _ => false, fetched: false);

        var window = new ImportWindow { DataContext = vm };
        window.Show();

        // No owner, so no question — and nothing blocking the button over one.
        Assert.False(vm.CanChooseFollow);
        Assert.True(vm.CanAdd);
        Assert.DoesNotContain("Follow it", AllText(window));
    }

    /// <summary>
    /// Built the way MainViewModel builds it for a file: the "source" it is handed is the
    /// document's own canonicalUrl, because there is nothing else to hand it. The harness
    /// above passes a filename instead, which is fine for the follow choice and wrong for
    /// this — the whole question here is what happens when the claimed address is shown.
    /// </summary>
    private static (ImportWindow Window, ImportViewModel Vm) ShowFileAsMainWindowWould()
    {
        var bundle = Published();
        var vm = new ImportViewModel(bundle, bundle.CanonicalUrl!, _ => false, fetched: false);
        var window = new ImportWindow { DataContext = vm };
        window.Show();

        return (window, vm);
    }

    /// <summary>
    /// The line somebody reads to decide whether a link they were sent is worth trusting.
    /// For a file, both halves of it — who published this and where it came from — are
    /// strings out of a document anybody can write, and nothing checked either.
    /// </summary>
    [AvaloniaFact]
    public void A_files_attribution_is_shown_as_the_files_claim()
    {
        var (window, vm) = ShowFileAsMainWindowWould();

        Assert.Equal("the file says: by someone-else · from cairns.gg", vm.Provenance);

        var text = AllText(window);
        Assert.Contains("the file says:", text);
        Assert.Contains("someone-else", text);
    }

    /// <summary>
    /// And a fetched one is not hedged, because there the host is a fact about an exchange
    /// that happened. Qualifying both equally would make the qualification meaningless.
    /// </summary>
    [AvaloniaFact]
    public void A_fetched_packs_attribution_is_not_hedged()
    {
        var (window, vm) = Show(fetched: true);

        Assert.Equal("by someone-else · from cairns.gg", vm.Provenance);
        Assert.DoesNotContain("the file says", AllText(window));
    }
}
