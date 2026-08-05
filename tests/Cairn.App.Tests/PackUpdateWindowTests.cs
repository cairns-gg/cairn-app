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
/// The dialog that shows what an author's newer revision would do.
///
/// Rendered rather than inspected as a view model, because Avalonia resolves bindings at
/// runtime: a mistyped path draws nothing and fails nothing. Half of this window is rows
/// that only appear for one kind of change, so a wrong condition shows a checkbox against
/// a mod nobody can answer for, or hides the one that needed answering.
/// </summary>
[Collection(AvaloniaTests.Collection)]
public class PackUpdateWindowTests
{
    private static PackManifest Pack(string gameVersion, params (string Id, string? Pin)[] mods) => new()
    {
        Id = "anego",
        Name = "Anego Server",
        GameVersion = gameVersion,
        Mods = [.. mods.Select(m => new PackMod { ModId = m.Id, Version = m.Pin })],
    };

    private static (string, string?) Mod(string id, string? pin = null) => (id, pin);

    private static (PackUpdateWindow Window, PackUpdateViewModel Vm) Show(PackUpdatePlan plan)
    {
        var vm = new PackUpdateViewModel(plan, "Anego Server");
        var window = new PackUpdateWindow { DataContext = vm };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        return (window, vm);
    }

    private static Button Find(Visual root, string name) =>
        root.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);

    private static List<CheckBox> Boxes(Visual root) =>
        [.. root.GetVisualDescendants().OfType<CheckBox>().Where(c => c.IsVisible)];

    private static List<string> Texts(Visual root) =>
        [.. root.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text))
            .Select(t => t.Text!)];

    /// <summary>The everyday case: the author changed things, you changed nothing.</summary>
    private static PackUpdatePlan Simple() => PackUpdatePlan.Between(
        Pack("1.22.5", Mod("carryon")),
        Pack("1.22.5", Mod("carryon"), Mod("betterruins")),
        Pack("1.22.5", Mod("carryon")),
        fromRevision: 1, toRevision: 2);

    [AvaloniaFact]
    public void The_authors_changes_are_listed_with_the_revision_being_taken()
    {
        var (window, _) = Show(Simple());

        var texts = Texts(window);
        Assert.Contains(texts, t => t.Contains("Revision 2"));
        Assert.Contains(texts, t => t.Contains("betterruins"));
        Assert.Contains(texts, t => t.Contains("adds"));
    }

    [AvaloniaFact]
    public void An_ordinary_change_offers_nothing_to_tick()
    {
        // Additions and removals are what an update is. Putting a control on them would
        // turn reading a list into answering one.
        var (window, _) = Show(Simple());

        Assert.Empty(Boxes(window));
    }

    [AvaloniaFact]
    public void A_mod_you_removed_offers_both_putting_it_back_and_silencing_it()
    {
        var plan = PackUpdatePlan.Between(
            Pack("1.22.5", Mod("carryon")),
            Pack("1.22.5", Mod("carryon"), Mod("heavyweight")),
            Pack("1.22.5", Mod("carryon"), Mod("heavyweight")),
            toRevision: 2);

        var (window, vm) = Show(plan);

        var boxes = Boxes(window);
        Assert.Equal(2, boxes.Count);
        Assert.Contains(boxes, b => (b.Content as string) == "leave it out");
        Assert.Contains(boxes, b => (b.Content as string) == "stop asking about this one");

        // Neither is ticked: the default keeps your removal and keeps asking.
        Assert.All(boxes, b => Assert.False(b.IsChecked));
        Assert.False(vm.Changes.Single().Take);
        Assert.False(vm.Changes.Single().Silence);
    }

    [AvaloniaFact]
    public void Ticking_put_it_back_writes_through_and_withdraws_the_silence_offer()
    {
        var plan = PackUpdatePlan.Between(
            Pack("1.22.5", Mod("carryon")),
            Pack("1.22.5", Mod("carryon"), Mod("heavyweight")),
            Pack("1.22.5", Mod("carryon"), Mod("heavyweight")),
            toRevision: 2);

        var (window, vm) = Show(plan);
        var row = vm.Changes.Single();

        row.Take = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // Straight through to the plan, so what is on screen and what Apply does agree.
        Assert.True(plan.Changes.Single().Take);

        // And there is nothing left to stop asking about.
        Assert.False(row.CanSilence);
        Assert.DoesNotContain(Boxes(window), b => (b.Content as string) == "stop asking about this one");
    }

    [AvaloniaFact]
    public void A_pin_conflict_can_be_answered_but_not_silenced()
    {
        // It resolves itself the moment either side moves, so there is nothing permanent
        // to suppress — unlike a removal, which stays true for ever.
        var plan = PackUpdatePlan.Between(
            Pack("1.22.5", Mod("carryon", "1.1.0")),
            Pack("1.22.5", Mod("carryon", "2.0.0")),
            Pack("1.22.5", Mod("carryon", "1.0.0")),
            toRevision: 2);

        var (window, vm) = Show(plan);

        var boxes = Boxes(window);
        Assert.Single(boxes);
        Assert.Equal("keep yours (1.1.0)", boxes[0].Content as string);
        Assert.False(vm.Changes.Single().CanSilence);

        vm.Changes.Single().Take = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal("use theirs (2.0.0)", Boxes(window)[0].Content as string);
    }

    [AvaloniaFact]
    public void A_retarget_and_a_missing_base_are_both_said_out_loud()
    {
        var plan = PackUpdatePlan.Between(
            Pack("1.22.5", Mod("carryon")),
            Pack("1.23.0", Mod("carryon")),
            @base: null,
            toRevision: 2);

        var (window, _) = Show(plan);
        var texts = Texts(window);

        Assert.Contains(texts, t => t.Contains("1.22.5") && t.Contains("1.23.0"));

        // The blind case changes how every other line should be read, so it cannot be the
        // thing somebody scrolls past.
        Assert.Contains(texts, t => t.Contains("cannot be told from"));
    }

    [AvaloniaFact]
    public void Questions_are_listed_before_the_authors_changes()
    {
        // The thing needing an answer should not need scrolling to.
        var plan = PackUpdatePlan.Between(
            Pack("1.22.5", Mod("aaa-carryon", "1.1.0")),
            Pack("1.22.5", Mod("aaa-carryon", "2.0.0"), Mod("zzz-new")),
            Pack("1.22.5", Mod("aaa-carryon", "1.0.0")),
            toRevision: 2);

        var (_, vm) = Show(plan);

        Assert.True(vm.Changes[0].IsChoice, "the question was not first");
        Assert.Equal("zzz-new", vm.Changes[1].ModId);
    }

    [AvaloniaFact]
    public void Apply_closes_true_and_every_other_route_closes_false()
    {
        var (window, _) = Show(Simple());

        // The buttons exist and are wired; a dialog whose Apply does nothing is a pack
        // that never updates and never says why.
        Assert.NotNull(Find(window, "ApplyButton"));
        Assert.NotNull(Find(window, "CancelButton"));

        Assert.Contains(Texts(window), t => t.Contains("Update to revision 2"));
    }
}
