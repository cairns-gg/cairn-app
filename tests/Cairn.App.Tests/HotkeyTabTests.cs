using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Cairn.App.ViewModels;
using Cairn.App.Views;
using Cairn.Core.Hotkeys;
using KeyBinding = Cairn.Core.Hotkeys.KeyBinding;
using Cairn.Core.Packs;
using Cairn.Core.Cairns;
using Xunit;

namespace Cairn.App.Tests;

/// <summary>
/// The Hotkeys tab, driven through the real window.
///
/// The pack fixture carries actual mod zips with actual assemblies in them, because the
/// tab's whole job is to read hotkeys out of files nobody has launched. A stubbed catalogue
/// would test the list rendering and nothing that makes the feature work.
/// </summary>
[Collection(AvaloniaTests.Collection)]
public class HotkeyTabTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "cairn-hotkeytab-" + Guid.NewGuid().ToString("n")[..8]);

    public HotkeyTabTests()
    {
        var mods = Path.Combine(_home, "packs", "anego", "Mods");
        Directory.CreateDirectory(mods);

        File.WriteAllText(Path.Combine(_home, "packs", "anego", "pack.json"), JsonSerializer.Serialize(
            new { id = "anego", name = "Anego", gameVersion = "1.22.5", mods = Array.Empty<object>() },
            new JsonSerializerOptions { WriteIndented = true }));

        // Two mods that both want P — the collision this feature exists for — and one that
        // wants something else.
        WriteModZip(Path.Combine(mods, "scribe.zip"),
            [("scribepinhud", "Pin the HUD", 98, HotkeyKind.GUIOrOtherControls)]);

        WriteModZip(Path.Combine(mods, "prospector.zip"),
            [("prospectorsinstinct-config", "Prospector config", 98, HotkeyKind.GUIOrOtherControls),
             ("prospector-scan", "Scan", 90, HotkeyKind.CharacterControls)]);

        // One the player's hands know. Movement by type, exactly as the game classifies
        // Sit down — the row that made a hard lock the wrong rule.
        WriteModZip(Path.Combine(mods, "clamber.zip"),
            [("clamber-up", "Clamber up", 89, HotkeyKind.MovementControls)]);

        // Two mods holding Ctrl, the way CarryOn's swap-to-back does. Shared on purpose.
        WriteModZip(Path.Combine(mods, "carry.zip"),
            [("carry-swapback", "Swap to back modifier", 3, HotkeyKind.CharacterControls)]);
        WriteModZip(Path.Combine(mods, "haul.zip"),
            [("haul-modifier", "Haul modifier", 3, HotkeyKind.CharacterControls)]);

        Environment.SetEnvironmentVariable("CAIRN_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CAIRN_HOME", null);
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    /// <summary>A zip holding one assembly that registers the given hotkeys.</summary>
    private static void WriteModZip(
        string path, (string Code, string Name, int Key, HotkeyKind Kind)[] hotkeys)
    {
        var assembly = new PersistedAssemblyBuilder(new AssemblyName("FakeMod"), typeof(object).Assembly);
        var module = assembly.DefineDynamicModule("FakeMod");
        var type = module.DefineType("FakeMod.System", TypeAttributes.Public);

        var register = type.DefineMethod(
            "RegisterHotKey", MethodAttributes.Public | MethodAttributes.Static, typeof(void),
            [typeof(string), typeof(string), typeof(int), typeof(int),
             typeof(bool), typeof(bool), typeof(bool)]);
        register.GetILGenerator().Emit(OpCodes.Ret);

        var caller = type.DefineMethod(
            "Start", MethodAttributes.Public | MethodAttributes.Static, typeof(void), []);
        var il = caller.GetILGenerator();

        foreach (var (code, name, key, kind) in hotkeys)
        {
            il.Emit(OpCodes.Ldstr, code);
            il.Emit(OpCodes.Ldstr, name);
            il.Emit(OpCodes.Ldc_I4, key);
            il.Emit(OpCodes.Ldc_I4, (int)kind);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Call, register);
        }

        il.Emit(OpCodes.Ret);
        type.CreateType();

        using var image = new MemoryStream();
        assembly.Save(image);

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var entry = zip.CreateEntry("FakeMod.dll").Open();
        image.Position = 0;
        image.CopyTo(entry);
    }

    private static (MainWindow Window, MainViewModel Vm) Show()
    {
        var vm = new MainViewModel(new OfflineHandler());
        var window = new MainWindow { DataContext = vm };
        window.Show();
        vm.SelectedPack = vm.Packs.First();
        return (window, vm);
    }

    /// <summary>Opens the tab and waits for the scan, which runs off the UI thread.</summary>
    private static async Task<PackDetailViewModel> OpenTab(MainViewModel vm)
    {
        var detail = vm.Detail!;
        detail.SelectedTab = PackDetailViewModel.HotkeysTab;

        await Settle(() => detail.Hotkeys.Count > 0);
        return detail;
    }

    /// <summary>
    /// Pumps the dispatcher until something is true, or gives up.
    ///
    /// The condition has to be the thing the test is about. Waiting for "any rows at all"
    /// is no wait when the rows already on screen are the stale ones a re-scan is about to
    /// replace — the assertion then races the scan and passes on a quiet machine.
    /// </summary>
    private static async Task Settle(Func<bool> until)
    {
        for (var i = 0; i < 200 && !until(); i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// The tab index is a constant in the view model and an ordinal in the markup, and
    /// nothing but this connects them.
    /// </summary>
    [AvaloniaFact]
    public void The_constant_names_the_tab_it_says_it_does()
    {
        var (window, _) = Show();

        var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
        var items = tabs.Items.OfType<TabItem>().ToList();

        Assert.Equal("Hotkeys", items[PackDetailViewModel.HotkeysTab].Header as string);
    }

    [AvaloniaFact]
    public async Task Opening_the_tab_reads_the_hotkeys_out_of_the_mod_files()
    {
        var (_, vm) = Show();
        var detail = await OpenTab(vm);

        // Nothing has been launched and no mod has been run: this comes out of the zips.
        Assert.Equal(
            ["carry-swapback", "clamber-up", "haul-modifier", "prospector-scan",
             "prospectorsinstinct-config", "scribepinhud"],
            detail.Hotkeys.Select(h => h.Code).Order());

        var pin = detail.Hotkeys.Single(h => h.Code == "scribepinhud");
        Assert.Equal("Pin the HUD", pin.Display);
        Assert.Equal("P", pin.DefaultText);
    }

    [AvaloniaFact]
    public async Task Two_mods_on_the_same_key_are_reported_as_a_clash()
    {
        var (window, vm) = Show();
        var detail = await OpenTab(vm);

        Assert.Equal(2, detail.HotkeyClashCount);
        Assert.True(detail.HasHotkeyClashes);

        var pin = detail.Hotkeys.Single(h => h.Code == "scribepinhud");
        Assert.True(pin.Clashes);
        Assert.Equal("Prospector config", pin.ClashesWith);

        // On the filter rather than in a banner above the list that would show them: one
        // place to read the count, and it is the control you would reach for next.
        Assert.Equal("Only conflicts (2)", detail.OnlyClashesLabel);
        Assert.Contains(
            window.GetVisualDescendants().OfType<CheckBox>()
                .Where(c => c.IsEffectivelyVisible).Select(c => c.Content as string),
            t => t == "Only conflicts (2)");
    }

    [AvaloniaFact]
    public async Task Rebinding_one_of_them_clears_the_clash()
    {
        var (_, vm) = Show();
        var detail = await OpenTab(vm);

        var pin = detail.Hotkeys.Single(h => h.Code == "scribepinhud");
        pin.CaptureCommand.Execute(null);
        Assert.True(pin.Capturing);

        // The press arrives at the window, which is the only thing that sees one.
        Assert.True(detail.CaptureHotkey(KeyBindingCode("K"), ctrl: true, alt: false, shift: false));

        Assert.False(pin.Capturing);
        Assert.Equal("Ctrl-K", pin.EffectiveText);
        Assert.True(pin.IsOverridden);

        // The list is recomputed from what is bound now, not from what the mods ship —
        // otherwise it would go on reporting the clash somebody has just fixed.
        Assert.Equal(0, detail.HotkeyClashCount);
    }

    [AvaloniaFact]
    public async Task A_real_keypress_reaches_the_row_that_is_waiting()
    {
        var (window, vm) = Show();
        var detail = await OpenTab(vm);

        var pin = detail.Hotkeys.Single(h => h.Code == "scribepinhud");
        pin.CaptureCommand.Execute(null);

        window.KeyPressQwerty(PhysicalKey.J, RawInputModifiers.Shift);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal("Shift-J", pin.EffectiveText);
    }

    [AvaloniaFact]
    public async Task Escape_leaves_the_binding_alone()
    {
        var (_, vm) = Show();
        var detail = await OpenTab(vm);

        var pin = detail.Hotkeys.Single(h => h.Code == "scribepinhud");
        pin.CaptureCommand.Execute(null);

        detail.CaptureHotkey(KeyBindingCode("Escape"), false, false, false);

        // The one key somebody presses meaning "not this".
        Assert.False(pin.Capturing);
        Assert.False(pin.IsOverridden);
        Assert.Equal("P", pin.EffectiveText);
    }

    [AvaloniaFact]
    public async Task Reset_puts_the_mods_own_default_back()
    {
        var (_, vm) = Show();
        var detail = await OpenTab(vm);

        var pin = detail.Hotkeys.Single(h => h.Code == "scribepinhud");
        pin.Binding = KeyBinding.Parse("Ctrl-K");
        Assert.True(pin.IsOverridden);

        pin.ClearCommand.Execute(null);

        // Cleared rather than set to the default value: the pack then says nothing about
        // this hotkey, so a mod that moves its own default later is followed.
        Assert.Null(pin.Binding);
        Assert.Equal("P", pin.EffectiveText);
    }

    [AvaloniaFact]
    public async Task Rebinding_writes_the_pack_for_everyone_who_imports_it()
    {
        var (_, vm) = Show();
        var detail = await OpenTab(vm);

        detail.Hotkeys.Single(h => h.Code == "scribepinhud").Binding = KeyBinding.Parse("Ctrl-K");


        // In the manifest, which is the document that travels — the whole value is that it
        // reaches people who did not do the work.
        var manifest = PackManifest.Load(Path.Combine(_home, "packs", "anego", "pack.json"));
        Assert.Equal("Ctrl-K", manifest.Keybinds!["scribepinhud"]);

        // Only what the pack actually changed. Every other hotkey keeps whatever its mod
        // ships, including when the mod changes it.
        Assert.Single(manifest.Keybinds);
    }

    [AvaloniaFact]
    public async Task A_pack_that_binds_nothing_writes_no_keybinds_at_all()
    {
        var (_, vm) = Show();
        var detail = await OpenTab(vm);

        detail.Hotkeys.First().Binding = KeyBinding.Parse("Ctrl-K");
        detail.Hotkeys.First().Binding = null;

        // Back to a file that looks exactly as it did before the feature existed.
        Assert.DoesNotContain("keybinds",
            File.ReadAllText(Path.Combine(_home, "packs", "anego", "pack.json")));
    }

    [AvaloniaFact]
    public async Task What_the_pack_already_declares_is_shown_as_the_binding()
    {
        var path = Path.Combine(_home, "packs", "anego", "pack.json");
        var manifest = PackManifest.Load(path);
        manifest.Keybinds = new Dictionary<string, string> { ["scribepinhud"] = "Alt-K" };
        manifest.Save(path);

        var (_, vm) = Show();
        var detail = await OpenTab(vm);

        var pin = detail.Hotkeys.Single(h => h.Code == "scribepinhud");
        Assert.Equal("Alt-K", pin.EffectiveText);
        Assert.Equal("P", pin.DefaultText);        // the mod's own, kept alongside

        // Loading a pack is not editing it: the file on disk is untouched by opening the
        // tab, which matters now that every edit writes.
        Assert.Equal(
            "Alt-K",
            PackManifest.Load(Path.Combine(_home, "packs", "anego", "pack.json"))
                .Keybinds!["scribepinhud"]);
    }

    [AvaloniaFact]
    public async Task Searching_narrows_the_list_by_name_id_mod_or_key()
    {
        var (_, vm) = Show();
        var detail = await OpenTab(vm);

        Assert.Equal(6, detail.Hotkeys.Count);

        // By label — the resolved one, not the id.
        detail.HotkeySearch = "pin the";
        Assert.Equal(["scribepinhud"], detail.Hotkeys.Select(h => h.Code));

        // By id, for the rows whose mods never gave a readable name.
        detail.HotkeySearch = "prospector";
        Assert.Equal(2, detail.Hotkeys.Count);

        // By the mod it came from.
        detail.HotkeySearch = "scribe.zip";
        Assert.Equal(["scribepinhud"], detail.Hotkeys.Select(h => h.Code));

        // And by the key it is on. A term that names a key asks about that key rather than
        // matching it as a substring: "P" appears in half the mod ids in a pack, which is
        // no answer at all to "what else is on P?".
        detail.HotkeySearch = "P";
        Assert.Equal(
            ["prospectorsinstinct-config", "scribepinhud"],
            detail.Hotkeys.Select(h => h.Code).Order());

        // Modifiers count, so P and Ctrl-P are different questions.
        detail.Hotkeys.First().Binding = KeyBinding.Parse("Ctrl-P");
        detail.HotkeySearch = "Ctrl-P";
        Assert.Single(detail.Hotkeys);

        detail.HotkeySearch = "";
        Assert.Equal(6, detail.Hotkeys.Count);
    }

    [AvaloniaFact]
    public async Task Only_conflicts_hides_everything_that_is_fine()
    {
        var (window, vm) = Show();
        var detail = await OpenTab(vm);

        detail.OnlyClashes = true;

        // The two on P. The third hotkey is on a key of its own and is not the problem.
        Assert.Equal(
            ["prospectorsinstinct-config", "scribepinhud"],
            detail.Hotkeys.Select(h => h.Code).Order());

        Assert.Equal("showing 2 of 6", detail.HotkeyListLine);
        Assert.Contains(VisibleText(window), t => t == "showing 2 of 6");
    }

    /// <summary>
    /// Under this filter the list is the work remaining, so it gets shorter as the work
    /// gets done. Resolving a pair takes both of its rows away — neither collides any more.
    /// </summary>
    [AvaloniaFact]
    public async Task Resolving_a_conflict_takes_it_out_of_the_filtered_list()
    {
        var (_, vm) = Show();
        var detail = await OpenTab(vm);

        detail.OnlyClashes = true;
        Assert.Equal(2, detail.Hotkeys.Count);

        detail.Hotkeys.Single(h => h.Code == "scribepinhud").Binding = KeyBinding.Parse("Ctrl-K");

        Assert.Equal(0, detail.HotkeyClashCount);
        Assert.Empty(detail.Hotkeys);

        // And the pack says so, rather than leaving an empty box with no explanation.
        Assert.True(detail.ShowNoHotkeysFound);
        Assert.Equal(
            "Nothing collides. Every hotkey in this pack is on a key of its own.",
            detail.NoHotkeysFoundLine);
    }

    /// <summary>Fixing one of three leaves the other two, rather than clearing the list.</summary>
    [AvaloniaFact]
    public async Task The_conflicts_that_are_left_stay_on_screen()
    {
        var (_, vm) = Show();
        var detail = await OpenTab(vm);

        // A third mod on P, so resolving one pair does not resolve everything.
        detail.Hotkeys.Single(h => h.Code == "prospector-scan").Binding = KeyBinding.Parse("P");
        detail.OnlyClashes = true;
        Assert.Equal(3, detail.Hotkeys.Count);

        detail.Hotkeys.Single(h => h.Code == "scribepinhud").Binding = KeyBinding.Parse("Ctrl-K");

        Assert.Equal(
            ["prospector-scan", "prospectorsinstinct-config"],
            detail.Hotkeys.Select(h => h.Code).Order());
    }

    /// <summary>
    /// The trap in filtering an editable list: the rows that are not on screen are still
    /// part of the pack.
    /// </summary>
    [AvaloniaFact]
    public async Task Editing_while_filtered_keeps_the_bindings_it_is_not_showing()
    {
        var (_, vm) = Show();
        var detail = await OpenTab(vm);

        detail.Hotkeys.Single(h => h.Code == "scribepinhud").Binding = KeyBinding.Parse("Ctrl-K");
        detail.Hotkeys.Single(h => h.Code == "prospector-scan").Binding = KeyBinding.Parse("Alt-J");

        detail.HotkeySearch = "scribe";
        Assert.Single(detail.Hotkeys);


        var manifest = PackManifest.Load(Path.Combine(_home, "packs", "anego", "pack.json"));
        Assert.Equal("Ctrl-K", manifest.Keybinds!["scribepinhud"]);
        Assert.Equal("Alt-J", manifest.Keybinds["prospector-scan"]);
    }

    /// <summary>A collision is a fact about the pack, not about what is on screen.</summary>
    [AvaloniaFact]
    public async Task A_search_that_hides_one_half_of_a_clash_still_counts_it()
    {
        var (_, vm) = Show();
        var detail = await OpenTab(vm);

        detail.HotkeySearch = "scribe";

        Assert.Single(detail.Hotkeys);
        Assert.Equal(2, detail.HotkeyClashCount);
        Assert.True(detail.Hotkeys[0].Clashes);
    }

    [AvaloniaFact]
    public async Task A_search_matching_nothing_says_so()
    {
        var (_, vm) = Show();
        var detail = await OpenTab(vm);

        detail.HotkeySearch = "nothing here is called this";

        Assert.Empty(detail.Hotkeys);
        Assert.True(detail.ShowNoHotkeysFound);
        Assert.Equal("No hotkey matches that.", detail.NoHotkeysFoundLine);
    }

    /// <summary>
    /// The bug this replaced: there was a Save button, edits sat unsaved until it was
    /// pressed, selecting another pack threw them away without a word, and the pack did not
    /// offer itself for publishing because nothing had reached the disk. Nothing else in
    /// this pane works that way — adding a mod and pinning a version both write on the
    /// click — so neither does this.
    /// </summary>
    [AvaloniaFact]
    public async Task An_edit_reaches_the_pack_with_no_second_step()
    {
        var (_, vm) = Show();
        var detail = await OpenTab(vm);

        var path = Path.Combine(_home, "packs", "anego", "pack.json");
        Assert.DoesNotContain("keybinds", File.ReadAllText(path));

        detail.Hotkeys.Single(h => h.Code == "scribepinhud").Binding = KeyBinding.Parse("Ctrl-K");

        Assert.Equal("Ctrl-K", PackManifest.Load(path).Keybinds!["scribepinhud"]);
    }

    /// <summary>
    /// And it survives the pane being rebuilt, which is what happens the moment somebody
    /// clicks another pack.
    /// </summary>
    [AvaloniaFact]
    public async Task An_edit_survives_selecting_another_pack()
    {
        var (_, vm) = Show();
        var detail = await OpenTab(vm);

        detail.Hotkeys.Single(h => h.Code == "scribepinhud").Binding = KeyBinding.Parse("Ctrl-K");

        vm.SelectedPack = null;
        vm.SelectedPack = vm.Packs.First();

        var reopened = await OpenTab(vm);
        Assert.Equal("Ctrl-K", reopened.Hotkeys.Single(h => h.Code == "scribepinhud").EffectiveText);
    }

    /// <summary>Opening the tab reads the pack; it does not write it.</summary>
    [AvaloniaFact]
    public async Task Opening_the_tab_does_not_touch_the_manifest()
    {
        var path = Path.Combine(_home, "packs", "anego", "pack.json");
        var before = File.ReadAllText(path);

        var (_, vm) = Show();
        await OpenTab(vm);

        // A row reports its binding from inside its own constructor, before the list it is
        // joining contains it — so a write here saved a set with that row missing from it.
        Assert.Equal(before, File.ReadAllText(path));
    }

    // ---- keys that are held rather than pressed ----

    /// <summary>
    /// Shift and Ctrl are shared by design: vanilla puts sneak, the click modifier and the
    /// middle mouse button on LShift, and CarryOn's swap to back is deliberately Ctrl-click.
    /// Counting those buried the mods actually fighting over P.
    /// </summary>
    [AvaloniaFact]
    public async Task Two_mods_holding_the_same_modifier_are_not_a_conflict()
    {
        var (_, vm) = Show();
        var detail = await OpenTab(vm);

        var swap = detail.Hotkeys.Single(h => h.Code == "carry-swapback");

        Assert.Equal("LControl", swap.EffectiveText);
        Assert.False(swap.Clashes);

        // Only the two on P, not the two on Ctrl.
        Assert.Equal(2, detail.HotkeyClashCount);

        detail.OnlyClashes = true;
        Assert.DoesNotContain(detail.Hotkeys, h => h.Code == "carry-swapback");
    }

    /// <summary>
    /// Said plainly all the same. A key silently shared with something else is its own
    /// puzzle, which is what sent somebody looking at the conflict list in the first place.
    /// </summary>
    [AvaloniaFact]
    public async Task A_shared_held_key_is_named_without_being_called_a_conflict()
    {
        var (window, vm) = Show();
        var detail = await OpenTab(vm);

        var swap = detail.Hotkeys.Single(h => h.Code == "carry-swapback");

        Assert.True(swap.SharesHeldKey);
        Assert.True(swap.ShowSharedKey);
        Assert.False(swap.ShowClash);
        Assert.Equal("held alongside Haul modifier", swap.SharedKeyLine);
        Assert.Contains(VisibleText(window), t => t == "held alongside Haul modifier");
    }

    [AvaloniaFact]
    public async Task Searching_for_a_modifier_still_finds_everything_on_it()
    {
        var (_, vm) = Show();
        var detail = await OpenTab(vm);

        // Not a conflict is not the same as not findable.
        detail.HotkeySearch = "LControl";

        Assert.Equal(
            ["carry-swapback", "haul-modifier"],
            detail.Hotkeys.Select(h => h.Code).Order());
    }

    // ---- the player's own controls ----

    /// <summary>
    /// Movement is marked and held back, not forbidden. A hard lock was the wrong rule: it
    /// caught Sit down, which is movement by type and a key mods genuinely want.
    /// </summary>
    [AvaloniaFact]
    public async Task A_movement_control_says_what_it_is_and_waits_to_be_unlocked()
    {
        var (window, vm) = Show();
        var detail = await OpenTab(vm);

        var clamber = detail.Hotkeys.Single(h => h.Code == "clamber-up");

        Assert.True(clamber.IsPlayerControl);
        Assert.Equal("movement control", clamber.ControlLabel);
        Assert.False(clamber.CanEdit);
        Assert.True(clamber.ShowUnlock);
        Assert.False(clamber.CaptureCommand.CanExecute(null));
        Assert.Contains(VisibleText(window), t => t == "movement control");

        clamber.UnlockCommand.Execute(null);

        Assert.True(clamber.CanEdit);
        Assert.False(clamber.ShowUnlock);
        Assert.True(clamber.CaptureCommand.CanExecute(null));

        // The tag stays up afterwards: the row is still one whose key somebody's hands know.
        Assert.True(clamber.ShowControlLabel);

        clamber.CaptureCommand.Execute(null);
        detail.CaptureHotkey(KeyBindingCode("H"), false, false, false);
        Assert.Equal("H", clamber.EffectiveText);
    }

    [AvaloniaFact]
    public async Task An_ordinary_hotkey_needs_no_unlocking()
    {
        var (_, vm) = Show();
        var detail = await OpenTab(vm);

        var pin = detail.Hotkeys.Single(h => h.Code == "scribepinhud");

        Assert.False(pin.IsPlayerControl);
        Assert.False(pin.ShowUnlock);
        Assert.True(pin.CanEdit);
        Assert.Equal("", pin.ControlLabel);
    }

    // ---- no key at all ----

    /// <summary>
    /// The third answer to a collision. Five mods want P and for four of them the honest
    /// resolution is not another key.
    /// </summary>
    [AvaloniaFact]
    public async Task Unbinding_takes_a_hotkey_off_its_key_and_out_of_the_clash()
    {
        var (_, vm) = Show();
        var detail = await OpenTab(vm);

        Assert.Equal(2, detail.HotkeyClashCount);

        var pin = detail.Hotkeys.Single(h => h.Code == "scribepinhud");
        pin.UnbindCommand.Execute(null);

        Assert.True(pin.IsUnbound);
        Assert.Equal("none", pin.EffectiveText);
        Assert.Equal(0, detail.HotkeyClashCount);
    }

    [AvaloniaFact]
    public async Task Two_unbound_hotkeys_do_not_collide_with_each_other()
    {
        var (_, vm) = Show();
        var detail = await OpenTab(vm);

        foreach (var row in detail.Hotkeys.Where(h => h.CanEdit).ToList())
            row.UnbindCommand.Execute(null);

        // They are both switched off. Calling that a conflict would fill the list with the
        // rows somebody has already dealt with.
        Assert.Equal(0, detail.HotkeyClashCount);
    }

    [AvaloniaFact]
    public async Task An_unbound_hotkey_travels_with_the_pack_and_reads_back()
    {
        var (_, vm) = Show();
        var detail = await OpenTab(vm);

        detail.Hotkeys.Single(h => h.Code == "scribepinhud").UnbindCommand.Execute(null);

        var manifest = PackManifest.Load(Path.Combine(_home, "packs", "anego", "pack.json"));
        Assert.Equal("none", manifest.Keybinds!["scribepinhud"]);

        // And it is a binding like any other on the way back in — distinct from the pack
        // saying nothing, which would hand the hotkey back to the mod's own default.
        Assert.True(KeyBinding.Parse(manifest.Keybinds["scribepinhud"])!.IsUnbound);
    }

    [AvaloniaFact]
    public async Task Reset_after_unbinding_gives_the_mods_key_back()
    {
        var (_, vm) = Show();
        var detail = await OpenTab(vm);

        var pin = detail.Hotkeys.Single(h => h.Code == "scribepinhud");
        pin.UnbindCommand.Execute(null);
        pin.ClearCommand.Execute(null);

        Assert.False(pin.IsUnbound);
        Assert.Equal("P", pin.EffectiveText);
    }

    [AvaloniaFact]
    public async Task A_locked_control_cannot_be_unbound_either()
    {
        var (_, vm) = Show();
        var detail = await OpenTab(vm);

        var clamber = detail.Hotkeys.Single(h => h.Code == "clamber-up");

        // Taking somebody's movement key away is the same act as moving it.
        Assert.False(clamber.UnbindCommand.CanExecute(null));

        clamber.UnlockCommand.Execute(null);
        Assert.True(clamber.UnbindCommand.CanExecute(null));
    }

    // ---- and the pack that carries them ----

    /// <summary>
    /// Hotkeys are part of the shared document, so editing them is something to publish.
    /// The Share button has to notice without the pane being rebuilt.
    /// </summary>
    [AvaloniaFact]
    public async Task Rebinding_offers_the_pack_for_publishing_again()
    {
        var store = new PackStore(Path.Combine(_home, "packs"));

        store.SaveLink("anego", new PackLink
        {
            Role = PackRole.Author,
            Url = "https://cairns.gg/dizzyd/anego",
            Published = new PublishRecord
            {
                Fingerprint = PackLink.Fingerprint(store.PublishedDocument("anego", stripConnect: false)),
                Visibility = "public",
                Connect = "included",
            },
        });

        var (window, vm) = Show();
        var detail = await OpenTab(vm);

        Assert.Equal("Shared", detail.ShareLabel);

        detail.Hotkeys.Single(h => h.Code == "scribepinhud").Binding = KeyBinding.Parse("Ctrl-K");


        Assert.Equal("Publish changes", detail.ShareLabel);
        Assert.True(detail.ShareIsUrgent);

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Contains(
            window.GetVisualDescendants().OfType<Button>()
                .Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string),
            t => t == "Publish changes");
    }

    // ---- the keyboard, which is the part with no second chance ----

    /// <summary>
    /// Space and Enter are the two keys a focused button keeps for itself, and capture
    /// starts from a click on a button.
    ///
    /// This is why the handler tunnels from the window instead of overriding OnKeyDown:
    /// the override is a class handler on the bubbling pass, which runs after the focused
    /// control has had the key and only if it did not take it. Both were unbindable, and
    /// the row sat on "Press a key…" for ever waiting for a press that never left the
    /// button. Driven through the button, focus and all, because invoking the command
    /// directly is exactly the shortcut that hid this.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(PhysicalKey.Space, "Space")]
    [InlineData(PhysicalKey.Enter, "Enter")]
    public async Task A_key_the_focused_button_wants_still_reaches_the_row(
        PhysicalKey pressed, string expected)
    {
        var (window, vm) = Show();
        var detail = await OpenTab(vm);

        var pin = detail.Hotkeys.Single(h => h.Code == "scribepinhud");
        var button = CaptureButtonFor(window, pin);

        button.Focus();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(button.IsFocused);

        pin.CaptureCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        window.KeyPressQwerty(pressed, RawInputModifiers.None);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(expected, pin.EffectiveText);
        Assert.False(pin.Capturing);
    }

    /// <summary>
    /// Asking a second row for a key means you changed your mind, not that both should be
    /// listening.
    ///
    /// Both listening is worse than it sounds. The press went to whichever row came first
    /// in the list — which is sorted by mod file name and has nothing to do with what
    /// anybody clicked — and the other stayed armed until the tab changed, so the next key
    /// pressed anywhere in the window landed on it.
    /// </summary>
    [AvaloniaFact]
    public async Task Arming_a_second_row_lets_the_first_go()
    {
        var (window, vm) = Show();
        var detail = await OpenTab(vm);

        // Deliberately in list order, so a rule of "first one found" would pick the wrong
        // one and pass by accident.
        var first = detail.Hotkeys.Single(h => h.Code == "prospectorsinstinct-config");
        var second = detail.Hotkeys.Single(h => h.Code == "scribepinhud");

        first.CaptureCommand.Execute(null);
        second.CaptureCommand.Execute(null);

        Assert.False(first.Capturing);
        Assert.Same(second, detail.CapturingRow);

        window.KeyPressQwerty(PhysicalKey.J, RawInputModifiers.Shift);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal("Shift-J", second.EffectiveText);
        Assert.False(first.IsOverridden);
    }

    /// <summary>
    /// A binding the pack declares for a hotkey no row could be built for.
    ///
    /// The rows are what this scan found, which is not everything the manifest can name:
    /// the game's own are missing until its version is installed, and a mod that builds its
    /// registration at runtime never produces one. Rebuilding the dictionary from the rows
    /// deleted every one of those the moment somebody touched an unrelated key — from the
    /// document that travels, and most reliably on a machine that had not downloaded the
    /// game yet.
    /// </summary>
    [AvaloniaFact]
    public async Task A_binding_with_no_row_survives_an_edit_to_another()
    {
        var path = Path.Combine(_home, "packs", "anego", "pack.json");

        var manifest = PackManifest.Load(path);
        manifest.Keybinds = new Dictionary<string, string> { ["inventory"] = "Ctrl-I" };
        manifest.Save(path);

        var (_, vm) = Show();
        var detail = await OpenTab(vm);

        Assert.DoesNotContain(detail.Hotkeys, h => h.Code == "inventory");

        detail.Hotkeys.Single(h => h.Code == "scribepinhud").Binding = KeyBinding.Parse("Ctrl-K");

        var saved = PackManifest.Load(path);
        Assert.Equal("Ctrl-I", saved.Keybinds!["inventory"]);
        Assert.Equal("Ctrl-K", saved.Keybinds!["scribepinhud"]);
    }

    /// <summary>Reset still has to be able to take an entry back out of the manifest.</summary>
    [AvaloniaFact]
    public async Task Reset_removes_the_entry_and_leaves_the_rest()
    {
        var path = Path.Combine(_home, "packs", "anego", "pack.json");

        var manifest = PackManifest.Load(path);
        manifest.Keybinds = new Dictionary<string, string>
        {
            ["inventory"] = "Ctrl-I",
            ["scribepinhud"] = "Ctrl-K",
        };
        manifest.Save(path);

        var (_, vm) = Show();
        var detail = await OpenTab(vm);

        detail.Hotkeys.Single(h => h.Code == "scribepinhud").ClearCommand.Execute(null);

        var saved = PackManifest.Load(path);
        Assert.Equal(["inventory"], saved.Keybinds!.Keys);
    }

    // ---- the pack's files move under the list ----

    /// <summary>
    /// A mod added after the tab was first opened.
    ///
    /// The first thing anybody does with a new pack is add a mod to it, and the rows were
    /// read once per pack selection and never again: open Hotkeys on an empty pack, add
    /// Packrat, come back, and its hotkey was not there — not misread, never read. The scan
    /// was fine. Nothing asked it a second time.
    /// </summary>
    [AvaloniaFact]
    public async Task A_mod_added_after_the_tab_was_opened_shows_up()
    {
        var (_, vm) = Show();
        var detail = await OpenTab(vm);

        Assert.DoesNotContain(detail.Hotkeys, h => h.Code == "packrat.openall");

        WriteModZip(Path.Combine(_home, "packs", "anego", "Mods", "packrat.zip"),
            [("packrat.openall", "Open all", 100, HotkeyKind.CharacterControls)]);

        // Leaving and coming back is what somebody does after adding a mod.
        detail.SelectedTab = 0;
        detail.SelectedTab = PackDetailViewModel.HotkeysTab;
        await Settle(() => detail.Hotkeys.Any(h => h.Code == "packrat.openall"));

        Assert.Contains(detail.Hotkeys, h => h.Code == "packrat.openall");
    }

    /// <summary>
    /// And a mod removed from it, which is the same staleness in the other direction: a row
    /// for a hotkey no mod in the pack registers any more, offering to bind it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_mod_removed_after_the_tab_was_opened_goes()
    {
        var (_, vm) = Show();
        var detail = await OpenTab(vm);

        Assert.Contains(detail.Hotkeys, h => h.Code == "scribepinhud");

        File.Delete(Path.Combine(_home, "packs", "anego", "Mods", "scribe.zip"));

        detail.SelectedTab = 0;
        detail.SelectedTab = PackDetailViewModel.HotkeysTab;
        await Settle(() => detail.Hotkeys.All(h => h.Code != "scribepinhud"));

        Assert.DoesNotContain(detail.Hotkeys, h => h.Code == "scribepinhud");
    }

    /// <summary>
    /// Re-opening a tab whose mods have not moved must not read seventy archives again.
    /// The whole reason the rows are kept is that the scan is a second of disk.
    /// </summary>
    [AvaloniaFact]
    public async Task Re_opening_an_unchanged_tab_reads_nothing()
    {
        var (_, vm) = Show();
        var detail = await OpenTab(vm);

        var rows = detail.Hotkeys.ToList();

        detail.SelectedTab = 0;
        detail.SelectedTab = PackDetailViewModel.HotkeysTab;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // The same row objects, not equal ones: a rebuild would drop any capture in
        // progress and lose the unlock somebody had just granted.
        Assert.Equal(rows, detail.Hotkeys);
    }

    private static Button CaptureButtonFor(MainWindow window, HotkeyRowViewModel row) =>
        window.GetVisualDescendants().OfType<Button>()
            .First(b => ReferenceEquals(b.DataContext, row) && b.Command == row.CaptureCommand);

    private static IEnumerable<string> VisibleText(Visual root) =>
        root.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text))
            .Select(t => t.Text!);

    private static int KeyBindingCode(string name) => KeyBinding.Parse(name)!.KeyCode;
}
