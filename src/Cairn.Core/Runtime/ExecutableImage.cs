using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Cairn.Core.Runtime;

public enum ExecutableArch
{
    Unknown,
    X86,
    X64,
    Arm64,
}

/// <summary>
/// Reads the target architecture straight out of a native executable header.
///
/// Needed because a machine's .NET and the client it is asked to host need not agree. The
/// game shipped as an x64 apphost everywhere until 1.22, which added a native mac-arm64
/// client — so an Apple Silicon machine can now hold an x64 install, an arm64 install, or
/// both, alongside .NET of either architecture. Pointing an x64 game at an arm64 runtime
/// fails in a way that reads as "the game is broken", so Cairn checks instead of assuming.
/// </summary>
public static class ExecutableImage
{
    /// <summary>
    /// What this machine runs natively, which is not always what this process is.
    ///
    /// OSArchitecture rather than ProcessArchitecture: Cairn's own x64 build launched on
    /// Apple Silicon runs under Rosetta, and every caller here is asking what the machine
    /// can run — which client to install, which client to build — not what Cairn happens to
    /// have been compiled for. Answering with the process would install an emulated game on
    /// a machine that can run a native one.
    /// </summary>
    public static ExecutableArch NativeArchitecture => RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => ExecutableArch.Arm64,
        Architecture.X86 => ExecutableArch.X86,
        _ => ExecutableArch.X64,
    };

    // Mach-O
    private const uint MachMagic64 = 0xFEEDFACF;
    private const uint MachMagic32 = 0xFEEDFACE;
    private const uint MachFatMagic = 0xCAFEBABE;
    private const int CpuTypeX86 = 7;
    private const int CpuTypeX86_64 = 0x0100_0007;
    private const int CpuTypeArm64 = 0x0100_000C;

    public static ExecutableArch ReadArchitecture(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[64];
            var read = fs.ReadAtLeast(head, 8, throwOnEndOfStream: false);
            if (read < 8) return ExecutableArch.Unknown;

            // Mach-O (macOS)
            var le = BinaryPrimitives.ReadUInt32LittleEndian(head);
            if (le is MachMagic64 or MachMagic32)
                return FromMachCpuType(BinaryPrimitives.ReadInt32LittleEndian(head[4..]));

            // Universal ("fat") binary: header is big-endian; report the first slice.
            if (BinaryPrimitives.ReadUInt32BigEndian(head) == MachFatMagic)
            {
                if (read < 16) return ExecutableArch.Unknown;
                return FromMachCpuType(BinaryPrimitives.ReadInt32BigEndian(head[8..]));
            }

            // PE (Windows)
            if (head[0] == (byte)'M' && head[1] == (byte)'Z')
                return ReadPortableExecutable(fs);

            // ELF (Linux)
            if (head[0] == 0x7F && head[1] == (byte)'E' && head[2] == (byte)'L' && head[3] == (byte)'F')
                return ReadElf(fs, littleEndian: head[5] == 1);

            return ExecutableArch.Unknown;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ExecutableArch.Unknown;
        }
    }

    private static ExecutableArch FromMachCpuType(int cpuType) => cpuType switch
    {
        CpuTypeX86_64 => ExecutableArch.X64,
        CpuTypeArm64 => ExecutableArch.Arm64,
        CpuTypeX86 => ExecutableArch.X86,
        _ => ExecutableArch.Unknown,
    };

    private static ExecutableArch ReadPortableExecutable(FileStream fs)
    {
        Span<byte> buf = stackalloc byte[4];

        fs.Position = 0x3C;
        if (fs.ReadAtLeast(buf, 4, throwOnEndOfStream: false) < 4) return ExecutableArch.Unknown;
        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(buf);
        if (peOffset <= 0 || peOffset > fs.Length - 6) return ExecutableArch.Unknown;

        fs.Position = peOffset;
        if (fs.ReadAtLeast(buf, 4, throwOnEndOfStream: false) < 4) return ExecutableArch.Unknown;
        if (buf[0] != (byte)'P' || buf[1] != (byte)'E' || buf[2] != 0 || buf[3] != 0)
            return ExecutableArch.Unknown;

        Span<byte> machine = stackalloc byte[2];
        if (fs.ReadAtLeast(machine, 2, throwOnEndOfStream: false) < 2) return ExecutableArch.Unknown;

        return BinaryPrimitives.ReadUInt16LittleEndian(machine) switch
        {
            0x8664 => ExecutableArch.X64,
            0xAA64 => ExecutableArch.Arm64,
            0x014C => ExecutableArch.X86,
            _ => ExecutableArch.Unknown,
        };
    }

    private static ExecutableArch ReadElf(FileStream fs, bool littleEndian)
    {
        Span<byte> machine = stackalloc byte[2];
        fs.Position = 0x12;
        if (fs.ReadAtLeast(machine, 2, throwOnEndOfStream: false) < 2) return ExecutableArch.Unknown;

        var value = littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(machine)
            : BinaryPrimitives.ReadUInt16BigEndian(machine);

        return value switch
        {
            0x3E => ExecutableArch.X64,
            0xB7 => ExecutableArch.Arm64,
            0x03 => ExecutableArch.X86,
            _ => ExecutableArch.Unknown,
        };
    }
}
