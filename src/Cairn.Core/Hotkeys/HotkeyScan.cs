using System.Collections.Immutable;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Cairn.Core.Hotkeys;

/// <summary>What a mod asked the game to bind, and how sure we are of it.</summary>
/// <param name="Code">The hotkey id, which is what <c>keyMapping</c> is keyed by.</param>
/// <param name="Name">The label the controls screen shows, or a lang key for it.</param>
/// <param name="Default">The combination the mod ships, or null when it was computed.</param>
public sealed record HotkeyRegistration(
    string Code,
    string? Name,
    KeyBinding? Default,
    HotkeyKind Kind);

/// <summary>
/// <c>Vintagestory.API.Client.HotkeyType</c>. Ported for the same reason as
/// <see cref="GlKeys"/>, and needed for one rule: movement and character controls are the
/// player's, and a pack must not rebind them.
/// </summary>
public enum HotkeyKind
{
    HelpAndOverlays = 0,
    MouseModifiers = 1,
    GUIOrOtherControls = 2,
    MovementControls = 3,
    CharacterControls = 4,
    InventoryHotkeys = 5,
    CreativeTool = 6,
    CreativeOrSpectatorTool = 7,
    DevTool = 8,
    MouseControls = 9,
    Unknown = -1,
}

/// <summary>
/// Reads the hotkeys an assembly registers, without running any of it.
///
/// Hotkeys are not declared anywhere a file can be read: they come from
/// <c>IInputAPI.RegisterHotKey(code, name, key, type, ...)</c> calls in mod code. So the
/// only way to know a pack's bindings before it has ever been launched is to look at the
/// calls, and the only two ways to do that are to execute the mod or to read its IL.
///
/// This reads. Executing a downloaded mod outside the game — against a stub API, on the
/// launcher's authority, in a runtime with no sandbox — is a far larger promise than
/// "Cairn installs your mods", and the game ships a mod safety check precisely because
/// running this code is a decision. Reading bytes is not.
///
/// The same scan works on the game's own assembly, which is what makes conflict detection
/// worth anything: most collisions are between a mod and vanilla, not between two mods.
///
/// What it cannot do is recover an argument that was computed — a code built from a loop
/// index, a name from a variable. Those come back as a registration with no default rather
/// than as a guess, and the count of them is reported, because "we found 12 and could not
/// read 3" is honest where a list of 12 is not.
///
/// Two different failures, kept apart. A registration whose <em>code</em> could not be read
/// is one nobody can see or bind, and it is what <c>unreadable</c> counts. One whose code
/// read but whose <em>key</em> did not is a hotkey like any other with an unknown default —
/// it comes back in the list with a null <see cref="HotkeyRegistration.Default"/>, and
/// counting it as missing would have a caller report the same hotkey twice: once on screen,
/// once as something that is not.
/// </summary>
public static class HotkeyScan
{
    private const string Register = "RegisterHotKey";
    private const string RegisterFirst = "RegisterHotKeyFirst";

    /// <summary>Everything one assembly registers. Never throws; a mod that cannot be read has none.</summary>
    /// <param name="modId">
    /// What the zip's modinfo.json calls this mod, where it is known. Mods build their own
    /// hotkey codes out of it — <c>Mod.Info.ModID + ".openall"</c> — and without it the
    /// whole registration reads as computed.
    /// </param>
    public static IReadOnlyList<HotkeyRegistration> Read(
        byte[] assembly, out int unreadable, string? modId = null)
    {
        unreadable = 0;

        try
        {
            return ReadCore(assembly, out unreadable, modId);
        }
        catch (Exception e) when (e is BadImageFormatException or InvalidOperationException
                                      or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            // A native dll shipped beside a managed one, or metadata we cannot follow. A
            // pack must still open.
            return [];
        }
    }

    private static IReadOnlyList<HotkeyRegistration> ReadCore(
        byte[] image, out int unreadable, string? modId)
    {
        unreadable = 0;
        var found = new List<HotkeyRegistration>();

        // Wrapped rather than copied. ImmutableArray.Create duplicates the whole assembly
        // for no benefit here: this method owns the array, never mutates it, and the
        // wrapper does not outlive the call. That was the third full copy of every mod
        // assembly the hotkey scan made.
        using var pe = new PEReader(
            System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(image));
        if (!pe.HasMetadata) return found;

        var md = pe.GetMetadataReader();

        // Tokens for every RegisterHotKey the assembly can reach. Both tables: a mod calls
        // it through a MemberReference, and the game's own assembly — where the method is
        // defined — calls it through a MethodDefinition.
        var targets = new HashSet<int>();

        foreach (var h in md.MemberReferences)
            if (md.GetString(md.GetMemberReference(h).Name) is Register or RegisterFirst)
                targets.Add(MetadataTokens.GetToken(h));

        foreach (var h in md.MethodDefinitions)
            if (md.GetString(md.GetMethodDefinition(h).Name) is Register or RegisterFirst)
                targets.Add(MetadataTokens.GetToken(h));

        if (targets.Count == 0) return found;

        var constants = StaticFields(pe, md);

        foreach (var h in md.MethodDefinitions)
        {
            var definition = md.GetMethodDefinition(h);
            if (definition.RelativeVirtualAddress == 0) continue;

            var il = pe.GetMethodBody(definition.RelativeVirtualAddress).GetILBytes();
            if (il is null) continue;

            Walk(md, il, targets, found, ref unreadable, constants, modId: modId);
        }

        return found;
    }

    /// <summary>
    /// Values held in static fields, read out of the type initialisers that set them.
    ///
    /// <c>static readonly string HotkeyCode = "zoombutton"</c> is a common way to write a
    /// mod, and it compiles to a field load at the call site rather than an inlined literal
    /// — a <c>const</c> would be inlined and needs nothing here. Without this the whole
    /// registration reads as computed, and one mod in the sample lost five of them that way.
    ///
    /// A field assigned two different literals — set in a branch, or reassigned — is left
    /// unknown rather than resolved to whichever assignment came last in the IL. This walk
    /// does not follow branches, so "last" means nothing.
    /// </summary>
    private static Dictionary<int, object?> StaticFields(PEReader pe, MetadataReader md)
    {
        var values = new Dictionary<int, object?>();

        foreach (var h in md.MethodDefinitions)
        {
            var definition = md.GetMethodDefinition(h);
            if (md.GetString(definition.Name) != ".cctor") continue;
            if (definition.RelativeVirtualAddress == 0) continue;

            var il = pe.GetMethodBody(definition.RelativeVirtualAddress).GetILBytes();
            if (il is null) continue;

            var ignored = new List<HotkeyRegistration>();
            var missed = 0;

            Walk(md, il, [], ignored, ref missed, constants: null, stores: values);
        }

        return values;
    }

    /// <summary>
    /// One pass over a method body, keeping a stack of the values it can name.
    ///
    /// The stack is symbolic — string and integer literals are tracked, everything else is
    /// an unknown — but its <em>depth</em> is modelled exactly, from the runtime's own
    /// stack-behaviour table plus a signature lookup for calls. Depth is what matters: get
    /// it wrong by one and every argument is read from the wrong slot, which is how an
    /// earlier attempt reported the name of one hotkey as the code of another.
    ///
    /// Walking with real operand lengths matters for the same reason. Scanning backwards
    /// for byte patterns reads operand bytes as instructions and produces confident
    /// nonsense — a key code of 1919943387, which is four ASCII characters of a string.
    /// </summary>
    /// <param name="constants">Static field values, where they are known. See <see cref="StaticFields"/>.</param>
    /// <param name="stores">
    /// When given, what this body assigns to static fields is recorded here instead — the
    /// same walk, pointed at a type initialiser.
    /// </param>
    private static void Walk(
        MetadataReader md, byte[] il, HashSet<int> targets,
        List<HotkeyRegistration> found, ref int unreadable,
        IReadOnlyDictionary<int, object?>? constants = null,
        Dictionary<int, object?>? stores = null,
        string? modId = null)
    {
        var stack = new List<object?>();

        // Locals, because a mod as often as not writes
        //     var hotkey = ModId + ".openall";
        //     api.Input.RegisterHotKey(hotkey, ...)
        // and a walk that does not follow the variable loses the whole registration rather
        // than just its name. Same rule as the static fields: a local assigned two different
        // values is unknown, because this walk does not follow branches.
        var locals = new Dictionary<int, object?>();
        var i = 0;

        while (i < il.Length)
        {
            var start = i;
            var (code, _) = Operands.Read(il, ref i);
            if (code < 0) return;                          // not an opcode we know; stop reading this body

            switch (code)
            {
                case 0x72:                                 // ldstr
                    stack.Add(UserString(md, Operand32(il, start + 1)));
                    continue;

                case >= 0x16 and <= 0x1E:                  // ldc.i4.0 .. ldc.i4.8
                    stack.Add(code - 0x16);
                    continue;

                case 0x15:                                 // ldc.i4.m1
                    stack.Add(-1);
                    continue;

                case 0x1F:                                 // ldc.i4.s
                    stack.Add((int)(sbyte)il[start + 1]);
                    continue;

                case 0x20:                                 // ldc.i4
                    stack.Add(Operand32(il, start + 1));
                    continue;

                case >= 0x06 and <= 0x09:                  // ldloc.0 .. ldloc.3
                    stack.Add(Local(locals, code - 0x06));
                    continue;

                case 0x11:                                 // ldloc.s
                    stack.Add(Local(locals, il[start + 1]));
                    continue;

                case >= 0x0A and <= 0x0D:                  // stloc.0 .. stloc.3
                    Stored(locals, code - 0x0A, stack);
                    continue;

                case 0x13:                                 // stloc.s
                    Stored(locals, il[start + 1], stack);
                    continue;

                case 0x7E:                                 // ldsfld
                    stack.Add(constants is not null
                              && constants.TryGetValue(Operand32(il, start + 1), out var held)
                        ? held
                        : null);
                    continue;

                case 0x80:                                 // stsfld
                    if (stores is not null)
                        Assigned(stores, Operand32(il, start + 1),
                            stack.Count > 0 ? stack[^1] : null);

                    Pop(stack, 1);
                    continue;

                case 0x28 or 0x6F or 0x73:                 // call / callvirt / newobj
                {
                    var token = Operand32(il, start + 1);
                    var signature = Signatures.Of(md, token);

                    if (signature is null) { stack.Clear(); continue; }

                    var pops = signature.Value.Parameters
                               + (code != 0x73 && signature.Value.IsInstance ? 1 : 0);

                    if (code is 0x6F or 0x28 && targets.Contains(token))
                        Take(stack, signature.Value.Parameters, found, ref unreadable);

                    // Two calls whose result this walk can name.
                    //
                    // Joining literals is still a literal, and mods build both codes and
                    // lang keys as "modid" + ":something" — folding it is arithmetic, not a
                    // guess, because every part is on the stack and known.
                    //
                    // Lang.Get is where nearly every hotkey name comes from, so discarding
                    // it left the list showing ids. What it returns is not knowable here —
                    // it depends on the player's language — but its argument is the key,
                    // and the mod ships the translations. See HotkeyLang.
                    var folded =
                        IsCall(md, token, "String", "Concat") ? Join(stack, signature.Value.Parameters)
                        : IsCall(md, token, "Lang", "Get", "GetIfExists", "GetMatching")
                            ? FirstString(stack, signature.Value.Parameters)
                        // A mod asking its own name. Every hotkey code Packrat registers is
                        // built from it, and the answer is sitting in the zip's modinfo.json.
                        : modId is not null && IsCall(md, token, "ModInfo", "get_ModID")
                            ? modId
                            : null;

                    Pop(stack, pops);
                    if (folded is not null) { stack.Add(folded); continue; }

                    if (code == 0x73 || !signature.Value.ReturnsVoid) stack.Add(null);
                    continue;
                }

                case 0x29:                                 // calli — the callee is not named
                case 0x2A:                                 // ret
                    stack.Clear();
                    continue;
            }

            // Everything else moves the stack by an amount the runtime's own table knows.
            // Modelling it is what keeps the arguments to the call we care about in the
            // slots they were pushed into.
            var (pop, push) = Operands.Effect(code);
            if (pop < 0 || push < 0) { stack.Clear(); continue; }

            Pop(stack, pop);
            for (var p = 0; p < push; p++) stack.Add(null);
        }
    }

    /// <summary>
    /// Whether a call is one of these methods on that type. Matched on the declaring type
    /// as well as the name, because plenty of things are called Get.
    /// </summary>
    private static bool IsCall(MetadataReader md, int token, string type, params string[] methods)
    {
        try
        {
            if (MetadataTokens.EntityHandle(token) is not { Kind: HandleKind.MemberReference } h)
                return false;

            var reference = md.GetMemberReference((MemberReferenceHandle)h);
            if (!methods.Contains(md.GetString(reference.Name), StringComparer.Ordinal)) return false;

            return reference.Parent.Kind == HandleKind.TypeReference
                   && md.GetString(md.GetTypeReference((TypeReferenceHandle)reference.Parent).Name) == type;
        }
        catch (Exception e) when (e is BadImageFormatException or ArgumentOutOfRangeException
                                      or InvalidCastException)
        {
            return false;
        }
    }

    /// <summary>The first of the top <paramref name="count"/> entries, if it is a known string.</summary>
    private static string? FirstString(List<object?> stack, int count)
    {
        if (count < 1 || stack.Count < count) return null;
        return stack[stack.Count - count] as string;
    }

    /// <summary>
    /// The joined value of the top <paramref name="count"/> stack entries, or null unless
    /// every one of them is a string this walk knows. An array overload of Concat pushes
    /// one unknown, which is the answer it deserves.
    /// </summary>
    private static string? Join(List<object?> stack, int count)
    {
        if (count is < 1 or > 4 || stack.Count < count) return null;

        var parts = new string[count];

        for (var i = 0; i < count; i++)
        {
            if (stack[stack.Count - count + i] is not string part) return null;
            parts[i] = part;
        }

        return string.Concat(parts);
    }

    /// <summary>
    /// Records what a type initialiser put in a static field. A second, different value
    /// makes it unknown: this walk does not follow branches, so it cannot say which of two
    /// assignments is the one that runs.
    /// </summary>
    private static void Assigned(Dictionary<int, object?> stores, int field, object? value)
    {
        if (stores.TryGetValue(field, out var existing))
        {
            if (!Equals(existing, value)) stores[field] = null;
            return;
        }

        stores[field] = value;
    }

    private static object? Local(Dictionary<int, object?> locals, int slot) =>
        locals.TryGetValue(slot, out var value) ? value : null;

    private static void Stored(Dictionary<int, object?> locals, int slot, List<object?> stack)
    {
        var value = stack.Count > 0 ? stack[^1] : null;

        // Assigned twice with different values: which one runs depends on a branch this
        // walk did not follow, so neither is an answer.
        if (locals.TryGetValue(slot, out var existing) && !Equals(existing, value))
            locals[slot] = null;
        else
            locals[slot] = value;

        Pop(stack, 1);
    }

    private static void Pop(List<object?> stack, int count)
    {
        var take = Math.Min(count, stack.Count);
        stack.RemoveRange(stack.Count - take, take);
    }

    /// <summary>
    /// Turns the top of the stack into a registration. The arguments are the last
    /// <paramref name="arity"/> values pushed, in order — read without disturbing the
    /// stack, which the caller unwinds by the signature.
    /// </summary>
    private static void Take(
        List<object?> stack, int arity, List<HotkeyRegistration> found, ref int unreadable)
    {
        var args = new object?[arity];
        for (var a = 0; a < arity; a++)
        {
            var at = stack.Count - arity + a;
            args[a] = at >= 0 && at < stack.Count ? stack[at] : null;
        }

        // (string hotkeyCode, string name, GlKeys key, HotkeyType type, bool alt, bool ctrl, bool shift)
        if (args.Length < 3 || args[0] is not string code || code.Length == 0)
        {
            unreadable++;
            return;
        }

        var name = args[1] as string;
        var key = args[2] as int?;

        var kind = args.Length > 3 && args[3] is int t && t is >= 0 and <= 9
            ? (HotkeyKind)t
            : HotkeyKind.Unknown;

        // A key that was computed leaves the hotkey with no default, which is a fact about
        // that registration and not a registration we missed. It is not counted here: the
        // caller has the entry, can show it, and can bind it — counting it would put the
        // same hotkey both on screen and in the tally of what is not.
        var binding = key is { } k && GlKeys.IsKnown(k)
            ? new KeyBinding(k,
                Ctrl: args.Length > 5 && args[5] is 1,
                Alt: args.Length > 4 && args[4] is 1,
                Shift: args.Length > 6 && args[6] is 1)
            : null;

        found.Add(new HotkeyRegistration(code, name, binding, kind));
    }

    private static int Operand32(byte[] il, int at) => BitConverter.ToInt32(il, at);

    private static string? UserString(MetadataReader md, int token)
    {
        if ((token & 0xFF000000) != 0x70000000) return null;

        try
        {
            return md.GetUserString(MetadataTokens.UserStringHandle(token & 0xFFFFFF));
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>How many arguments a called method takes, and what it leaves behind.</summary>
    private readonly record struct Signature(int Parameters, bool IsInstance, bool ReturnsVoid);

    private static class Signatures
    {
        /// <summary>
        /// Resolved per assembly read. A method body calls the same handful of things over
        /// and over, and decoding a signature blob per call site is the one part of this
        /// that would show up in a scan of seventy zips.
        /// </summary>
        [ThreadStatic] private static Dictionary<int, Signature?>? _cache;
        [ThreadStatic] private static MetadataReader? _for;

        public static Signature? Of(MetadataReader md, int token)
        {
            if (!ReferenceEquals(_for, md)) { _for = md; _cache = []; }
            if (_cache!.TryGetValue(token, out var cached)) return cached;

            var resolved = Resolve(md, token);
            _cache[token] = resolved;
            return resolved;
        }

        private static Signature? Resolve(MetadataReader md, int token)
        {
            try
            {
                var handle = MetadataTokens.EntityHandle(token);

                // A generic call goes through a MethodSpecification, whose own signature is
                // the type arguments — the parameters are on the method it instantiates.
                if (handle.Kind == HandleKind.MethodSpecification)
                {
                    var spec = md.GetMethodSpecification((MethodSpecificationHandle)handle);
                    handle = spec.Method;
                }

                return handle.Kind switch
                {
                    HandleKind.MethodDefinition => From(
                        md.GetMethodDefinition((MethodDefinitionHandle)handle)
                          .DecodeSignature(VoidSpotter.Instance, null)),

                    HandleKind.MemberReference => From(
                        md.GetMemberReference((MemberReferenceHandle)handle)
                          .DecodeMethodSignature(VoidSpotter.Instance, null)),

                    _ => null,
                };
            }
            catch (Exception e) when (e is BadImageFormatException or ArgumentOutOfRangeException
                                          or InvalidCastException or InvalidOperationException)
            {
                return null;
            }

            static Signature From(MethodSignature<int> s) =>
                new(s.ParameterTypes.Length, s.Header.IsInstance, s.ReturnType == VoidSpotter.Void);
        }
    }

    /// <summary>
    /// How long each instruction is and what it does to the stack, taken from the runtime's
    /// own opcode table rather than written out here. A hand-copied table with one wrong
    /// entry desynchronises the walk, and every instruction after it is read out of the
    /// middle of an operand.
    /// </summary>
    private static class Operands
    {
        private static readonly int[] OneByte = new int[256];
        private static readonly int[] TwoByte = new int[256];
        private static readonly (int Pop, int Push)[] OneByteEffect = new (int, int)[256];
        private static readonly (int Pop, int Push)[] TwoByteEffect = new (int, int)[256];

        static Operands()
        {
            Array.Fill(OneByte, -1);
            Array.Fill(TwoByte, -1);
            Array.Fill(OneByteEffect, (-1, -1));
            Array.Fill(TwoByteEffect, (-1, -1));

            foreach (var field in typeof(OpCodes).GetFields())
            {
                if (field.GetValue(null) is not OpCode op) continue;

                var size = op.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI
                        or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => -2,          // 4 + 4n, handled at the call site
                    _ => 4,
                };

                var effect = (Pop(op.StackBehaviourPop), Push(op.StackBehaviourPush));

                var value = (ushort)op.Value;
                if (value <= 0xFF) { OneByte[value] = size; OneByteEffect[value] = effect; }
                else { TwoByte[value & 0xFF] = size; TwoByteEffect[value & 0xFF] = effect; }
            }

            static int Pop(StackBehaviour b) => b switch
            {
                StackBehaviour.Pop0 => 0,
                StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
                StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi
                    or StackBehaviour.Popi_popi8 or StackBehaviour.Popi_popr4
                    or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1
                    or StackBehaviour.Popref_popi => 2,
                StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi
                    or StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4
                    or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref
                    or StackBehaviour.Popref_popi_pop1 => 3,
                _ => -1,                                     // Varpop: the caller resolves it
            };

            static int Push(StackBehaviour b) => b switch
            {
                StackBehaviour.Push0 => 0,
                StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8
                    or StackBehaviour.Pushr4 or StackBehaviour.Pushr8
                    or StackBehaviour.Pushref => 1,
                StackBehaviour.Push1_push1 => 2,
                _ => -1,
            };
        }

        /// <summary>What one instruction takes and leaves; -1 either side means "cannot say".</summary>
        public static (int Pop, int Push) Effect(int code) =>
            code > 0xFF ? TwoByteEffect[code & 0xFF] : OneByteEffect[code];

        /// <summary>Advances past one instruction. Code is -1 when the byte is not an opcode.</summary>
        public static (int Code, int Size) Read(byte[] il, ref int i)
        {
            var b = il[i];
            var size = b == 0xFE
                ? (i + 1 < il.Length ? TwoByte[il[i + 1]] : -1)
                : OneByte[b];

            if (size == -1) { i = il.Length; return (-1, 0); }

            var code = b == 0xFE ? 0xFE00 | il[i + 1] : b;
            var header = b == 0xFE ? 2 : 1;

            if (size == -2)                                  // switch
            {
                if (i + header + 4 > il.Length) { i = il.Length; return (-1, 0); }
                var count = BitConverter.ToInt32(il, i + header);
                if (count < 0 || count > (il.Length - i) / 4) { i = il.Length; return (-1, 0); }
                size = 4 + count * 4;
            }

            i += header + size;
            return i <= il.Length ? (code, size) : (-1, 0);
        }
    }

    /// <summary>
    /// The signature decoder exists to count parameters and to notice a void return, so
    /// every type it is asked about answers the same thing — except <c>void</c>, which is
    /// the difference between a call that leaves a value on the stack and one that does not.
    /// </summary>
    private sealed class VoidSpotter : ISignatureTypeProvider<int, object?>
    {
        public const int Void = 1;

        public static readonly VoidSpotter Instance = new();

        public int GetPrimitiveType(PrimitiveTypeCode code) =>
            code == PrimitiveTypeCode.Void ? Void : 0;

        public int GetArrayType(int t, ArrayShape s) => 0;
        public int GetByReferenceType(int t) => 0;
        public int GetFunctionPointerType(MethodSignature<int> s) => 0;
        public int GetGenericInstantiation(int t, ImmutableArray<int> a) => 0;
        public int GetGenericMethodParameter(object? c, int index) => 0;
        public int GetGenericTypeParameter(object? c, int index) => 0;
        public int GetModifiedType(int modifier, int unmodified, bool isRequired) => 0;
        public int GetPinnedType(int t) => 0;
        public int GetPointerType(int t) => 0;
        public int GetSZArrayType(int t) => 0;
        public int GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte rawKind) => 0;
        public int GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte rawKind) => 0;
        public int GetTypeFromSpecification(MetadataReader r, object? c, TypeSpecificationHandle h, byte rawKind) => 0;
    }
}
