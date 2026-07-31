using Cairn.Core;
using Cairn.Core.Games;
using Cairn.Core.Launch;
using Cairn.Core.Runtime;
using Cairn.Core.ModDb;
using Cairn.Core.Packs;

namespace Cairn.Cli;

/// <summary>
/// Headless front-end. The GUI drives the same Cairn.Core engine, so anything the
/// launcher can do is scriptable and testable here first.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0) { Usage(); return 1; }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("cairn/0.1 (+https://github.com/dizzyd/cairn)");
        var moddb = new ModDbClient(http);
        var store = new PackStore();
        var games = new GameStore();
        var runtimes = new RuntimeStore();

        try
        {
            return args[0] switch
            {
                "info" => Info(),
                "list" => List(store),
                "init" => Init(store, args),
                "add" => Add(store, args),
                "search" => await Search(moddb, args),
                "remove" => Remove(store, args),
                "delete" => Delete(store, args),
                "export" => Export(store, args),
                "import" => await Import(store, http, args),
                "games" => await Games(games, http, args),
                "runtimes" => await Runtimes(runtimes, http, args),
                "sync" => await Sync(store, moddb, http, args),
                "update" => await Update(store, moddb, http, args),
                "launch" => await LaunchPack(store, games, runtimes, moddb, http, args),
                "-h" or "--help" or "help" => Ok(Usage),
                _ => Fail($"unknown command '{args[0]}'"),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
    }

    private static void Usage()
    {
        Console.WriteLine("""
            cairn-cli - Vintage Story client-side modpack manager

              cairn-cli info                          show the detected install and data path
              cairn-cli list                          list packs
              cairn-cli init <name> [--id <id>] [--game <version>] [--connect host:port]
              cairn-cli add <id> <modid> [version]    add a mod to a pack
              cairn-cli remove <id> <modid>           remove a mod from a pack
              cairn-cli delete <id>                   delete a pack and its mods
              cairn-cli export <id> [-o file] [--no-lock]   write a shareable pack file
              cairn-cli import <file|url> [--id x] [--loose] create a pack from one
              cairn-cli games                         list installed and available game versions
              cairn-cli games install <version>       download and install a game version
              cairn-cli games remove <version>        delete an installed game version
              cairn-cli runtimes                      list .NET runtimes Cairn manages
              cairn-cli runtimes install <major>      download a private .NET runtime (e.g. 8)
              cairn-cli runtimes remove <version>     delete one
              cairn-cli search <text> [--game <version>]  search ModDB
              cairn-cli sync <id>                     install what the lockfile says
              cairn-cli update <id> [modid...]     move followed mods to their newest
              cairn-cli update <id> --check        report updates without installing
              cairn-cli launch <id> [--dry-run] [--no-install]  sync, then start the game

            Packs live under $CAIRN_HOME (default ~/.cairn/packs/<id>).
            """);
    }

    private static int Ok(Action a) { a(); return 0; }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return 1;
    }

    private static GameInstall RequireInstall()
        => GameInstall.TryLocate()
           ?? throw new InvalidOperationException(
               "No Vintage Story install found. Set VINTAGE_STORY to the install directory.");

    private static int Info()
    {
        var install = GameInstall.TryLocate();
        Console.WriteLine($"install     : {install?.Directory ?? "(not found)"}");
        Console.WriteLine($"version     : {install?.Version ?? "-"}");
        Console.WriteLine($"executable  : {install?.Executable ?? "-"}");
        Console.WriteLine($"data path   : {GameInstall.DefaultDataPath}  (yours; packs use it only as a seed)");
        Console.WriteLine($"cairn home  : {CairnPaths.Root}");

        if (install is not null)
        {
            Console.WriteLine($"game arch   : {install.Architecture}");
            Console.WriteLine($"needs .NET  : {install.RequiredFramework}");

            var resolution = new GameLauncher(install).ResolveRuntime();
            Console.WriteLine($"runtime     : {resolution.Describe()}");
            if (resolution.ArchMismatch)
                Console.WriteLine("  warning   : architecture mismatch - the game will not start with this runtime");
        }

        return install is null ? 1 : 0;
    }

    private static int List(PackStore store)
    {
        var ids = store.ListIds().ToList();
        if (ids.Count == 0)
        {
            Console.WriteLine("no packs yet - create one with: cairn-cli init <name>");
            return 0;
        }

        foreach (var id in ids)
        {
            var manifest = store.Load(id);
            var connect = manifest.Connect is null ? "" : $"  -> {manifest.Connect}";
            Console.WriteLine($"  {id,-20} game {manifest.GameVersion,-8} {manifest.Mods.Count,3} mods{connect}");
        }

        return 0;
    }

    private static int Init(PackStore store, string[] args)
    {
        if (args.Length < 2)
            return Fail("usage: cairn-cli init <name> [--id <id>] [--game <version>] [--connect host:port]");

        var name = args[1];
        var gameVersion = ArgValue(args, "--game") ?? GameInstall.TryLocate()?.Version;
        if (gameVersion is null or "unknown")
            return Fail("could not detect the game version; pass --game <version>");

        // Slugging is idempotent, so `cairn-cli init anego` still produces exactly "anego"
        // while `cairn-cli init "Anego Server"` now works instead of being refused.
        var id = ArgValue(args, "--id") ?? store.SuggestId(name);

        var problem = store.DescribeIdProblem(id);
        if (problem is not null) return Fail(problem);

        store.Create(id, gameVersion, name, ArgValue(args, "--connect"));
        Console.WriteLine($"created {store.ManifestPath(id)}  (id {id}, game {gameVersion})");
        return 0;
    }

    private static int Add(PackStore store, string[] args)
    {
        if (args.Length < 3) return Fail("usage: cairn-cli add <id> <modid> [version]");

        var id = args[1];
        var manifest = store.Load(id);
        var modId = args[2];
        var version = args.Length > 3 ? args[3] : null;

        if (manifest.Mods.Any(m => string.Equals(m.ModId, modId, StringComparison.OrdinalIgnoreCase)))
            return Fail($"'{modId}' is already in pack '{id}'");

        manifest.Mods.Add(new PackMod { ModId = modId, Version = version });

        store.Save(manifest);
        Console.WriteLine($"added {modId}{(version is null ? "" : " " + version)} to '{id}'");
        return 0;
    }

    private static async Task<int> Search(ModDbClient moddb, string[] args)
    {
        if (args.Length < 2) return Fail("usage: cairn-cli search <text> [--game <version>]");

        // Defaults to the installed game, so results say what would actually install.
        var gameVersion = ArgValue(args, "--game") ?? GameInstall.TryLocate()?.Version;
        if (gameVersion is "unknown") gameVersion = null;

        var words = args[1..].Where(a => a != "--game" && a != gameVersion).ToArray();
        var query = string.Join(' ', words);

        var results = await moddb.SearchRankedAsync(query, gameVersion);
        if (results.Count == 0) { Console.WriteLine("no results"); return 0; }

        if (results.Count > 20)
            Console.WriteLine($"{results.Count} results — showing the closest 20");

        foreach (var r in results.Take(20))
        {
            var m = r.Mod;
            var idStr = m.ModIdStrs.FirstOrDefault() ?? "?";

            // Listed rather than dropped: knowing a mod exists but has no release yet is
            // more useful than it silently missing from the results.
            var mark = r.Compatible ? " " : "!";

            Console.WriteLine($"{mark} {idStr,-24} {m.Side,-7} {m.Downloads,7:N0} dl  {m.Name}");
            if (!string.IsNullOrWhiteSpace(m.Summary))
                Console.WriteLine($"  {"",-24} {Truncate(m.Summary, 76)}");
        }

        if (gameVersion is not null && results.Any(r => !r.Compatible))
            Console.WriteLine($"\n  ! = no release for game {gameVersion}");

        return 0;
    }

    /// <summary>
    /// Moves followed mods to their newest release. Separate from sync on purpose: sync
    /// installs what the lock says, so launching cannot change a pack underneath a save.
    /// </summary>
    private static async Task<int> Update(PackStore store, ModDbClient moddb, HttpClient http, string[] args)
    {
        if (args.Length < 2) return Fail("usage: cairn-cli update <id> [modid...] [--check]");

        var id = args[1];
        var manifest = store.Load(id);
        var syncer = new PackSyncer(moddb, http);

        var updates = await syncer.CheckUpdatesAsync(manifest, store.LockPath(id));

        if (updates.Count == 0)
        {
            Console.WriteLine("everything is up to date");
            return 0;
        }

        foreach (var u in updates) Console.WriteLine($"  {u.Describe()}");

        if (args.Contains("--check")) return 0;

        // Named mods only, when any are named.
        var named = args[2..].Where(a => !a.StartsWith('-')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wanted = named.Count == 0
            ? updates.Select(u => u.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : named;

        Console.WriteLine();
        var report = await syncer.SyncAsync(
            manifest, store.ModsDir(id), store.LockPath(id),
            new Progress<SyncStep>(s => Console.WriteLine($"  {Describe(s)}")),
            allowUpdates: wanted);

        return report.Failed ? 1 : 0;
    }

    private static async Task<int> Sync(PackStore store, ModDbClient moddb, HttpClient http, string[] args)
    {
        if (args.Length < 2) return Fail("usage: cairn-cli sync <id>");

        var id = args[1];
        var report = await RunSync(store, moddb, http, id);
        return report.Failed ? 1 : 0;
    }

    private static string Describe(SyncStep step)
    {
        var marker = step.Action switch
        {
            SyncAction.Downloaded => "+",
            SyncAction.Updated => "^",
            SyncAction.Removed => "-",
            SyncAction.Unchanged => "=",
            SyncAction.Warned => "!",
            _ => "x",
        };

        return $"{marker} {step.ModId,-22} {step.Detail}";
    }

    private static async Task<SyncReport> RunSync(PackStore store, ModDbClient moddb, HttpClient http, string id)
    {
        var manifest = store.Load(id);
        Console.WriteLine($"syncing '{id}' for game {manifest.GameVersion}");

        var syncer = new PackSyncer(moddb, http);
        var progress = new Progress<SyncStep>(s => Console.WriteLine($"  {Describe(s)}"));

        var report = await syncer.SyncAsync(
            manifest, store.ModsDir(id), store.LockPath(id), progress);

        Console.WriteLine($"  {report.Lock.Mods.Count} mods locked -> {store.LockPath(id)}");
        return report;
    }

    private static async Task<int> LaunchPack(PackStore store, GameStore gameStore, RuntimeStore runtimes, ModDbClient moddb, HttpClient http, string[] args)
    {
        if (args.Length < 2) return Fail("usage: cairn-cli launch <id>");

        var id = args[1];
        var manifest = store.Load(id);

        // A pack names the game version it wants; prefer an install of exactly that.
        var library = new GameLibrary(gameStore, GameInstall.TryLocate());
        var install = library.ForVersion(manifest.GameVersion);

        // Same contract as the launcher: fetch what the pack needs rather than telling
        // the user to go and do it.
        var provisioner = new GameProvisioner(http, gameStore, runtimes);
        var plan = provisioner.Plan(manifest.GameVersion, GameInstall.TryLocate());

        if (plan.AnythingToDo)
        {
            if (args.Contains("--no-install"))
                return Fail(plan.Describe() + " Re-run without --no-install to fetch it.");

            Console.WriteLine(plan.Describe());

            var last = "";
            var progress = new Progress<ProvisionStep>(p =>
            {
                if (p.Fraction is { } f && p.Phase == "downloading")
                    Console.Write($"\r  {p.Detail} ({f * 100,5:F1}%)      ");
                else if (p.Phase != last) Console.WriteLine($"\r  {p.Detail}          ");

                last = p.Phase;
            });

            install = await provisioner.EnsureAsync(
                manifest.GameVersion, GameInstall.TryLocate(), progress);

            install ??= library.ForVersion(manifest.GameVersion);
        }

        // Also covers the case where the plan reported nothing to do but the install
        // still did not resolve; and it satisfies nullable analysis.
        if (install is null)
            return Fail($"Could not prepare Vintage Story {manifest.GameVersion}.");

        Console.WriteLine($"using game {install.Version} at {install.Directory}");

        // A private runtime, when we have one that fits, beats whatever is installed
        // system-wide - and is often the only thing that fits for an older game version.
        var managedRoot = runtimes.RootFor(install);
        var probe = new LaunchOptions { PreferredDotnetRoot = managedRoot };

        var runtime = new GameLauncher(install).ResolveRuntime(probe);
        if (!runtime.Resolved)
            return Fail($"{install.Version} needs .NET {install.RequiredFramework} and none was found. "
                        + $"Install one with: cairn-cli runtimes install {install.RequiredFramework.Major}");

        Console.WriteLine($"using runtime {runtime.Describe()}");

        var report = await RunSync(store, moddb, http, id);
        if (report.Failed)
            return Fail("sync failed; not launching (use --force once implemented to override)");

        // A pack keeps its worlds and configs to itself. Resolving
        // the path is a pure read, so --dry-run can print it; carrying the login into it
        // writes, and waits until we know we are really launching.
        var packData = new PackData(store);

        var launcher = new GameLauncher(install);
        var options = new LaunchOptions
        {
            DataPath = packData.DataPathFor(id),
            ModPaths = { store.ModsDir(id) },
            Connect = manifest.Connect,
            PreferredDotnetRoot = managedRoot,
        };

        Console.WriteLine($"launching: {install.Executable} {string.Join(' ', launcher.BuildArguments(options))}");

        if (args.Contains("--dry-run"))
        {
            var psi = launcher.BuildStartInfo(options);
            foreach (var name in new[] { "DOTNET_ROOT", "DOTNET_ROOT_X64" })
                if (psi.Environment.TryGetValue(name, out var v))
                    Console.WriteLine($"  {name}={v}");

            Console.WriteLine("dry run - not started");
            return 0;
        }

        // Carried in now, so a pack does not ask for a fresh login.
        packData.BeforeLaunch(id);

        var proc = launcher.Launch(options);
        Console.WriteLine($"started pid {proc.Id}");
        return 0;
    }

    private static async Task<int> Games(GameStore games, HttpClient http, string[] args)
    {
        var catalog = new GameCatalog(http);
        var action = args.Length > 1 ? args[1] : "list";

        if (action == "remove")
        {
            if (args.Length < 3) return Fail("usage: cairn-cli games remove <version>");

            // By the install rather than the version, so a directory whose name differs
            // from the version its assembly reports is still the one that goes.
            var found = games.Find(args[2]);
            if (found is null) return Fail($"{args[2]} is not installed by Cairn");

            games.Remove(found);
            Console.WriteLine($"removed game {args[2]}");
            return 0;
        }

        if (action == "install")
        {
            if (args.Length < 3) return Fail("usage: cairn-cli games install <version>");

            var wanted = args[2];
            var releases = await catalog.ListReleasesAsync(includePreReleases: true);
            var release = releases.FirstOrDefault(r => r.Version == wanted);
            if (release is null)
                return Fail($"no {GameCatalog.PlatformKey} download published for {wanted}");

            if (!release.CanInstall)
                return Fail($"{wanted} ships as {release.Artifact.FileName}, which Cairn cannot install");

            Console.WriteLine($"installing {wanted} ({release.Artifact.FileSize})");

            var lastPhase = (InstallPhase)(-1);
            var progress = new Progress<InstallProgress>(p =>
            {
                if (p.Phase == InstallPhase.Downloading)
                {
                    var pct = p.Fraction is { } f ? $"{f * 100,5:F1}%" : "     ?";
                    Console.Write($"\r  downloading {pct}  ({p.Done / 1024 / 1024} MB)   ");
                }
                else if (p.Phase != lastPhase)
                {
                    Console.WriteLine($"\r  {p.Phase.ToString().ToLowerInvariant()}: {p.Detail}          ");
                }

                lastPhase = p.Phase;
            });

            var installer = new GameInstaller(http, games);
            var install = await installer.InstallAsync(release, progress);
            Console.WriteLine($"  installed {install.Version} -> {install.Directory}");
            return 0;
        }

        var installed = games.ListInstalled().ToList();
        Console.WriteLine($"installed ({games.Root}):");
        if (installed.Count == 0) Console.WriteLine("  (none managed by Cairn)");
        foreach (var i in installed)
            Console.WriteLine($"  {i.Version,-12} {i.Architecture,-6} needs .NET {i.RequiredFramework}");

        var system = GameInstall.TryLocate();
        if (system is not null && !installed.Any(i => i.Directory == system.Directory))
            Console.WriteLine($"  {system.Version,-12} {system.Architecture,-6} (pre-existing: {system.Directory})");

        Console.WriteLine();
        Console.WriteLine($"available for {GameCatalog.PlatformKey}:");
        var all = await catalog.ListReleasesAsync();
        foreach (var r in all.Take(12))
        {
            var mark = games.IsInstalled(r.Version) ? "*" : " ";
            var kind = r.CanInstall ? "" : "  (not downloadable)";
            Console.WriteLine($"  {mark} {r.Version,-12} {r.Artifact.FileSize,10}{kind}");
        }

        Console.WriteLine($"  ... {all.Count} versions published");
        return 0;
    }

    private static int Export(PackStore store, string[] args)
    {
        if (args.Length < 2) return Fail("usage: cairn-cli export <id> [-o file] [--no-lock]");

        var id = args[1];
        if (!store.Exists(id)) return Fail($"no pack '{id}'");

        var json = store.Export(id, includeLock: !args.Contains("--no-lock"));
        var output = ArgValue(args, "-o");

        if (output is null)
        {
            Console.WriteLine(json);
            return 0;
        }

        File.WriteAllText(output, json);
        Console.WriteLine($"wrote {output}");
        return 0;
    }

    private static async Task<int> Import(PackStore store, HttpClient http, string[] args)
    {
        if (args.Length < 2) return Fail("usage: cairn-cli import <file|url> [--id x] [--loose]");

        var source = args[1];
        string json;

        try
        {
            // https only: a pack decides which mods get installed, so it must not
            // arrive over a connection anyone on the path can rewrite.
            if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                return Fail("refusing to import over http; use https");

            json = source.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? await http.GetStringAsync(source)
                : File.ReadAllText(source);
        }
        catch (Exception e) when (e is IOException or HttpRequestException)
        {
            return Fail($"could not read '{source}': {e.Message}");
        }

        var bundle = PackBundle.Parse(json);

        // --loose tracks newest-compatible instead of reproducing the author's versions.
        var manifest = store.Import(bundle, ArgValue(args, "--id"), pinToLock: !args.Contains("--loose"));

        var pinned = manifest.Mods.Count(m => m.Version is not null);
        Console.WriteLine($"imported '{manifest.Id}' for game {manifest.GameVersion} "
                          + $"({manifest.Mods.Count} mods, {pinned} pinned)");
        Console.WriteLine($"  sync it with: cairn-cli sync {manifest.Id}");
        return 0;
    }

    private static async Task<int> Runtimes(RuntimeStore store, HttpClient http, string[] args)
    {
        var installer = new DotnetRuntimeInstaller(http, store);
        var action = args.Length > 1 ? args[1] : "list";

        if (action == "install")
        {
            if (args.Length < 3 || !int.TryParse(args[2], out var major))
                return Fail("usage: cairn-cli runtimes install <major>   (e.g. 8)");

            // The game is x64 on every platform, so that is the runtime it needs.
            var rid = DotnetRuntimeInstaller.RidFor(Cairn.Core.Runtime.ExecutableArch.X64);
            var release = await installer.ResolveAsync(major, rid);
            Console.WriteLine($"installing .NET {release.Version} ({rid})");

            var lastPhase = "";
            var progress = new Progress<InstallProgressReport>(p =>
            {
                if (p.Phase == "downloading")
                {
                    var pct = p.Fraction is { } f ? $"{f * 100,5:F1}%" : "     ?";
                    Console.Write($"\r  downloading {pct}  ({p.Done / 1024 / 1024} MB)   ");
                }
                else if (p.Phase != lastPhase) Console.WriteLine($"\r  {p.Phase}          ");

                lastPhase = p.Phase;
            });

            var installed = await installer.InstallAsync(release, progress);
            Console.WriteLine($"  installed -> {installed.Root}");
            return 0;
        }

        if (action == "remove")
        {
            if (args.Length < 3) return Fail("usage: cairn-cli runtimes remove <version>");

            var match = store.ListInstalled()
                .FirstOrDefault(r => Path.GetFileName(r.Root).StartsWith(args[2], StringComparison.Ordinal));
            if (match is null) return Fail($"no managed runtime matching '{args[2]}'");

            Directory.Delete(match.Root, recursive: true);
            Console.WriteLine($"removed {Path.GetFileName(match.Root)}");
            return 0;
        }

        var all = store.ListInstalled().ToList();
        Console.WriteLine($"managed runtimes ({store.Root}):");
        if (all.Count == 0) Console.WriteLine("  (none)");
        foreach (var r in all)
            Console.WriteLine($"  {Path.GetFileName(r.Root),-24} {r.Arch,-6} "
                              + string.Join(", ", r.Frameworks.OrderBy(v => v)));

        return 0;
    }

    private static int Remove(PackStore store, string[] args)
    {
        if (args.Length < 3) return Fail("usage: cairn-cli remove <id> <modid>");

        var id = args[1];
        var manifest = store.Load(id);
        var modId = args[2];

        if (manifest.Mods.RemoveAll(m => string.Equals(m.ModId, modId, StringComparison.OrdinalIgnoreCase)) == 0)
            return Fail($"'{modId}' is not in pack '{id}'");

        store.Save(manifest);
        Console.WriteLine($"removed {modId} from '{id}' (its zip goes on the next sync)");
        return 0;
    }

    private static int Delete(PackStore store, string[] args)
    {
        if (args.Length < 2) return Fail("usage: cairn-cli delete <id>");

        var id = args[1];
        if (!store.Exists(id)) return Fail($"no pack '{id}'");

        store.Delete(id);
        Console.WriteLine($"deleted pack '{id}' and its downloaded mods");
        return 0;
    }

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…";
}
