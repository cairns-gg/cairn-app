using System.Runtime.InteropServices;
using System.Text.Json;
using Cairn.Core.Runtime;

namespace Cairn.Core.Games.Optimum;

/// <summary>One stage of the build, for a progress line above the log.</summary>
public sealed record OptimumStep(string Phase, string Detail, double? Fraction = null);

/// <summary>
/// Builds Optimum from source and installs it as a variant.
///
/// Optimum is a client fork, not a mod: there is nothing to download, because it is
/// distributed as patches that have to be applied to a decompiled game. That is why this
/// exists — the manual procedure is well beyond what most players can do, and every step of
/// it is mechanical.
///
/// The work is driven through Optimum's own scripts rather than reimplemented. Their
/// bootstrap applies ~95 patches, renames launchers, overlays runtime donors and rebrands a
/// bundle; a second implementation of that in C# would only ever prove it agrees with
/// itself, and the failure mode is a client that looks right and is not — which is exactly
/// the bug that made this feature necessary to verify twice.
/// </summary>
public sealed class OptimumProvisioner
{
    private readonly HttpClient _http;
    private readonly GameStore _games;
    private readonly RuntimeStore _runtimes;
    private readonly string _buildsRoot;

    public OptimumProvisioner(
        HttpClient http, GameStore games, RuntimeStore runtimes, string? buildsRoot = null)
    {
        _http = http;
        _games = games;
        _runtimes = runtimes;
        _buildsRoot = buildsRoot ?? CairnPaths.BuildsRoot;
    }

    /// <summary>The working tree, kept between builds so a rebuild is not a fresh decompile.</summary>
    public string WorkingTree => Path.Combine(_buildsRoot, "optimum");

    /// <summary>
    /// Everything the build printed, kept outside the working tree.
    ///
    /// Outside because the tree is a git checkout the build stages files in, and a log
    /// written inside it would show up as a change to the repository. Kept at all because a
    /// build that failed while nobody was watching is otherwise unexplainable.
    /// </summary>
    public string LogPath => Path.Combine(_buildsRoot, "optimum-build.log");

    /// <summary>What this would cost, without doing any of it. No network, no processes.</summary>
    public OptimumBuildPlan Plan(string gameVersion, OptimumSource? source = null)
    {
        source ??= OptimumSource.Pinned;

        return new OptimumBuildPlan
        {
            Prereqs = OptimumPrereqs.Check(),
            NeedsSdk = FindSdk() is null,
            AlreadyBuilt = _games.Find(source.InstallName) is not null
                           || GameInstall.TryAt(_games.InstallDir(source.InstallName)) is not null,
            Source = source,
            FreeBytes = FreeSpace(_buildsRoot),
        };
    }

    /// <summary>An SDK good enough for Optimum's global.json, Cairn's own or the system's.</summary>
    private DotnetSdk? FindSdk()
    {
        // Cairn's own store first: on a machine with an older system SDK, one Cairn
        // downloaded is the one known to satisfy the pin.
        foreach (var dir in SafeDirectories(_runtimes.Root))
        {
            var candidate = DotnetSdkLocator.Inspect(dir);
            if (candidate?.Satisfies(DotnetSdkLocator.RequiredForOptimum) == true) return candidate;
        }

        return DotnetSdkLocator.Find(DotnetSdkLocator.RequiredForOptimum);
    }

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try
        {
            return Directory.Exists(root) ? Directory.EnumerateDirectories(root) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Free bytes on the volume a path lives on, or -1 when it cannot be read.</summary>
    private static long FreeSpace(string path)
    {
        try
        {
            // The directory need not exist yet; walk up to something that does.
            var probe = Path.GetFullPath(path);
            while (!Directory.Exists(probe))
            {
                var parent = Path.GetDirectoryName(probe);
                if (string.IsNullOrEmpty(parent) || parent == probe) return -1;
                probe = parent;
            }

            return new DriveInfo(Path.GetPathRoot(probe) ?? probe).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return -1;
        }
    }

    /// <summary>
    /// Builds Optimum and installs it, returning the install.
    /// </summary>
    /// <param name="vanilla">
    /// A stock install of the same version, handed to the Windows packager so it overlays
    /// the client Cairn already has rather than downloading a second copy. The other
    /// platforms' packagers fetch their own.
    /// </param>
    public async Task<GameInstall> BuildAsync(
        OptimumSource? source = null,
        GameInstall? vanilla = null,
        IProgress<OptimumStep>? progress = null,
        IProgress<string>? log = null,
        CancellationToken ct = default)
    {
        source ??= OptimumSource.Pinned;

        var plan = Plan(source.GameVersion, source);

        if (!plan.Prereqs.Satisfied) throw new OptimumBuildException(plan.Prereqs.Describe());
        if (!plan.EnoughSpace) throw new OptimumBuildException(plan.Describe());

        Directory.CreateDirectory(_buildsRoot);

        // Every line goes to the file as well as the caller, so a failure is still
        // explainable after the window is gone.
        using var file = new StreamWriter(LogPath, append: true) { AutoFlush = true };
        var both = new Progress<string>(line =>
        {
            lock (file) file.WriteLine(line);
            log?.Report(line);
        });

        lock (file) file.WriteLine($"--- build started for {source.GameVersion} ---");

        var sdk = await EnsureSdkAsync(progress, ct).ConfigureAwait(false);

        await CheckoutAsync(source, progress, both, ct).ConfigureAwait(false);

        // What the checkout says it builds, versus what the pin claims. They disagree when
        // a pin is bumped and the constant is not, and the result would be an install named
        // for one game version holding another.
        var declared = OptimumSource.ReadGameVersion(WorkingTree);
        if (declared is not null && !source.Supports(declared))
            throw new OptimumBuildException(
                $"This Optimum revision builds for Vintage Story {declared}, not "
                + $"{source.GameVersion}. Cairn's pinned revision is out of step with itself.");

        await BootstrapAsync(source, sdk, progress, both, ct).ConfigureAwait(false);
        await CompileAsync(sdk, progress, both, ct).ConfigureAwait(false);

        var packaged = await PackageAsync(source, sdk, vanilla, progress, both, ct)
            .ConfigureAwait(false);

        var install = Place(source, packaged, progress);

        // Only once the client is safely in the library. What is left behind is the
        // redistributable the packager also made — a 700 MB disk image on macOS, a tarball
        // on Linux — which is of no use to Cairn: it installs the directory, not the
        // archive of it. Keeping it doubled the cost of the feature for nothing.
        DiscardPackagerOutput(both);

        progress?.Report(new OptimumStep("ready", $"Optimum {source.Version} is installed.", 1));
        return install;
    }

    private async Task<DotnetSdk> EnsureSdkAsync(
        IProgress<OptimumStep>? progress, CancellationToken ct)
    {
        if (FindSdk() is { } existing)
        {
            progress?.Report(new OptimumStep("sdk", $"using the .NET SDK at {existing.Root}", 0.02));
            return existing;
        }

        progress?.Report(new OptimumStep("sdk", "downloading a .NET SDK", 0.02));

        var rid = DotnetRuntimeInstaller.RidFor(HostArch());
        var installer = new DotnetRuntimeInstaller(_http, _runtimes);

        var release = await installer
            .ResolveSdkAsync(DotnetSdkLocator.RequiredForOptimum.Major, rid, ct)
            .ConfigureAwait(false);

        var relay = new Progress<InstallProgressReport>(p => progress?.Report(
            new OptimumStep("sdk",
                p.Phase == "downloading"
                    ? $".NET SDK {release.Version} — {p.Done / 1024 / 1024} MB"
                    : p.Phase,
                0.02 + (p.Fraction ?? 0) * 0.05)));

        var installed = await installer.InstallAsync(release, relay, ct).ConfigureAwait(false);

        return DotnetSdkLocator.Inspect(installed.Root)
               ?? throw new OptimumBuildException(
                   "The downloaded .NET SDK does not look like an SDK. This is a bug in Cairn.");
    }

    /// <summary>This machine's own architecture — what the SDK must run on, not the game's.</summary>
    private static ExecutableArch HostArch() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => ExecutableArch.Arm64,
        Architecture.X86 => ExecutableArch.X86,
        _ => ExecutableArch.X64,
    };

    /// <summary>
    /// Puts the working tree at the pinned commit, cloning it if it is not there.
    ///
    /// A full clone rather than a shallow one: the repository is a few megabytes, and a
    /// shallow clone cannot check out an arbitrary commit — which is the entire point of
    /// pinning one.
    /// </summary>
    private async Task CheckoutAsync(
        OptimumSource source, IProgress<OptimumStep>? progress, IProgress<string> log,
        CancellationToken ct)
    {
        progress?.Report(new OptimumStep("cloning", $"fetching Optimum {source.Version}", 0.08));

        if (!Directory.Exists(Path.Combine(WorkingTree, ".git")))
        {
            // A directory that is not a checkout is in the way — a half-finished clone from
            // a cancelled run, most likely.
            if (Directory.Exists(WorkingTree)) Directory.Delete(WorkingTree, recursive: true);

            await ProcessRunner.RunOrThrowAsync("git",
                    ["clone", "--quiet", source.Url, WorkingTree],
                    _buildsRoot, log, ct: ct)
                .ConfigureAwait(false);
        }
        else
        {
            await ProcessRunner.RunOrThrowAsync("git",
                    ["-C", WorkingTree, "fetch", "--quiet", "origin"],
                    _buildsRoot, log, ct: ct)
                .ConfigureAwait(false);
        }

        // Hard reset rather than checkout: a previous build stages files into the index as
        // part of applying patches, so an ordinary checkout would refuse or merge.
        await ProcessRunner.RunOrThrowAsync("git",
                ["-C", WorkingTree, "reset", "--hard", "--quiet", source.Ref],
                _buildsRoot, log, ct: ct)
            .ConfigureAwait(false);
    }

    private async Task BootstrapAsync(
        OptimumSource source, DotnetSdk sdk, IProgress<OptimumStep>? progress,
        IProgress<string> log, CancellationToken ct)
    {
        progress?.Report(new OptimumStep("bootstrap",
            "decompiling the game and applying Optimum's patches — this is the long part", 0.15));

        var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var script = Path.Combine(WorkingTree, "scripts", windows ? "bootstrap.ps1" : "bootstrap.sh");

        var (host, args) = ProcessRunner.ScriptHost(script);
        args.AddRange(windows
            ? ["-Version", source.GameVersion]
            : ["--version", source.GameVersion]);

        await ProcessRunner.RunOrThrowAsync(host, args, WorkingTree, log, BuildEnv(sdk), ct)
            .ConfigureAwait(false);
    }

    private async Task CompileAsync(
        DotnetSdk sdk, IProgress<OptimumStep>? progress, IProgress<string> log,
        CancellationToken ct)
    {
        progress?.Report(new OptimumStep("building", "compiling the patched client", 0.6));

        await ProcessRunner.RunOrThrowAsync(sdk.Executable,
                ["build", "VintageStory.slnx", "-c", "Release", "--nologo"],
                WorkingTree, log, BuildEnv(sdk), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Where the packager assembles its output before Cairn takes the client from it.</summary>
    public string PackagerOutput => Path.Combine(_buildsRoot, "optimum-out");

    /// <summary>
    /// Removes the packager's leftovers.
    ///
    /// Never fatal. The build has succeeded by the time this runs, and failing it over a
    /// file that would not delete would throw away a client that took twenty minutes to
    /// make in order to reclaim disk.
    /// </summary>
    private void DiscardPackagerOutput(IProgress<string>? log)
    {
        try
        {
            if (Directory.Exists(PackagerOutput)) Directory.Delete(PackagerOutput, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.Report($"could not remove {PackagerOutput}: {e.Message}");
        }
    }

    /// <summary>
    /// Deletes the working tree and anything left beside it, returning the bytes freed.
    ///
    /// Separate from the build because the tree is worth keeping by default: it is what
    /// makes a rebuild minutes rather than the full decompile again. It is also several
    /// gigabytes sitting idle between pin bumps, so there has to be a way to say no.
    /// </summary>
    public long Clean()
    {
        var freed = 0L;

        foreach (var path in new[] { WorkingTree, PackagerOutput })
        {
            if (!Directory.Exists(path)) continue;

            freed += DirectorySize(path);

            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new OptimumBuildException($"Could not remove {path}: {e.Message}", e);
            }
        }

        return freed;
    }

    private static long DirectorySize(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(f => { try { return new FileInfo(f).Length; } catch (IOException) { return 0; } });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Which packager to drive. A parameter so all three can be tested from one host.</summary>
    public enum BuildPlatform { Windows, MacOS, Linux }

    public static BuildPlatform HostPlatform =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? BuildPlatform.Windows
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? BuildPlatform.MacOS
        : BuildPlatform.Linux;

    /// <summary>
    /// The packager script and its arguments, for one platform.
    ///
    /// Separated from running it because the interesting part is which value goes in which
    /// argument, and that cannot be checked by running a packager on a host that only has
    /// one of the three. Every script here takes a version and means the <em>Vintage
    /// Story</em> version by it — it is what builds the client download URL, and each
    /// script defaults it from forks.json. Optimum's own version is read from its VERSION
    /// file and is never passed in. Handing over Optimum's version instead asks the CDN for
    /// a client release numbered 0.3.5 and gets a 404, twenty minutes into a build that had
    /// otherwise succeeded.
    /// </summary>
    public static (string Script, List<string> Arguments) PackagerFor(
        OptimumSource source,
        string outputDir,
        GameInstall? vanilla = null,
        BuildPlatform? platform = null,
        bool arm64 = false)
    {
        switch (platform ?? HostPlatform)
        {
            case BuildPlatform.Windows:
                List<string> windows = ["-OutputDir", outputDir, "-Version", source.GameVersion];

                // Reuses the client Cairn already downloaded and unpacked. Without this the
                // packager fetches its own copy of a game that is already on the disk.
                if (vanilla is not null) windows.AddRange(["-VanillaDir", vanilla.Directory]);

                return ("package.ps1", windows);

            case BuildPlatform.MacOS:
                return ("package-macos.sh",
                    [
                        "--arch", arm64 ? "arm64" : "x64",
                        "--output", outputDir,
                        "--version", source.GameVersion,
                    ]);

            default:
                return ("package-linux.sh",
                    ["--output", outputDir, "--version", source.GameVersion]);
        }
    }

    /// <summary>
    /// Runs the platform's packager and returns the directory it assembled.
    ///
    /// Each platform's packager emits something different — Windows a folder, Linux a
    /// tarball, macOS a disk image — but all three assemble a client directory first, so
    /// the output is found by looking for one rather than by knowing each script's naming.
    /// </summary>
    private async Task<string> PackageAsync(
        OptimumSource source, DotnetSdk sdk, GameInstall? vanilla,
        IProgress<OptimumStep>? progress, IProgress<string> log, CancellationToken ct)
    {
        progress?.Report(new OptimumStep("packaging", "assembling the client", 0.85));

        var output = PackagerOutput;
        if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        Directory.CreateDirectory(output);

        // The machine's architecture, not this process's: the same question GameCatalog
        // asks when it picks a client to download, and it has to be answered the same way.
        // Cairn's own x64 build under Rosetta would otherwise build an x64 client beside
        // the arm64 stock install, needing a second .NET nothing else on the machine wants.
        var (name, packagerArgs) = PackagerFor(
            source, output, vanilla,
            arm64: ExecutableImage.NativeArchitecture == ExecutableArch.Arm64);

        var (host, args) = ProcessRunner.ScriptHost(Path.Combine(WorkingTree, "scripts", name));
        args.AddRange(packagerArgs);

        await ProcessRunner.RunOrThrowAsync(host, args, WorkingTree, log, BuildEnv(sdk), ct)
            .ConfigureAwait(false);

        return FindPackagedClient(output)
               ?? throw new OptimumBuildException(
                   "The build finished but produced no client directory. See " + LogPath);
    }

    /// <summary>
    /// The assembled client under a packager's output directory.
    ///
    /// Identified by what makes a directory a game install rather than by name, because the
    /// three packagers name their output three different ways and one of them is a .app
    /// bundle. Shallow on purpose: the same DLL appears inside staging copies and inside
    /// the .optimum donor directories, and the wanted one is always near the top.
    /// </summary>
    public static string? FindPackagedClient(string outputDir)
    {
        if (!Directory.Exists(outputDir)) return null;

        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((outputDir, 0));

        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();

            if (File.Exists(Path.Combine(dir, "VintagestoryAPI.dll"))) return dir;

            if (depth >= 3) continue;

            foreach (var child in SafeDirectories(dir))
            {
                // Staging leftovers hold a whole second copy of the client.
                var name = Path.GetFileName(child);
                if (name.StartsWith('_') || name is ".optimum") continue;

                queue.Enqueue((child, depth + 1));
            }
        }

        return null;
    }

    /// <summary>
    /// Moves the packaged client into the game library and marks it as a variant.
    /// </summary>
    private GameInstall Place(
        OptimumSource source, string packaged, IProgress<OptimumStep>? progress)
    {
        progress?.Report(new OptimumStep("installing", "putting the client in place", 0.95));

        var target = _games.InstallDir(source.InstallName);

        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        try
        {
            Directory.Move(packaged, target);
        }
        catch (IOException)
        {
            // Different volumes, which happens as soon as CAIRN_HOME is moved off the
            // system disk. Copying is slower and always works.
            CopyTree(packaged, target);
        }

        WriteMarker(target, source);

        return GameInstall.TryAt(target)
               ?? throw new OptimumBuildException(
                   $"The client was built but {target} is not a usable install. See {LogPath}");
    }

    /// <summary>
    /// Writes the variant marker, naming the launcher to run.
    ///
    /// The executable is the whole reason the marker carries more than a label. Optimum's
    /// output is a copy of the vanilla client plus its own launcher, and the vanilla
    /// executable sits right there in the same directory — so an install without this runs
    /// the stock game while every message in Cairn says it is running Optimum.
    /// </summary>
    public static void WriteMarker(string dir, OptimumSource source)
    {
        var launcher = new[] { "Optimum.exe", "Optimum" }
            .FirstOrDefault(name => File.Exists(Path.Combine(dir, name)));

        if (launcher is null)
            throw new OptimumBuildException(
                $"The packaged client has no Optimum launcher in it, so Cairn would run the "
                + $"stock game instead. See {source.Url} — this is a change in its packaging.");

        var marker = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["label"] = "Optimum",
            ["executable"] = launcher,
        });

        File.WriteAllText(Path.Combine(dir, GameInstall.VariantMarker), marker);
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);

        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);

        try { Directory.Delete(from, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// The environment the build runs in.
    ///
    /// The SDK is put on PATH and named by DOTNET_ROOT so every nested tool — bootstrap's
    /// ilspycmd, the packagers' dotnet calls — finds the same one Cairn chose, rather than
    /// a system install that may not satisfy the pin. MSBuild node reuse is off because a
    /// build that leaves daemons behind holds files open in a tree Cairn may be about to
    /// delete.
    /// </summary>
    private static Dictionary<string, string> BuildEnv(DotnetSdk sdk)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";

        return new Dictionary<string, string>
        {
            ["DOTNET_ROOT"] = sdk.Root,
            ["PATH"] = sdk.Root + Path.PathSeparator + path,
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["MSBUILDDISABLENODEREUSE"] = "1",
        };
    }
}
