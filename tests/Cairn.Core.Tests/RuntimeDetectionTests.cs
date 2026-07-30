using System.Runtime.InteropServices;
using Cairn.Core.Runtime;
using Xunit;

namespace Cairn.Core.Tests;

public class ExecutableImageTests
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "Cairn-exeimg-" + Guid.NewGuid().ToString("n")[..8]);

    private string Write(string name, byte[] bytes)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Pad(byte[] head, int length = 64)
    {
        var buf = new byte[Math.Max(length, head.Length)];
        head.CopyTo(buf, 0);
        return buf;
    }

    [Fact]
    public void Reads_macho_x64()
    {
        // Exactly the bytes at the head of the shipped game binary: cf fa ed fe, then
        // cputype 0x01000007 little-endian.
        var path = Write("macho-x64", Pad([0xCF, 0xFA, 0xED, 0xFE, 0x07, 0x00, 0x00, 0x01]));
        Assert.Equal(ExecutableArch.X64, ExecutableImage.ReadArchitecture(path));
    }

    [Fact]
    public void Reads_macho_arm64()
    {
        var path = Write("macho-arm64", Pad([0xCF, 0xFA, 0xED, 0xFE, 0x0C, 0x00, 0x00, 0x01]));
        Assert.Equal(ExecutableArch.Arm64, ExecutableImage.ReadArchitecture(path));
    }

    [Fact]
    public void Reads_universal_binary_first_slice()
    {
        // FAT_MAGIC and the following fields are big-endian.
        var path = Write("macho-fat", Pad([
            0xCA, 0xFE, 0xBA, 0xBE,           // FAT_MAGIC
            0x00, 0x00, 0x00, 0x02,           // 2 slices
            0x01, 0x00, 0x00, 0x07,           // slice 0 cputype = x86_64
        ]));
        Assert.Equal(ExecutableArch.X64, ExecutableImage.ReadArchitecture(path));
    }

    [Fact]
    public void Reads_pe_x64()
    {
        var buf = new byte[256];
        buf[0] = (byte)'M'; buf[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(buf, 0x3C);      // e_lfanew
        buf[0x80] = (byte)'P'; buf[0x81] = (byte)'E';
        BitConverter.GetBytes((ushort)0x8664).CopyTo(buf, 0x84);

        var path = Write("pe-x64", buf);
        Assert.Equal(ExecutableArch.X64, ExecutableImage.ReadArchitecture(path));
    }

    [Fact]
    public void Reads_elf_x64()
    {
        var buf = new byte[64];
        buf[0] = 0x7F; buf[1] = (byte)'E'; buf[2] = (byte)'L'; buf[3] = (byte)'F';
        buf[5] = 1;                                          // little-endian
        BitConverter.GetBytes((ushort)0x3E).CopyTo(buf, 0x12);

        var path = Write("elf-x64", buf);
        Assert.Equal(ExecutableArch.X64, ExecutableImage.ReadArchitecture(path));
    }

    [Fact]
    public void Unrecognised_and_missing_files_are_Unknown_not_exceptions()
    {
        Assert.Equal(ExecutableArch.Unknown, ExecutableImage.ReadArchitecture(Write("text", "hello world"u8.ToArray())));
        Assert.Equal(ExecutableArch.Unknown, ExecutableImage.ReadArchitecture(Write("tiny", [0x01])));
        Assert.Equal(ExecutableArch.Unknown,
            ExecutableImage.ReadArchitecture(Path.Combine(_dir, "does-not-exist")));
    }

    [Fact]
    public void Agrees_with_the_host_architecture_on_a_real_binary()
    {
        // A real system binary is a sanity check that the parser is not merely
        // self-consistent with its own synthetic fixtures.
        var probe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Environment.ProcessPath
            : "/bin/sh";

        if (probe is null || !File.Exists(probe)) return;

        var arch = ExecutableImage.ReadArchitecture(probe);
        Assert.NotEqual(ExecutableArch.Unknown, arch);

        // /bin/sh on macOS is universal, so only assert it is one of the sane values.
        Assert.Contains(arch, new[] { ExecutableArch.X64, ExecutableArch.Arm64, ExecutableArch.X86 });
    }
}

public class DotnetRuntimeLocatorTests
{
    [Fact]
    public void Inspect_rejects_a_directory_that_is_not_a_dotnet_root()
    {
        Assert.Null(DotnetRuntimeLocator.Inspect(Path.GetTempPath()));
        Assert.Null(DotnetRuntimeLocator.Inspect("/definitely/not/here"));
        Assert.Null(DotnetRuntimeLocator.Inspect(""));
    }

    [Fact]
    public void Inspect_reads_frameworks_from_a_synthetic_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "Cairn-fakeroot-" + Guid.NewGuid().ToString("n")[..8]);
        var shared = Path.Combine(root, "shared", "Microsoft.NETCore.App");
        Directory.CreateDirectory(Path.Combine(shared, "10.0.10"));
        Directory.CreateDirectory(Path.Combine(shared, "9.0.18"));
        Directory.CreateDirectory(Path.Combine(shared, "10.1.0-rc.1")); // pre-release suffix stripped

        try
        {
            var runtime = DotnetRuntimeLocator.Inspect(root);
            Assert.NotNull(runtime);
            Assert.Contains(new Version(10, 0, 10), runtime!.Frameworks);
            Assert.Contains(new Version(9, 0, 18), runtime.Frameworks);
            Assert.Contains(new Version(10, 1, 0), runtime.Frameworks);

            // No dotnet host in the fixture, so architecture is unknown rather than wrong.
            Assert.Equal(ExecutableArch.Unknown, runtime.Arch);

            Assert.True(runtime.Satisfies(new Version(10, 0, 0)));
            Assert.False(runtime.Satisfies(new Version(11, 0, 0)));
            Assert.Equal(new Version(10, 1, 0), runtime.Best(new Version(10, 0, 0)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Satisfies_does_not_roll_forward_across_majors()
    {
        var runtime = new DotnetRuntime("/x", ExecutableArch.X64, [new Version(9, 0, 18)]);

        // A net10.0 app cannot run on 9.x, so a 9-only install must not count.
        Assert.False(runtime.Satisfies(new Version(10, 0, 0)));
        Assert.True(runtime.Satisfies(new Version(9, 0, 0)));
    }

    [Fact]
    public void Candidate_roots_prefer_an_explicit_root()
    {
        var roots = DotnetRuntimeLocator.CandidateRoots("/my/private/dotnet").ToList();
        Assert.Equal("/my/private/dotnet", roots[0]);
    }

    [Fact]
    public void Find_locates_the_real_runtime_on_this_machine_when_present()
    {
        // Informational rather than strict: CI may have no .NET install layout at all.
        var found = DotnetRuntimeLocator.Find(
            RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
                ? ExecutableArch.Arm64
                : ExecutableArch.X64,
            new Version(10, 0, 0));

        if (found is not null)
        {
            Assert.True(found.Satisfies(new Version(10, 0, 0)));
            Assert.NotEmpty(found.Frameworks);
        }
    }
}
