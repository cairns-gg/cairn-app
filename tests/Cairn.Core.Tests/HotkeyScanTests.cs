using System.Reflection;
using System.Reflection.Emit;
using Cairn.Core.Hotkeys;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Reading hotkeys out of an assembly without running it.
///
/// The fixtures are real assemblies, emitted here rather than checked in, because the thing
/// under test is an IL walk and a hand-written byte array would only prove the walk agrees
/// with whatever the byte array was written to contain. An earlier version of this scanner
/// looked for byte patterns and read operand bytes as instructions, reporting a key code of
/// 1919943387 — four ASCII characters of somebody's string — with total confidence.
/// </summary>
public class HotkeyScanTests
{
    /// <summary>What an emitted fixture can call.</summary>
    private sealed record Fixture(ILGenerator Il, MethodBuilder Register, MethodBuilder Lang);

    /// <summary>
    /// Builds an assembly with a RegisterHotKey of the game's own signature and a method
    /// that calls it however the test says.
    /// </summary>
    private static byte[] Emit(Action<Fixture> body)
    {
        var assembly = new PersistedAssemblyBuilder(
            new AssemblyName("FakeMod"), typeof(object).Assembly);

        var module = assembly.DefineDynamicModule("FakeMod");
        var type = module.DefineType("FakeMod.System", TypeAttributes.Public);

        // (string hotkeyCode, string name, GlKeys key, HotkeyType type, bool alt, bool ctrl, bool shift)
        var register = type.DefineMethod(
            "RegisterHotKey", MethodAttributes.Public | MethodAttributes.Static, typeof(void),
            [typeof(string), typeof(string), typeof(int), typeof(int),
             typeof(bool), typeof(bool), typeof(bool)]);
        register.GetILGenerator().Emit(OpCodes.Ret);

        // Stands in for Lang.Get(...) — a call whose result is one of the arguments, which
        // is what displaced every later argument by a slot before the stack depth was
        // modelled properly.
        var lang = type.DefineMethod(
            "Lang", MethodAttributes.Public | MethodAttributes.Static, typeof(string), [typeof(string)]);
        var langIl = lang.GetILGenerator();
        langIl.Emit(OpCodes.Ldarg_0);
        langIl.Emit(OpCodes.Ret);

        var caller = type.DefineMethod(
            "Register", MethodAttributes.Public | MethodAttributes.Static, typeof(void), []);

        var il = caller.GetILGenerator();
        body(new Fixture(il, register, lang));
        il.Emit(OpCodes.Ret);

        type.CreateType();

        using var stream = new MemoryStream();
        assembly.Save(stream);
        return stream.ToArray();
    }

    /// <summary>One registration with every argument written out literally.</summary>
    private static void Call(
        Fixture f, string code, string name, int key, HotkeyKind kind,
        bool alt = false, bool ctrl = false, bool shift = false, bool viaLang = true)
    {
        f.Il.Emit(OpCodes.Ldstr, code);
        f.Il.Emit(OpCodes.Ldstr, name);
        if (viaLang) f.Il.Emit(OpCodes.Call, f.Lang);
        f.Il.Emit(OpCodes.Ldc_I4, key);
        f.Il.Emit(OpCodes.Ldc_I4, (int)kind);
        f.Il.Emit(alt ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
        f.Il.Emit(ctrl ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
        f.Il.Emit(shift ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
        f.Il.Emit(OpCodes.Call, f.Register);
    }

    [Fact]
    public void A_literal_registration_is_read_whole()
    {
        var image = Emit(f => Call(
            f, "scribepinhud", "scribe:hotkey", 98, HotkeyKind.GUIOrOtherControls,
            ctrl: true, viaLang: false));

        var found = HotkeyScan.Read(image, out var unreadable);

        var hotkey = Assert.Single(found);
        Assert.Equal("scribepinhud", hotkey.Code);
        Assert.Equal("scribe:hotkey", hotkey.Name);
        Assert.Equal(HotkeyKind.GUIOrOtherControls, hotkey.Kind);

        // The modifiers are three separate booleans in the game's own order — alt, ctrl,
        // shift — and reading them in another order produces a real binding on wrong keys.
        Assert.Equal("Ctrl-P", hotkey.Default!.ToString());
        Assert.Equal(0, unreadable);
    }

    /// <summary>
    /// Mods usually fetch the label through <c>Lang.Get</c>, so the name is the result of a
    /// call rather than a literal. That is the case that used to break everything after it.
    /// </summary>
    [Fact]
    public void A_name_fetched_at_runtime_costs_the_name_and_nothing_else()
    {
        var direct = HotkeyScan.Read(
            Emit(f => Call(f, "a", "A", 98, HotkeyKind.CharacterControls, viaLang: false)), out _)
            .Single();

        var throughLang = HotkeyScan.Read(
            Emit(f => Call(f, "a", "A", 98, HotkeyKind.CharacterControls)), out _)
            .Single();

        // The name is genuinely unknown — it is whatever the call returns at runtime, in
        // whatever language — and the honest answer is to say so.
        Assert.Null(throughLang.Name);

        // Everything pushed after it still lands where it was put. This is the assertion
        // that fails when the stack depth is modelled loosely: the code of one hotkey shows
        // up as the name of another, and the key becomes whatever integer was nearby.
        Assert.Equal("a", throughLang.Code);
        Assert.Equal(direct.Default, throughLang.Default);
        Assert.Equal(direct.Kind, throughLang.Kind);
    }

    /// <summary>
    /// The overwhelmingly common shape: <c>Lang.Get("mymod:hotkey-thing")</c>. What it
    /// returns is not knowable — it depends on the player's language — but its argument is
    /// the key, and the mod ships the translations. Without this the list showed ids.
    /// </summary>
    [Fact]
    public void A_name_fetched_through_Lang_Get_yields_the_key_it_asked_for()
    {
        var image = Emit(f =>
        {
            f.Il.Emit(OpCodes.Ldstr, "scribepinhud");
            f.Il.Emit(OpCodes.Ldstr, "scribe:hotkey-scribepinhud");
            f.Il.Emit(OpCodes.Call, LangGet());
            f.Il.Emit(OpCodes.Ldc_I4, 98);
            f.Il.Emit(OpCodes.Ldc_I4_2);
            f.Il.Emit(OpCodes.Ldc_I4_0);
            f.Il.Emit(OpCodes.Ldc_I4_0);
            f.Il.Emit(OpCodes.Ldc_I4_0);
            f.Il.Emit(OpCodes.Call, f.Register);
        });

        var hotkey = Assert.Single(HotkeyScan.Read(image, out var unreadable));

        Assert.Equal("scribe:hotkey-scribepinhud", hotkey.Name);
        Assert.Equal("P", hotkey.Default!.ToString());
        Assert.Equal(0, unreadable);
    }

    /// <summary>
    /// CarryOn builds its lang keys as <c>ModId + ":pickup-hotkey"</c>, where ModId is a
    /// static field. Joining two things this walk knows is arithmetic, not a guess.
    /// </summary>
    [Fact]
    public void Concatenated_literals_are_folded()
    {
        var image = Emit(f =>
        {
            f.Il.Emit(OpCodes.Ldstr, "carryonpickupkey");
            f.Il.Emit(OpCodes.Ldstr, "carryon");
            f.Il.Emit(OpCodes.Ldstr, ":pickup-hotkey");
            f.Il.Emit(OpCodes.Call, typeof(string).GetMethod("Concat", [typeof(string), typeof(string)])!);
            f.Il.Emit(OpCodes.Call, LangGet());
            f.Il.Emit(OpCodes.Ldc_I4, 1);
            f.Il.Emit(OpCodes.Ldc_I4_4);
            f.Il.Emit(OpCodes.Ldc_I4_0);
            f.Il.Emit(OpCodes.Ldc_I4_0);
            f.Il.Emit(OpCodes.Ldc_I4_0);
            f.Il.Emit(OpCodes.Call, f.Register);
        });

        var hotkey = Assert.Single(HotkeyScan.Read(image, out _));

        Assert.Equal("carryon:pickup-hotkey", hotkey.Name);
        Assert.Equal("LShift", hotkey.Default!.ToString());
    }

    /// <summary>
    /// A stand-in for <c>Vintagestory.API.Config.Lang.Get</c>. Matched on the declaring type
    /// name as well as the method name, so any old Get is not treated as a translation.
    /// </summary>
    private static MethodInfo LangGet() =>
        typeof(Lang).GetMethod(nameof(Lang.Get))!;

    private static class Lang
    {
        public static string Get(string key) => key;
    }

    [Fact]
    public void A_computed_code_is_reported_rather_than_guessed()
    {
        var image = Emit(f =>
        {
            // A code built at runtime from something outside the file — a config value, a
            // loop index. Joining two literals would fold; this cannot, and a
            // plausible-looking wrong answer would end up in a settings file.
            f.Il.Emit(OpCodes.Call, typeof(Environment).GetProperty(nameof(Environment.NewLine))!.GetMethod!);
            f.Il.Emit(OpCodes.Ldstr, ":suffix");
            f.Il.Emit(OpCodes.Call, typeof(string).GetMethod("Concat", [typeof(string), typeof(string)])!);
            f.Il.Emit(OpCodes.Ldstr, "name");
            f.Il.Emit(OpCodes.Ldc_I4, 98);
            f.Il.Emit(OpCodes.Ldc_I4_2);
            f.Il.Emit(OpCodes.Ldc_I4_0);
            f.Il.Emit(OpCodes.Ldc_I4_0);
            f.Il.Emit(OpCodes.Ldc_I4_0);
            f.Il.Emit(OpCodes.Call, f.Register);
        });

        Assert.Empty(HotkeyScan.Read(image, out var unreadable));
        Assert.Equal(1, unreadable);
    }

    [Fact]
    public void A_computed_key_keeps_the_hotkey_and_drops_only_the_binding()
    {
        var image = Emit(f =>
        {
            f.Il.Emit(OpCodes.Ldstr, "keylockertoggle");
            f.Il.Emit(OpCodes.Ldstr, "name");
            f.Il.Emit(OpCodes.Ldc_I4, 40);
            f.Il.Emit(OpCodes.Ldc_I4, 2);
            f.Il.Emit(OpCodes.Add);                       // a key worked out rather than written
            f.Il.Emit(OpCodes.Ldc_I4_2);
            f.Il.Emit(OpCodes.Ldc_I4_0);
            f.Il.Emit(OpCodes.Ldc_I4_0);
            f.Il.Emit(OpCodes.Ldc_I4_0);
            f.Il.Emit(OpCodes.Call, f.Register);
        });

        // The hotkey exists and is worth showing — the editor can bind it. Only the default
        // is unknown, and saying so is the honest answer.
        var hotkey = Assert.Single(HotkeyScan.Read(image, out var unreadable));
        Assert.Equal("keylockertoggle", hotkey.Code);
        Assert.Null(hotkey.Default);

        // Not counted as one we could not read: it is in the list, and a caller adding the
        // two together reports a row somebody is looking at as a row they cannot see.
        // The null default is what says the key is unknown.
        Assert.Equal(0, unreadable);
    }

    [Fact]
    public void Several_registrations_in_one_method_stay_in_their_own_slots()
    {
        var image = Emit(f =>
        {
            Call(f, "first", "1", 98, HotkeyKind.CharacterControls);
            Call(f, "second", "2", 99, HotkeyKind.CharacterControls);
            Call(f, "third", "3", 100, HotkeyKind.CharacterControls, shift: true);
        });

        var found = HotkeyScan.Read(image, out _);

        Assert.Equal(["first", "second", "third"], found.Select(x => x.Code));
        Assert.Equal(["P", "Q", "Shift-R"], found.Select(x => x.Default!.ToString()));
    }

    /// <summary>
    /// Builds an assembly whose registration takes its code and key from static fields set
    /// in the type initialiser, optionally assigning the code twice.
    /// </summary>
    private static byte[] EmitWithStaticFields(string code, int key, string? reassignedTo = null)
    {
        var assembly = new PersistedAssemblyBuilder(
            new AssemblyName("FakeMod"), typeof(object).Assembly);

        var module = assembly.DefineDynamicModule("FakeMod");
        var type = module.DefineType("FakeMod.System", TypeAttributes.Public);

        var register = type.DefineMethod(
            "RegisterHotKey", MethodAttributes.Public | MethodAttributes.Static, typeof(void),
            [typeof(string), typeof(string), typeof(int), typeof(int),
             typeof(bool), typeof(bool), typeof(bool)]);
        register.GetILGenerator().Emit(OpCodes.Ret);

        const FieldAttributes StaticReadonly =
            FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly;

        var codeField = type.DefineField("HotkeyCode", typeof(string), StaticReadonly);
        var keyField = type.DefineField("DefaultKey", typeof(int), StaticReadonly);

        var cctor = type.DefineTypeInitializer().GetILGenerator();
        cctor.Emit(OpCodes.Ldstr, code);
        cctor.Emit(OpCodes.Stsfld, codeField);
        if (reassignedTo is not null)
        {
            cctor.Emit(OpCodes.Ldstr, reassignedTo);
            cctor.Emit(OpCodes.Stsfld, codeField);
        }

        cctor.Emit(OpCodes.Ldc_I4, key);
        cctor.Emit(OpCodes.Stsfld, keyField);
        cctor.Emit(OpCodes.Ret);

        var caller = type.DefineMethod(
            "Register", MethodAttributes.Public | MethodAttributes.Static, typeof(void), []);
        var il = caller.GetILGenerator();
        il.Emit(OpCodes.Ldsfld, codeField);
        il.Emit(OpCodes.Ldstr, "a name");
        il.Emit(OpCodes.Ldsfld, keyField);
        il.Emit(OpCodes.Ldc_I4_4);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Call, register);
        il.Emit(OpCodes.Ret);

        type.CreateType();

        using var stream = new MemoryStream();
        assembly.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// <c>static readonly string HotkeyCode = "zoombutton"</c> is a common way to write a
    /// mod, and it compiles to a field load rather than an inlined literal. Five of one
    /// mod's six registrations were lost to this before the type initialiser was read.
    /// </summary>
    [Fact]
    public void A_code_held_in_a_static_field_is_resolved_from_the_type_initialiser()
    {
        var found = HotkeyScan.Read(EmitWithStaticFields("zoombutton", 108), out var unreadable);

        var hotkey = Assert.Single(found);
        Assert.Equal("zoombutton", hotkey.Code);
        Assert.Equal("Z", hotkey.Default!.ToString());
        Assert.Equal(0, unreadable);
    }

    [Fact]
    public void A_field_assigned_twice_is_left_unknown()
    {
        // The walk does not follow branches, so it cannot say which assignment is the one
        // that runs — and picking whichever came last in the IL would be a guess dressed
        // as an answer.
        Assert.Empty(HotkeyScan.Read(
            EmitWithStaticFields("first", 108, reassignedTo: "second"), out var unreadable));

        Assert.Equal(1, unreadable);
    }

    /// <summary>
    /// The shape that made Packrat's hotkey vanish from a real pack: the code goes into a
    /// local first, and is built out of the mod's own id.
    ///
    /// <code>
    /// var hotkey = Mod.Info.ModID + ".openall";
    /// api.Input.RegisterHotKey(hotkey, Lang.Get($"{Mod.Info.ModID}:openall"), GlKeys.R, ...);
    /// </code>
    ///
    /// It was not merely missing its default — the whole registration was dropped, so a
    /// three-way collision on R was reported as a two-way one.
    /// </summary>
    [Fact]
    public void A_code_built_from_the_mod_id_and_held_in_a_local_is_recovered()
    {
        var image = Emit(f =>
        {
            var local = f.Il.DeclareLocal(typeof(string));

            f.Il.Emit(OpCodes.Call, ModInfoGetter());          // Mod.Info.ModID
            f.Il.Emit(OpCodes.Ldstr, ".openall");
            f.Il.Emit(OpCodes.Call, typeof(string).GetMethod("Concat", [typeof(string), typeof(string)])!);
            f.Il.Emit(OpCodes.Stloc, local);

            f.Il.Emit(OpCodes.Ldloc, local);
            f.Il.Emit(OpCodes.Ldstr, "name");
            f.Il.Emit(OpCodes.Ldc_I4, 100);                    // R
            f.Il.Emit(OpCodes.Ldc_I4_4);
            f.Il.Emit(OpCodes.Ldc_I4_0);
            f.Il.Emit(OpCodes.Ldc_I4_0);
            f.Il.Emit(OpCodes.Ldc_I4_0);
            f.Il.Emit(OpCodes.Call, f.Register);
        });

        var hotkey = Assert.Single(HotkeyScan.Read(image, out var unreadable, modId: "packrat"));

        Assert.Equal("packrat.openall", hotkey.Code);
        Assert.Equal("R", hotkey.Default!.ToString());
        Assert.Equal(0, unreadable);
    }

    [Fact]
    public void Without_the_mod_id_that_registration_is_reported_as_unread()
    {
        var image = Emit(f =>
        {
            var local = f.Il.DeclareLocal(typeof(string));
            f.Il.Emit(OpCodes.Call, ModInfoGetter());
            f.Il.Emit(OpCodes.Ldstr, ".openall");
            f.Il.Emit(OpCodes.Call, typeof(string).GetMethod("Concat", [typeof(string), typeof(string)])!);
            f.Il.Emit(OpCodes.Stloc, local);
            f.Il.Emit(OpCodes.Ldloc, local);
            f.Il.Emit(OpCodes.Ldstr, "name");
            f.Il.Emit(OpCodes.Ldc_I4, 100);
            f.Il.Emit(OpCodes.Ldc_I4_4);
            f.Il.Emit(OpCodes.Ldc_I4_0);
            f.Il.Emit(OpCodes.Ldc_I4_0);
            f.Il.Emit(OpCodes.Ldc_I4_0);
            f.Il.Emit(OpCodes.Call, f.Register);
        });

        // A zip with no readable modinfo.json says so rather than inventing a prefix.
        Assert.Empty(HotkeyScan.Read(image, out var unreadable));
        Assert.Equal(1, unreadable);
    }

    [Fact]
    public void A_local_written_twice_with_different_values_is_unknown()
    {
        var image = Emit(f =>
        {
            var local = f.Il.DeclareLocal(typeof(string));

            // Which one runs depends on a branch this walk did not follow.
            f.Il.Emit(OpCodes.Ldstr, "one");
            f.Il.Emit(OpCodes.Stloc, local);
            f.Il.Emit(OpCodes.Ldstr, "two");
            f.Il.Emit(OpCodes.Stloc, local);

            f.Il.Emit(OpCodes.Ldloc, local);
            f.Il.Emit(OpCodes.Ldstr, "name");
            f.Il.Emit(OpCodes.Ldc_I4, 100);
            f.Il.Emit(OpCodes.Ldc_I4_4);
            f.Il.Emit(OpCodes.Ldc_I4_0);
            f.Il.Emit(OpCodes.Ldc_I4_0);
            f.Il.Emit(OpCodes.Ldc_I4_0);
            f.Il.Emit(OpCodes.Call, f.Register);
        });

        Assert.Empty(HotkeyScan.Read(image, out var unreadable, modId: "packrat"));
        Assert.Equal(1, unreadable);
    }

    /// <summary>A stand-in for <c>Mod.Info.ModID</c>, matched on the declaring type's name.</summary>
    private static MethodInfo ModInfoGetter() =>
        typeof(ModInfo).GetProperty(nameof(ModInfo.ModID))!.GetMethod!;

    private static class ModInfo
    {
        public static string ModID => "";
    }

    [Fact]
    public void An_assembly_that_registers_nothing_yields_nothing()
    {
        var image = Emit(f => f.Il.Emit(OpCodes.Nop));

        Assert.Empty(HotkeyScan.Read(image, out var unreadable));
        Assert.Equal(0, unreadable);
    }

    [Fact]
    public void Anything_that_is_not_an_assembly_is_not_an_error()
    {
        // Mod zips carry native libraries beside managed ones, and a truncated download is
        // a thing that happens. Neither is a reason for a pack to refuse to open.
        Assert.Empty(HotkeyScan.Read([1, 2, 3, 4], out _));
        Assert.Empty(HotkeyScan.Read([], out _));
    }
}

/// <summary>
/// Noticing that a pack's mods have moved.
///
/// Reading seventy archives is a second of disk, so the result is kept — and a kept result
/// is only ever as good as the thing that says when to throw it away. Invalidating at each
/// place that changes a pack is the version of this that misses the next place: a pack gains
/// mods from sync, from an update, from an import and from somebody dropping a zip in the
/// folder.
/// </summary>
public class HotkeyStampTests : IDisposable
{
    private readonly string _mods = Path.Combine(
        Path.GetTempPath(), "cairn-stamp-" + Guid.NewGuid().ToString("n")[..8]);

    public HotkeyStampTests() => Directory.CreateDirectory(_mods);

    public void Dispose()
    {
        if (Directory.Exists(_mods)) Directory.Delete(_mods, recursive: true);
    }

    private string Write(string name, string content = "zip")
    {
        var path = Path.Combine(_mods, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void The_same_files_stamp_the_same()
    {
        Write("carryon.zip");
        Write("scribe.zip");

        Assert.Equal(HotkeyCatalog.Stamp(_mods), HotkeyCatalog.Stamp(_mods));
    }

    [Fact]
    public void A_mod_added_changes_it()
    {
        Write("carryon.zip");
        var before = HotkeyCatalog.Stamp(_mods);

        Write("packrat.zip");

        Assert.NotEqual(before, HotkeyCatalog.Stamp(_mods));
    }

    [Fact]
    public void A_mod_removed_changes_it()
    {
        Write("carryon.zip");
        var scribe = Write("scribe.zip");
        var before = HotkeyCatalog.Stamp(_mods);

        File.Delete(scribe);

        Assert.NotEqual(before, HotkeyCatalog.Stamp(_mods));
    }

    [Fact]
    public void A_mod_replaced_by_another_version_changes_it()
    {
        // Sync writes the new file under the same name often enough — an update to a mod
        // whose file name does not carry its version — that a listing of names alone would
        // hold a stale list of that mod's hotkeys for ever.
        var path = Write("carryon.zip", "one");
        var before = HotkeyCatalog.Stamp(_mods);

        File.WriteAllText(path, "a different length entirely");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        Assert.NotEqual(before, HotkeyCatalog.Stamp(_mods));
    }

    [Fact]
    public void A_directory_that_is_not_there_stamps_as_nothing()
    {
        // A pack whose Mods folder has not been made yet is a pack with no mods, not an
        // error — and it has to stamp differently from one that has some.
        Assert.Equal("", HotkeyCatalog.Stamp(Path.Combine(_mods, "nope")));

        Write("carryon.zip");
        Assert.NotEqual("", HotkeyCatalog.Stamp(_mods));
    }
}
