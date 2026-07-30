using System.Diagnostics;
using Cairn.Core.Runtime;

namespace Cairn.Core.Launch;

public sealed class LaunchOptions
{
    /// <summary>
    /// Shared across packs on purpose. Login state lives in clientsettings.json inside
    /// the data path (Sessionkey, SessionSignature, MpToken, PlayerUID), along with
    /// keybinds, graphics settings and saves. Giving each pack its own data path would
    /// force a separate login per pack, so packs differ by mod path instead.
    /// </summary>
    public string? DataPath { get; set; }

    /// <summary>
    /// Mod directories to stack, in order. --addModPath is additive: the game always
    /// also searches &lt;install&gt;/Mods and &lt;dataPath&gt;/Mods, and there is no way to
    /// switch those off, so keep dataPath/Mods empty or treat it as an always-on layer.
    /// </summary>
    public List<string> ModPaths { get; set; } = [];

    /// <summary>"host:port" — joins the server directly, skipping the main menu.</summary>
    public string? Connect { get; set; }

    public string? OpenWorld { get; set; }
    public bool TraceLog { get; set; }

    /// <summary>
    /// A .NET root Cairn manages itself, tried before anything on the machine. Lets a
    /// private runtime satisfy the game without a system-wide .NET install.
    /// </summary>
    public string? PreferredDotnetRoot { get; set; }
}

/// <summary>Why the game will or will not find a runtime.</summary>
public sealed record RuntimeResolution(DotnetRuntime? Runtime, ExecutableArch GameArch, Version Required)
{
    public bool Resolved => Runtime is not null;

    public bool ArchMismatch => Runtime is not null
                                && Runtime.Arch != ExecutableArch.Unknown
                                && Runtime.Arch != GameArch;

    public string Describe() => Runtime is null
        ? $"No {Describe(GameArch)} .NET {Required.Major} runtime found. The game bundles no "
          + "runtime, so it cannot start until one is installed."
        : $".NET {Runtime.Best(Required)?.ToString() ?? "?"} ({Describe(Runtime.Arch)}) at {Runtime.Root}";

    private static string Describe(ExecutableArch arch) => arch switch
    {
        ExecutableArch.X64 => "x64",
        ExecutableArch.Arm64 => "arm64",
        ExecutableArch.X86 => "x86",
        _ => "unknown-architecture",
    };
}

public sealed class GameLauncher(GameInstall install)
{
    public IReadOnlyList<string> BuildArguments(LaunchOptions options)
    {
        var args = new List<string>();

        if (!string.IsNullOrWhiteSpace(options.DataPath))
        {
            args.Add("--dataPath");
            args.Add(options.DataPath);
        }

        foreach (var modPath in options.ModPaths)
        {
            args.Add("--addModPath");
            args.Add(modPath);
        }

        if (!string.IsNullOrWhiteSpace(options.Connect))
        {
            args.Add("--connect");
            args.Add(options.Connect);
        }

        if (!string.IsNullOrWhiteSpace(options.OpenWorld))
        {
            args.Add("--openWorld");
            args.Add(options.OpenWorld);
        }

        if (options.TraceLog) args.Add("--traceLog");

        return args;
    }

    /// <summary>Which runtime the game would use, without launching it.</summary>
    public RuntimeResolution ResolveRuntime(LaunchOptions? options = null) =>
        new(DotnetRuntimeLocator.Find(
                install.Architecture, install.RequiredFramework, options?.PreferredDotnetRoot),
            install.Architecture,
            install.RequiredFramework);

    public ProcessStartInfo BuildStartInfo(LaunchOptions options)
    {
        var psi = new ProcessStartInfo
        {
            FileName = install.Executable,
            WorkingDirectory = install.Directory,
            UseShellExecute = false,
        };

        foreach (var a in BuildArguments(options)) psi.ArgumentList.Add(a);

        ApplyRuntimeEnvironment(psi, options);
        return psi;
    }

    /// <summary>
    /// Points the game at an architecture-matched runtime.
    ///
    /// Both DOTNET_ROOT and the architecture-specific DOTNET_ROOT_&lt;ARCH&gt; are set to the
    /// same root. The arch-specific one takes precedence for an apphost, so setting only
    /// DOTNET_ROOT would lose to a stale DOTNET_ROOT_X64 inherited from the user's shell;
    /// setting both makes precedence irrelevant.
    ///
    /// When no suitable runtime is found we deliberately set nothing. hostfxr falls back
    /// to the machine's registered install when DOTNET_ROOT holds no usable framework, so
    /// writing a bad value cannot help and clearing a good one could hurt.
    /// </summary>
    private void ApplyRuntimeEnvironment(ProcessStartInfo psi, LaunchOptions options)
    {
        var resolution = ResolveRuntime(options);
        if (resolution.Runtime is null) return;

        psi.Environment["DOTNET_ROOT"] = resolution.Runtime.Root;

        var archSpecific = install.Architecture switch
        {
            ExecutableArch.X64 => "DOTNET_ROOT_X64",
            ExecutableArch.Arm64 => "DOTNET_ROOT_ARM64",
            ExecutableArch.X86 => "DOTNET_ROOT_X86",
            _ => null,
        };

        if (archSpecific is not null) psi.Environment[archSpecific] = resolution.Runtime.Root;
    }

    public Process Launch(LaunchOptions options)
        => Process.Start(BuildStartInfo(options))
           ?? throw new InvalidOperationException($"Could not start {install.Executable}.");
}
