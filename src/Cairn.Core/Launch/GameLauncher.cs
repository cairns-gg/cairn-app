using System.Diagnostics;
using Cairn.Core.Packs;
using Cairn.Core.Runtime;

namespace Cairn.Core.Launch;

public sealed class LaunchOptions
{
    /// <summary>
    /// Normally a pack's own directory, so its worlds and mod configs stay apart from
    /// every other pack's — Saves, ModConfig, Playerdata and ModsByServer all live under
    /// here, and the game gives no way to relocate them individually.
    ///
    /// Login lives here too, which is why packs once shared one path. PackData carries the
    /// session between packs instead, so separate data paths do not mean separate logins.
    /// </summary>
    public string? DataPath { get; set; }

    /// <summary>
    /// Mod directories to stack, in order. --addModPath is additive: the game always
    /// also searches &lt;install&gt;/Mods and &lt;dataPath&gt;/Mods, and there is no way to
    /// switch those off. With a per-pack data path that is harmless — those directories
    /// are the pack's own — but a pack still sharing one inherits whatever is in it.
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

        // Checked here as well as in the manifest, because this is the boundary rather
        // than the form. A manifest is validated when it is synced; a pack.json edited by
        // hand afterwards, or one written by a future caller that forgets, reaches argv
        // through this method and no other. Dropped rather than refused: a pack whose
        // address is unusable should still start, at the main menu, which is where it would
        // have ended up anyway.
        if (!string.IsNullOrWhiteSpace(options.Connect)
            && ServerAddress.IsValid(options.Connect))
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

    /// <summary>
    /// Which runtime the game would use, without launching it.
    ///
    /// An install's own runtime is offered before Cairn's managed one. A Flatpak ships the
    /// .NET the game was built against and is the runtime it uses when launched the ordinary
    /// way, so preferring a private copy Cairn happened to download would run the game on
    /// something nobody chose for it.
    /// </summary>
    public RuntimeResolution ResolveRuntime(LaunchOptions? options = null) =>
        new(DotnetRuntimeLocator.Find(
                install.Architecture, install.RequiredFramework,
                install.DotnetRoot, options?.PreferredDotnetRoot),
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
           ?? throw new InvalidOperationException(Lang.Get("launch-could-not-start", install.Executable));
}
