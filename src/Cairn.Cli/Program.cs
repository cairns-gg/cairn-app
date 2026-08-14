using Cairn.Core;
using Cairn.Core.Games;
using Cairn.Core.Games.Optimum;
using Cairn.Core.Launch;
using Cairn.Core.Runtime;
using Cairn.Core.ModDb;
using Cairn.Core.Packs;
using Cairn.Core.Cairns;

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

        // A default Windows console is IBM437, which silently drops every character this
        // codebase writes above ASCII — the em dashes throughout its messages, the arrow
        // in "1.0.0 -> 2.0.0", and the one marking a pack as running something other than
        // the stock game. The diagnostics report is the text people are told to paste into
        // an issue, so losing characters from it is worse than cosmetic.
        //
        // Set whether or not the stream is redirected. Redirection is the case that
        // matters most — piping the diagnostics report to a file is how somebody captures
        // it to paste — and it was demonstrably losing the characters: the bytes on disk
        // held four spaces where the arrow had been, with nothing above ASCII in them.
        try
        {
            if (OperatingSystem.IsWindows())
                Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (Exception e) when (e is IOException or PlatformNotSupportedException)
        {
            // An unusual console is not a reason to refuse to run.
        }

        // Before anything reads or writes a path, and before the migration below moves a
        // directory. A pointer at a disk that is not mounted has to stop here: falling back
        // to the default would start on an empty root, and the first thing that happens on
        // an empty root is Cairn offering to download everything again.
        //
        // Except for `home` itself, which is how somebody repairs exactly that. A repair
        // tool that refuses to run until the thing it repairs is fixed is no use at all.
        if (args[0] != "home" && CairnHome.Preflight() is { } problem)
        {
            Console.Error.WriteLine($"error: {problem}");
            Console.Error.WriteLine("       cairn-cli home            to see what is set");
            Console.Error.WriteLine("       cairn-cli home clear      to go back to the default");
            return 1;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) }.Bounded();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("cairn/0.1 (+https://github.com/dizzyd/cairn)");
        var moddb = new ModDbClient(http);
        var store = new PackStore();
        var games = new GameStore();
        var runtimes = new RuntimeStore();

        // Said out loud rather than done quietly: it moves a directory the user may have a
        // path to, and both front-ends do it, so whichever runs first is the one that says.
        foreach (var moved in games.MigrateToBundles())
            Console.WriteLine($"moved {moved} — an install has to be a bundle for macOS to "
                              + "scale its window properly");

        try
        {
            return args[0] switch
            {
                "home" => Home(args),
                "info" => Info(),
                "diagnostics" => Diagnostics(store, games, args),
                "list" => List(store),
                "init" => Init(store, args),
                "add" => Add(store, args),
                "search" => await Search(moddb, args),
                "remove" => Remove(store, args),
                "delete" => Delete(store, args),
                "export" => Export(store, args),
                "import" => await Import(store, http, args),
                "import-install" => await ImportInstall(store, moddb, args),
                "games" => await Games(games, http, args),
                "optimum" => await Optimum(games, runtimes, http, args),
                "runtimes" => await Runtimes(runtimes, http, args),
                "pull" => await Pull(store, http, args),
                "sync" => await Sync(store, moddb, http, args),
                "update" => await Update(store, moddb, http, args),
                "launch" => await LaunchPack(store, games, runtimes, moddb, http, args),
                "login" => await Login(http, args),
                "logout" => Logout(),
                "whoami" => await WhoAmI(http),
                "publish" => await Publish(store, moddb, http, args),
                "unpublish" => await Unpublish(store, http, args),
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
            cairn-cli - Vintage Story modpack manager

              cairn-cli info                          show the detected install and data path
              cairn-cli home [show]                   where Cairn keeps its state, and why
              cairn-cli home set <dir>                keep it somewhere else (moves nothing)
              cairn-cli home move <dir> [--yes]       copy everything there, then use it
              cairn-cli home discard <dir> [--yes]    delete a copy a move left behind
              cairn-cli home clear                    go back to the default
              cairn-cli diagnostics [<id>]            print what a bug report needs
              cairn-cli list                          list packs
              cairn-cli init <name> [--id <id>] [--game <version>] [--connect host:port]
              cairn-cli add <id> <modid> [version] [--accept-unmarked]  add a mod to a pack
              cairn-cli remove <id> <modid>           remove a mod from a pack
              cairn-cli delete <id>                   delete a pack and its mods
              cairn-cli export <id> [-o file] [--no-lock]   write a shareable pack file
              cairn-cli import <file|url> [--id x] [--loose] [--follow|--fork]
                                                        create a pack from one
              cairn-cli import-install <name> [--id x] [--game <version>] [--from <dir>]
                                                      [--include-disabled] [--dry-run]
                                                      make a pack from the mods you already have
              cairn-cli games                         list installed and available game versions
              cairn-cli games install <version>       download and install a game version
              cairn-cli games remove <version>        delete an installed game version
              cairn-cli optimum                       what building the Optimum client would cost
              cairn-cli optimum build [--yes]         build and install it (long; see the warning)
              cairn-cli optimum clean                 delete the build tree, keeping the client
              cairn-cli runtimes                      list .NET runtimes Cairn manages
              cairn-cli runtimes install <major>      download a private .NET runtime (e.g. 8)
              cairn-cli runtimes remove <version>     delete one
              cairn-cli search <text> [--game <version>]  search ModDB
              cairn-cli pull <id> [--check] [--theirs] take an author's newer revision
              cairn-cli sync <id>                     install what the lockfile says
              cairn-cli update <id> [modid...]     move followed mods to their newest
              cairn-cli update <id> --check [--fresh]  report updates without installing
              cairn-cli launch <id> [--dry-run] [--no-install] [--install <dir>]  sync, then start
              cairn-cli login [--no-browser]          sign in to cairns.gg
              cairn-cli logout                        forget this machine's token
              cairn-cli whoami                        who this machine is signed in as
              cairn-cli publish <id> [--slug x] [--public] [--keep-server]  share a pack
              cairn-cli unpublish <id>                withdraw a published pack

            Packs live under $CAIRN_HOME, then whatever `home set` recorded, then
            ~/.cairn/packs/<id> — in that order, and CAIRN_HOME always wins.
            Set CAIRNS_SERVER to publish somewhere other than https://cairns.gg.
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

    /// <summary>
    /// The same report the launcher copies to the clipboard, on stdout so it can be piped
    /// or redirected. Printed rather than sent, for the reason Diagnostics exists: this
    /// machine holds a cairns.gg token, and nothing here transmits anything.
    /// </summary>
    private static int Diagnostics(PackStore store, GameStore games, string[] args)
    {
        var id = args.Length > 1 ? args[1] : null;

        if (id is not null && !store.Exists(id)) return Fail($"no pack '{id}'");

        // The same merge the launcher reports: Cairn's own installs plus whatever was
        // already on the machine, because "which game is this actually running" is one of
        // the first questions a bug report has to answer.
        var library = new GameLibrary(games, GameInstall.TryLocate());

        Console.WriteLine(Cairn.Core.Diagnostics.Report(
            pack: id is null ? null : store.Load(id),
            locked: id is null ? null : store.LoadLock(id),
            log: null,
            library: library,
            modsDir: id is null ? null : store.ModsDir(id),

            // The same resolution the launcher makes, including a pack pointed at a
            // modified client — the report is worth less if the two front-ends disagree
            // about which install it describes.
            install: id is null ? null : Resolve(store, library, id)));

        return 0;
    }

    /// <summary>
    /// The install a pack would launch: its own choice if it has one and that still fits,
    /// else stock. See GameLibrary.ResolveFor — a recorded choice stops applying when the
    /// pack's game version moves away from it.
    /// </summary>
    private static GameInstall? Resolve(PackStore store, GameLibrary library, string id) =>
        library.ResolveFor(
            store.Load(id).GameVersion,
            store.LoadLocalState(id).InstallDirectory).Install;

    /// <summary>
    /// Shows or changes where Cairn keeps its state.
    ///
    /// Moves nothing. That is the whole of what this does and it is said on every path
    /// through it, because "set the home directory" reads like it relocates the data, and
    /// somebody who believes that will point Cairn at an empty disk and conclude their packs
    /// are gone.
    /// </summary>
    private static int Home(string[] args)
    {
        var action = args.Length > 1 ? args[1] : "show";

        switch (action)
        {
            case "show":
            {
                var r = CairnHome.Resolve();

                Console.WriteLine($"root        : {r.Root}");
                Console.WriteLine($"decided by  : {Describe(r.Source)}");
                Console.WriteLine($"pointer file: {CairnHome.PointerPath}"
                                  + (File.Exists(CairnHome.PointerPath) ? "" : "  (none)"));

                if (r.Problem is not null) Console.WriteLine($"problem     : {r.Problem}");

                // Said here rather than only on failure: the pointer being ignored is not an
                // error, and somebody wondering why their setting did nothing looks here.
                if (r.Source is HomeSource.Environment && File.Exists(CairnHome.PointerPath))
                    Console.WriteLine("note        : CAIRN_HOME is set, so the pointer file is ignored");

                if (r.Source is HomeSource.Pointer && !Directory.Exists(r.Root))
                    Console.WriteLine("problem     : that directory is not there");

                return r.Problem is null ? 0 : 1;
            }

            case "set":
            {
                if (args.Length < 3) return Fail("usage: cairn-cli home set <directory>");

                // Set-but-ignored is the worst outcome: the file is written, nothing changes,
                // and the reason is invisible. Refuse instead of leaving them to find out.
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CAIRN_HOME")))
                    return Fail("CAIRN_HOME is set, and it wins over the pointer file. "
                                + "Unset it first, or keep using it.");

                var target = Path.GetFullPath(args[2]);

                if (!Directory.Exists(target))
                    return Fail($"{target} is not there. Create it first — this names a "
                                + "directory, it does not make one.");

                CairnHome.SetPointer(target);

                Console.WriteLine($"cairn home is now {target}");
                Console.WriteLine($"recorded in {CairnHome.PointerPath}");
                Console.WriteLine();
                Console.WriteLine("Nothing was moved. Anything already installed is still in the");
                Console.WriteLine("old place and Cairn will no longer see it — copy it across, or");
                Console.WriteLine("clear this and start from what you had.");
                return 0;
            }

            case "move":
            {
                if (args.Length < 3) return Fail("usage: cairn-cli home move <directory> [--yes]");

                var plan = HomeMigration.Plan(Path.GetFullPath(args[2]));

                // Every refusal is decided before a byte is written, so this is the whole
                // of the risk assessment and it costs nothing to have got wrong.
                if (!plan.CanMove) return Fail(plan.Problem!);

                Console.WriteLine($"from  {plan.From}");
                Console.WriteLine($"to    {plan.To}");
                Console.WriteLine($"      {plan.Files} files, {HomeMigration.Describe(plan.Bytes)}"
                                  + (plan.Links > 0 ? $", {plan.Links} links kept as links" : ""));
                Console.WriteLine();
                Console.WriteLine("Copies everything, checks it file by file, repoints Cairn, and then");
                Console.WriteLine("deletes the original. Nothing is removed before the new copy has");
                Console.WriteLine("been verified — but when this finishes, the old one is gone.");

                if (!args.Contains("--yes"))
                {
                    Console.Write("\nMove it? [y/N] ");
                    if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
                        return Fail("cancelled");
                }

                using var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

                var lastPercent = -1;
                var progress = new Progress<MoveProgress>(p =>
                {
                    var percent = p.BytesTotal == 0 ? 100 : (int)(100 * p.Bytes / p.BytesTotal);
                    if (percent == lastPercent) return;
                    lastPercent = percent;

                    // Redirected output gets whole lines: a carriage return into a file
                    // produces one unreadable line holding every update at once.
                    if (Console.IsOutputRedirected) Console.WriteLine($"  {percent}%");
                    else Console.Write($"\r  {percent}%  {p.Files}/{p.FilesTotal} files   ");
                });

                MoveResult result;
                try
                {
                    result = HomeMigration.Move(plan, progress, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine();
                    return Fail($"cancelled; nothing was repointed and {plan.From} is untouched. "
                                + $"Delete the part-copy at {plan.To} before trying again.");
                }

                if (!Console.IsOutputRedirected) Console.WriteLine();

                Console.WriteLine();
                Console.WriteLine($"cairn home is now {plan.To}");
                Console.WriteLine($"copied {result.Files} files, {HomeMigration.Describe(result.Bytes)}"
                                  + (result.Links > 0 ? $" and {result.Links} links" : ""));

                if (result.Rewritten > 0)
                    Console.WriteLine($"repointed {result.Rewritten} pack"
                                      + (result.Rewritten == 1 ? "" : "s")
                                      + " at their pinned install");

                Console.WriteLine();

                if (result.RemovalProblem is { } stuck)
                {
                    // The move worked. Leading with the failure would send somebody looking
                    // for data that is exactly where it should be.
                    Console.WriteLine($"The original at {result.OldRoot} could not be removed:");
                    Console.WriteLine($"  {stuck}");
                    Console.WriteLine($"It is still using {HomeMigration.Describe(result.Bytes)}. To try again:");
                    Console.WriteLine($"  cairn-cli home discard {result.OldRoot}");
                }
                else
                {
                    Console.WriteLine($"removed the original, freeing {HomeMigration.Describe(result.Freed)}");
                }

                return 0;
            }

            case "discard":
            {
                if (args.Length < 3) return Fail("usage: cairn-cli home discard <old-root> [--yes]");

                var old = Path.GetFullPath(args[2]);

                if (!Directory.Exists(old)) return Fail($"{old} is not there");

                // The whole point of the command: deleting the live root is the one mistake
                // that cannot be walked back, and a mistyped path is how it would happen.
                if (string.Equals(Path.TrimEndingDirectorySeparator(old),
                                  Path.TrimEndingDirectorySeparator(CairnPaths.Root),
                                  StringComparison.OrdinalIgnoreCase))
                    return Fail($"{old} is where Cairn is keeping its files now");

                // Kept if it is in there: it is what points Cairn at where everything went.
                var keep = File.Exists(CairnHome.PointerPath)
                           && CairnHome.PointerPath.StartsWith(old, StringComparison.OrdinalIgnoreCase)
                    ? CairnHome.PointerPath
                    : null;

                Console.WriteLine($"deletes everything under {old}");
                if (keep is not null) Console.WriteLine($"keeps    {keep}");
                Console.WriteLine($"Cairn is using {CairnPaths.Root} and is not touched.");

                if (!args.Contains("--yes"))
                {
                    Console.Write("\nDelete it? [y/N] ");
                    if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
                        return Fail("cancelled");
                }

                var freed = HomeMigration.DeleteOldRoot(old, keep);

                Console.WriteLine($"deleted, freeing {HomeMigration.Describe(freed)}");
                return 0;
            }

            case "clear":
            {
                if (!File.Exists(CairnHome.PointerPath))
                {
                    Console.WriteLine("no pointer file; already using the default");
                    return 0;
                }

                // Read before removing it, so the message can name what is about to stop
                // being reachable rather than leaving somebody to work it out.
                var pointedAt = CairnHome.Resolve().Root;

                CairnHome.SetPointer(null);
                Console.WriteLine($"pointer removed; cairn home is {CairnHome.DefaultRoot} again");
                Console.WriteLine();
                Console.WriteLine($"Nothing was moved. Everything is still at {pointedAt},");
                Console.WriteLine("and Cairn no longer reads it — set the pointer again to get it back.");
                return 0;
            }

            default:
                return Fail($"unknown home action '{action}' — show, set, move, discard or clear");
        }

        static string Describe(HomeSource source) => source switch
        {
            HomeSource.Environment => "CAIRN_HOME",
            HomeSource.Pointer => "the pointer file",
            _ => "the default",
        };
    }

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
            return Fail("usage: cairn-cli init <name> [--id <id>] [--game <version>] "
                        + "[--connect host:port] [--description text]");

        var name = args[1];
        var gameVersion = ArgValue(args, "--game") ?? GameInstall.TryLocate()?.Version;
        if (gameVersion is null or "unknown")
            return Fail("could not detect the game version; pass --game <version>");

        // Slugging is idempotent, so `cairn-cli init anego` still produces exactly "anego"
        // while `cairn-cli init "Anego Server"` now works instead of being refused.
        var id = ArgValue(args, "--id") ?? store.SuggestId(name);

        var problem = store.DescribeIdProblem(id);
        if (problem is not null) return Fail(problem);

        var description = ArgValue(args, "--description");
        if (description is { Length: > PackManifest.MaxDescription })
            return Fail($"--description is longer than {PackManifest.MaxDescription} characters");

        store.Create(id, gameVersion, name, ArgValue(args, "--connect"), description);
        Console.WriteLine($"created {store.ManifestPath(id)}  (id {id}, game {gameVersion})");
        return 0;
    }

    private static int Add(PackStore store, string[] args)
    {
        if (args.Length < 3)
            return Fail("usage: cairn-cli add <id> <modid> [version] [--accept-unmarked]");

        var id = args[1];
        var manifest = store.Load(id);
        var modId = args[2];
        var version = args.Length > 3 && !args[3].StartsWith('-') ? args[3] : null;

        if (manifest.Mods.Any(m => string.Equals(m.ModId, modId, StringComparison.OrdinalIgnoreCase)))
            return Fail($"'{modId}' is already in pack '{id}'");

        // A flag rather than a prompt, because this is the acceptance: it says the person
        // running it has tried the mod on this game version and will live with the result.
        // Recorded against the version the pack targets now, so retargeting a minor asks
        // the question again instead of inheriting a promise nobody made about it.
        var accepted = args.Contains("--accept-unmarked");

        manifest.Mods.Add(new PackMod
        {
            ModId = modId,
            Version = version,
            AcceptedFor = accepted ? manifest.GameVersion : null,
        });

        store.Save(manifest);
        Console.WriteLine($"added {modId}{(version is null ? "" : " " + version)} to '{id}'");

        if (accepted)
        {
            Console.WriteLine($"  accepted for game {manifest.GameVersion} even if it is marked "
                              + "for no such version — it may misbehave, and sync will keep "
                              + "saying so");

            if (version is null)
                Console.WriteLine("  consider naming the version you tested: an unpinned mod "
                                  + "can move to another release nobody has tried");
        }

        return 0;
    }

    /// <summary>
    /// Makes a pack out of the mods somebody already has in plain Vintage Story.
    ///
    /// Prints every mod and what became of it, including the ones left out — a pack that
    /// silently holds eleven of your fourteen mods is worse than one that says which three
    /// it could not take and why.
    /// </summary>
    private static async Task<int> ImportInstall(PackStore store, ModDbClient moddb, string[] args)
    {
        if (args.Length < 2)
            return Fail("usage: cairn-cli import-install <name> [--id <id>] [--game <version>] "
                        + "[--from <dir>] [--include-disabled] [--dry-run]");

        var name = args[1];
        var install = GameInstall.TryLocate();

        var playedOn = install?.Version is { } v and not "unknown" ? v : null;
        var gameVersion = ArgValue(args, "--game") ?? playedOn;
        if (gameVersion is null)
            return Fail("could not detect the game version; pass --game <version>");

        var modsDir = ArgValue(args, "--from") ?? InstalledMods.DefaultModsDir;
        var scan = InstalledMods.Scan(modsDir);

        Console.WriteLine($"reading {modsDir}");

        if (scan.Mods.Count == 0)
            return Fail($"no mod zips in {modsDir}");

        foreach (var ignored in scan.Ignored)
            Console.WriteLine($"  ignoring {ignored} — only zipped mods can be imported");

        // Read from the data path the mods were found under, so --from somewhere else does
        // not have the wrong install's switched-off list applied to it.
        var disabled = args.Contains("--include-disabled")
            ? null
            : InstalledMods.DisabledIn(Path.GetDirectoryName(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(modsDir)))!);

        // Printed as each one is settled rather than in a block at the end: a long import is
        // one ModDB lookup per mod, and a terminal that says nothing for a minute looks hung.
        var plan = await new InstallImport(moddb).PlanAsync(
            scan, gameVersion, disabled, playedOn,
            new Progress<ImportCandidate>(c =>
                Console.WriteLine($"  {(c.Included ? "+" : "-")} {c.Mod.Describe}: "
                                  + $"{c.Verdict.ToString().ToLowerInvariant()} — {c.Note}")));

        var taking = plan.Count(c => c.Included);
        Console.WriteLine($"{taking} of {scan.Mods.Count} mods can go in a pack for game {gameVersion}");

        if (args.Contains("--dry-run")) return 0;
        if (taking == 0) return Fail("nothing to import");

        var id = ArgValue(args, "--id") ?? store.SuggestId(name);

        var problem = store.DescribeIdProblem(id);
        if (problem is not null) return Fail(problem);

        InstallImport.CreatePack(store, id, gameVersion, name, plan);

        Console.WriteLine($"created {store.ManifestPath(id)}  (id {id}, game {gameVersion})");
        Console.WriteLine($"run `cairn-cli sync {id}` to install them into the pack");
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

        // Remembered for a few minutes: the check is one ModDB request per unpinned mod,
        // and a script polling it should not pay that every time. --fresh overrides.
        // One request per unpinned mod, so a full pack takes seconds with nothing to look
        // at. Written to stderr so piping the result stays clean, and only to a terminal —
        // a redirected run wants the answer, not a play-by-play.
        var checkedSoFar = 0;
        var toCheck = manifest.Mods.Count(m => m.Version is null);

        var progress = Console.IsErrorRedirected
            ? null
            : new Progress<string>(modId =>
                Console.Error.Write($"\rchecking {modId} ({++checkedSoFar} of {toCheck})".PadRight(60)));

        var updates = await syncer.CheckUpdatesAsync(
            manifest, store.LockPath(id), progress,
            cache: new ModUpdateCache(), force: args.Contains("--fresh"));

        if (progress is not null) Console.Error.Write("\r".PadRight(61) + "\r");

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
        if (args.Length < 2)
            return Fail("usage: cairn-cli launch <id> [--dry-run] [--no-install] [--install <dir>]");

        var id = args[1];
        var manifest = store.Load(id);

        // A pack names the game version it wants; prefer an install of exactly that.
        var library = new GameLibrary(gameStore, GameInstall.TryLocate());

        // --install overrides for this run only; the pack's own choice is what it was told
        // to use. Neither is ever inferred: ForVersion will not return a modified client,
        // so running one is always something somebody asked for.
        var install = ArgValue(args, "--install") is { } dir
            ? GameInstall.TryAt(dir) ?? throw new InvalidOperationException(
                  $"'{dir}' is not a Vintage Story install.")
            : Resolve(store, library, id);

        // The same rule the launcher's picker applies, which it was not making here: a
        // build is offered for the version it is a build of and no other. A pack's mods
        // were resolved against its game version, so running a different one is not an
        // override, it is a pack running a client nothing in it was chosen for — and the
        // symptom is this command announcing the variant and then offering to download
        // the version it was told not to use.
        if (ArgValue(args, "--install") is not null && install is not null
            && !string.Equals(install.Version, manifest.GameVersion, StringComparison.OrdinalIgnoreCase))
            return Fail($"'{install.Directory}' is {install.Describe}, but '{id}' targets "
                        + $"{manifest.GameVersion}. Retarget the pack, or point it at a "
                        + $"{manifest.GameVersion} build.");

        if (install is { IsVariant: true } modified)
            Console.WriteLine($"running {modified.Variant}, not the stock game");

        // Same contract as the launcher: fetch what the pack needs rather than telling
        // the user to go and do it.
        var provisioner = new GameProvisioner(http, gameStore, runtimes);

        // Told about the install this run will actually use. Plan looks the version up for
        // itself, and Find deliberately will not return a variant — so without this a pack
        // pointed at a modified client offered to download the stock game it was told not
        // to run, having just announced it was running the other one.
        var plan = provisioner.Plan(manifest.GameVersion, install ?? GameInstall.TryLocate());

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
        {
            // Asked for this install rather than for the pack's version, which the plan
            // above already answered: a variant built for this machine can need a .NET of
            // a different architecture than the stock download of the same version, so a
            // version reported ready says nothing about the client about to start.
            if (args.Contains("--no-install"))
                return Fail($"{install.Version} needs .NET {install.RequiredFramework} and none was "
                            + "found. Re-run without --no-install to fetch it, or install one with: "
                            + $"cairn-cli runtimes install {install.RequiredFramework.Major}");

            Console.WriteLine(provisioner.PlanFor(install).Describe());

            var runtimeProgress = new Progress<ProvisionStep>(p =>
            {
                if (p.Fraction is { } f && p.Phase == "downloading")
                    Console.Write($"\r  {p.Detail} ({f * 100,5:F1}%)      ");
                else Console.WriteLine($"\r  {p.Detail}          ");
            });

            await provisioner.EnsureRuntimeAsync(install, runtimeProgress);

            managedRoot = runtimes.RootFor(install);
            probe = new LaunchOptions { PreferredDotnetRoot = managedRoot };
            runtime = new GameLauncher(install).ResolveRuntime(probe);

            if (!runtime.Resolved)
                return Fail($"{install.Version} needs .NET {install.RequiredFramework} and none "
                            + "could be installed. Try: cairn-cli runtimes install "
                            + $"{install.RequiredFramework.Major}");
        }

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

            // Every DOTNET_ROOT* that was set rather than the two that used to be the only
            // ones: the launcher names the variable after the game's architecture, so a
            // native arm64 client gets DOTNET_ROOT_ARM64 — and a dry run that omits it
            // fails to show the variable that actually decides which runtime is used.
            foreach (var (name, value) in psi.Environment
                         .Where(e => e.Key.StartsWith("DOTNET_ROOT", StringComparison.Ordinal))
                         .OrderBy(e => e.Key, StringComparer.Ordinal))
                Console.WriteLine($"  {name}={value}");

            Console.WriteLine("dry run - not started");
            return 0;
        }

        // Carried in now, so a pack does not ask for a fresh login.
        var bound = new List<string>();
        var config = new List<ModConfigChange>();

        foreach (var dropped in packData.BeforeLaunch(id, bound, config))
            Console.WriteLine($"no longer loading mods from {dropped} — this pack has its own");

        // The pack's hotkeys, for the ones this copy has no binding of its own for. Said
        // out loud here as well as in the launcher: a keyboard that changes without
        // mentioning it is the same surprise from either front end.
        if (bound.Count > 0)
            Console.WriteLine($"bound {bound.Count} hotkey{(bound.Count == 1 ? "" : "s")} "
                              + $"from the pack: {string.Join(", ", bound)}");

        // Every line of it, rather than a count: these are values that change how the game
        // plays, in files belonging to other people's mods, and the ones Cairn declined to
        // write are the half somebody has to be able to act on. The wording is
        // ModConfigChange.Describe so both front ends say the same thing.
        foreach (var change in config) Console.WriteLine(change.Describe());

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
            // from the version its assembly reports is still the one that goes. A variant
            // is named by its folder, because Find deliberately will not return one.
            var found = games.Find(args[2])
                        ?? games.ListInstalled().FirstOrDefault(i =>
                               string.Equals(Path.GetFileName(i.Directory), args[2],
                                   StringComparison.OrdinalIgnoreCase));
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
                return Fail($"no {GameCatalog.PlatformDescription} download published for {wanted}");

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
            // Describe rather than Version: two entries both reading "1.22.5" with nothing
            // to tell them apart is a puzzle, and one of them may not be the game.
            Console.WriteLine($"  {i.Describe,-24} {i.Architecture,-6} needs .NET {i.RequiredFramework}"
                              + (i.IsVariant ? $"   ({Path.GetFileName(i.Directory)})" : ""));

        var system = GameInstall.TryLocate();
        if (system is not null && !installed.Any(i => i.Directory == system.Directory))
            Console.WriteLine($"  {system.Version,-12} {system.Architecture,-6} (pre-existing: {system.Directory})");

        Console.WriteLine();
        Console.WriteLine($"available for {GameCatalog.PlatformDescription}:");
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

        // An export carries the manifest and lock and nothing else — no canonical URL, no
        // author — so a file made from somebody else's pack reaches the next person as an
        // unowned one they may publish freely. Pass on the link instead.
        if (store.LoadLink(id) is { Role: PackRole.Follower, Following: true } following)
            return Fail($"'{id}' was imported from {following.Url}; pass on that link rather "
                        + "than a copy, which would arrive with its author stripped off");

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

    /// <summary>
    /// Takes an author's newer revision of a followed pack.
    ///
    /// Distinct from <c>update</c>, which moves mods to their newest ModDB release. This
    /// moves the pack to what its author published, and the two pull in different
    /// directions: one diverges from the author, the other converges on them.
    /// </summary>
    private static async Task<int> Pull(PackStore store, HttpClient http, string[] args)
    {
        if (args.Length < 2)
            return Fail("usage: cairn-cli pull <id> [--check] [--theirs] [--reset]");

        var id = args[1];
        if (!store.Exists(id)) return Fail($"no pack '{id}'");

        var link = store.LoadLink(id);

        if (!PackUpdateCheck.CanCheck(link))
            return Fail($"'{id}' does not follow anybody — nothing to pull");

        // Fetched rather than checked, for the same reason the launcher does it: being on
        // the author's latest revision is not the same as matching it, and --reset has to
        // work for somebody who has edited a copy that is otherwise up to date.
        var bundle = await PackUpdateCheck.FetchAsync(link, http);

        if (bundle is null)
        {
            Console.WriteLine($"'{id}' could not be compared — {link!.Url} did not answer "
                              + "with a pack");
            return 1;
        }

        var latest = bundle.Revision ?? 0;
        var mine = store.Load(id);

        var plan = PackUpdatePlan.Between(
            mine, bundle.Pack!, store.LoadUpstream(id),
            link!.Revision, latest, store.LoadLocalState(id));

        // Reset discards this copy's changes rather than reconciling them, so it is set
        // before anything is printed: the list below has to describe what would happen.
        plan.Reset = args.Contains("--reset");

        Console.WriteLine(plan.Summary());

        if (plan.ResetRemovesAnything)
        {
            Console.WriteLine($"  ! reset removes {string.Join(", ", plan.RemovedByReset)} "
                              + "from this pack");

            // A world holds the blocks and items of the mods that built it, so this is a
            // change to the save and not only to a list.
            Console.WriteLine("  ! anything those mods placed in a world of this pack will "
                              + "be gone from it — back it up first");
        }

        if (!plan.HasBase)
            Console.WriteLine("  ! no record of the revision you started from, so a mod you "
                              + "removed reads as one the author added");

        if (plan.GameVersionChanges)
            Console.WriteLine($"  game {plan.PreviousGameVersion} -> {plan.GameVersion}");

        foreach (var change in plan.TheirChanges)
            Console.WriteLine($"  {change.ModId,-24} {change.Describe()}");

        // The launcher asks; a CLI cannot, so it says what it is about to decide and
        // offers the one flag that changes it. Defaults keep what is yours.
        foreach (var choice in plan.Choices)
        {
            if (plan.Reset) break;      // a reset does not consult them
            if (args.Contains("--theirs")) choice.Take = true;

            Console.WriteLine($"  ? {choice.ModId,-22} {choice.Describe()}"
                              + $" — keeping {(choice.Take ? "theirs" : "yours")}");
        }

        if (plan.Choices.Any() && !args.Contains("--theirs"))
            Console.WriteLine("  (--theirs takes the author's side for all of these)");

        // Nothing of theirs to take and nothing of yours that differs.
        if (!plan.AnyChange && !plan.Changes.Any() && !plan.Reset)
        {
            Console.WriteLine($"'{id}' matches the author's revision {latest}");
            return 0;
        }

        if (args.Contains("--check")) return 0;

        var merged = store.ApplyUpdate(id, plan, bundle);

        Console.WriteLine($"{(plan.Reset ? "reset to" : "pulled")} revision {latest} "
                          + $"into '{id}' ({merged.Mods.Count} mods) — run sync to install");
        return 0;
    }

    private static async Task<int> Import(PackStore store, HttpClient http, string[] args)
    {
        if (args.Length < 2) return Fail("usage: cairn-cli import <file|url> [--id x] [--loose] [--follow|--fork]");

        var source = args[1];
        string json;
        string? landed = null;

        try
        {
            // A pack decides which mods get installed, so it must not arrive over a
            // connection anyone on the path can rewrite. Loopback has no such path.
            if (PackSources.IsRewritableInFlight(source))
                return Fail("refusing to import over http; use https");

            // Through DocumentUrl, because a pack's canonical URL is the page a person
            // reads and that address serves HTML. Fetching it raw reported invalid JSON,
            // which is true and tells whoever pasted the URL nothing about what to paste
            // instead. cairn-server has done this since it was written; this had not.
            if (PackSources.IsRemote(source))
            {
                using var response = await http.GetAsync(PackUpdateCheck.DocumentUrl(source));
                response.EnsureSuccessStatusCode();

                // Where it answered from, not where it was asked. Redirects are followed
                // silently and may cross hosts, and this is the address recorded as the
                // pack's origin — see PackSources.LandingAddress.
                landed = PackSources.LandingAddress(response, PackUpdateCheck.DocumentUrl(source));
                json = await response.Content.ReadAsStringAsync();
            }
            else
            {
                json = File.ReadAllText(source);
            }
        }
        catch (Exception e) when (e is IOException or HttpRequestException)
        {
            return Fail($"could not read '{source}': {e.Message}");
        }

        var bundle = PackBundle.Parse(json);

        // --loose tracks newest-compatible instead of reproducing the author's versions.
        var reproduce = !args.Contains("--loose");

        if (args.Contains("--follow") && args.Contains("--fork"))
            return Fail("--follow and --fork ask for opposite things; pass one");

        // The address it actually came from, so a follow is recorded against that rather
        // than against whatever the document names itself — and against where the fetch
        // landed rather than where it was aimed, since a redirect can move it.
        var fetchedFrom = landed;

        ImportIntent? intent = args.Contains("--fork") ? ImportIntent.Fork
            : args.Contains("--follow") ? ImportIntent.Follow
            : null;

        // A published document out of a file, with nobody having said which they meant.
        // It forks, because the only address on offer is the file's own word for where it
        // lives and acting on that unasked is what lets a file point this machine at a
        // host of its choosing. Said out loud rather than done quietly — the choice is
        // real, and somebody who wanted the other one should not have to discover it from
        // a pack that never checks for updates.
        var unaskedFork = intent is null && fetchedFrom is null && bundle.IsPublished;

        var manifest = store.Import(bundle, ArgValue(args, "--id"), reproduce, fetchedFrom, intent);

        var pinned = manifest.Mods.Count(m => m.Version is not null);
        var how = reproduce && bundle.Lock is not null
            ? "reproducing the author's versions"
            : "tracking newest-compatible";
        Console.WriteLine($"imported '{manifest.Id}' for game {manifest.GameVersion} "
                          + $"({manifest.Mods.Count} mods, {pinned} pinned, {how})");

        if (unaskedFork)
        {
            Console.WriteLine($"  this pack says it comes from {bundle.CanonicalUrl}");
            Console.WriteLine("  imported as your own copy, so nothing checks back with it");
            Console.WriteLine("  pass --follow to keep it in step with that address instead");
        }
        else if (store.LoadLink(manifest.Id) is { Role: PackRole.Follower } link)
        {
            Console.WriteLine($"  following {link.Url}");
        }

        Console.WriteLine($"  sync it with: cairn-cli sync {manifest.Id}");
        return 0;
    }

    /// <summary>
    /// Building the Optimum client.
    ///
    /// Prints the same plan the launcher shows in its confirmation, because the cost is the
    /// decision: this is a twenty-minute compile of a game client, and every other thing
    /// Cairn installs is a download. With no arguments it only reports, so somebody can see
    /// what it would take without starting anything.
    /// </summary>
    private static async Task<int> Optimum(
        GameStore games, RuntimeStore runtimes, HttpClient http, string[] args)
    {
        var provisioner = new OptimumProvisioner(http, games, runtimes);
        var source = OptimumSource.Pinned;
        var plan = provisioner.Plan(source.GameVersion, source);

        var action = args.Length > 1 ? args[1] : "plan";

        if (action == "plan")
        {
            Console.WriteLine(plan.Describe());

            if (plan.AlreadyBuilt)
                Console.WriteLine($"\nAlready built: {games.InstallDir(source.InstallName)}");

            Console.WriteLine($"\nBuild it with: cairn-cli optimum build");
            return 0;
        }

        if (action == "clean")
        {
            var freed = provisioner.Clean();

            Console.WriteLine(freed == 0
                ? "nothing to remove"
                : $"removed the build tree, freeing {freed / 1024 / 1024} MB");

            // Said plainly, because the two are easy to confuse and only one of them is
            // the client somebody actually plays.
            if (plan.AlreadyBuilt)
                Console.WriteLine("the installed client is untouched; a rebuild will take"
                                  + " the full time again");

            return 0;
        }

        if (action != "build")
            return Fail("usage: cairn-cli optimum [plan|build|clean] [--yes]");

        if (!plan.CanStart) return Fail(plan.Describe());

        if (plan.AlreadyBuilt && !args.Contains("--force"))
        {
            Console.WriteLine($"Optimum is already installed at {games.InstallDir(source.InstallName)}");
            Console.WriteLine("Rebuild it with --force.");
            return 0;
        }

        Console.WriteLine(plan.Describe());

        // A CLI cannot show a dialog, so it says what it is about to do and waits — unless
        // it is being scripted, which is what --yes is for.
        if (!args.Contains("--yes"))
        {
            Console.Write("\nStart the build? [y/N] ");
            var answer = Console.ReadLine()?.Trim();

            if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
                return Fail("cancelled");
        }

        // Ctrl-C stops the build rather than killing the CLI out from under it, so a
        // half-written install is cleaned up by the same path a cancel in the launcher uses.
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var progress = new Progress<OptimumStep>(s =>
            Console.WriteLine($"[{s.Phase}] {s.Detail}"));

        // Straight to stdout: on a terminal the live log is the progress indicator, and a
        // twenty-minute silence is indistinguishable from a hang.
        var log = new Progress<string>(Console.WriteLine);

        var vanilla = games.Find(source.GameVersion) ?? GameInstall.TryLocate();
        if (vanilla is not null && vanilla.Version != source.GameVersion) vanilla = null;

        try
        {
            var install = await provisioner.BuildAsync(source, vanilla, progress, log, cts.Token);

            Console.WriteLine($"\ninstalled {install.Describe} at {install.Directory}");
            Console.WriteLine($"launch a pack with it: cairn-cli launch <id> --install {install.Directory}");
            return 0;
        }
        catch (OperationCanceledException)
        {
            return Fail($"cancelled; the working tree is kept at {provisioner.WorkingTree}");
        }
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

    // ---- cairns.gg ----

    private static async Task<int> Login(HttpClient http, string[] args)
    {
        var client = new CairnsClient(http);

        // Ctrl-C ends the wait rather than the process mid-write. Without a token this loop
        // is only bounded by what the server said, and the server is the party a hostile
        // one would be — so somebody standing at a prompt that has stopped making progress
        // had no way out but killing it.
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

        var flow = await client.StartSignInAsync(cancel.Token);

        Console.WriteLine($"""

            Open {flow.VerificationUri} and enter this code:

                {flow.UserCode}

            Waiting…
            """);

        // Opened as a convenience, not as the mechanism: the URL is printed above, so a
        // headless box, an unhelpful desktop or --no-browser all cost nothing.
        if (!args.Contains("--no-browser"))
            Browser.Open($"{flow.VerificationUri}?code={flow.UserCode}");

        CairnsSession session;
        try
        {
            session = await client.AwaitSignInAsync(flow, ct: cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return Fail("stopped waiting; nothing was signed in");
        }

        session.Save();

        Console.WriteLine($"signed in to {session.Server} as {session.Username}");
        return 0;
    }

    private static int Logout()
    {
        CairnsSession.Clear();
        Console.WriteLine("signed out; this machine's token is gone");
        return 0;
    }

    private static async Task<int> WhoAmI(HttpClient http)
    {
        if (CairnsSession.Load() is not { } session)
            return Fail("not signed in — run: cairn-cli login");

        var who = await new CairnsClient(http, session.Server).WhoAmIAsync(session);

        if (who is null) return Fail("the stored token is not valid any more — sign in again");

        Console.WriteLine($"{who} at {session.Server}");
        return 0;
    }

    private static async Task<int> Publish(
        PackStore store, ModDbClient moddb, HttpClient http, string[] args)
    {
        if (args.Length < 2) return Fail("usage: cairn-cli publish <id> [--slug x] [--public]");

        var id = args[1];
        if (!store.Exists(id)) return Fail($"no pack '{id}'");

        var link = store.LoadLink(id);

        // A pack imported from someone else is theirs. Checked here as well as in the
        // launcher, which hides its button: a hidden button is a courtesy, and this is the
        // rule.
        if (link is { Role: PackRole.Follower, Following: true })
            return Fail($"'{id}' was imported from {link.Url} and follows its author; "
                        + "publishing it would re-issue their pack under your name");

        if (CairnsSession.Load() is not { } session)
            return Fail("not signed in — run: cairn-cli login");

        // The same plan the launcher's Share window shows, so both front-ends refuse the
        // same packs for the same reasons.
        var manifest = store.Load(id);
        var plan = await PublishPlan.PrepareAsync(manifest, store.LoadLock(id), moddb);

        // Only when it would otherwise refuse, matching the launcher. Publishing a settled
        // pack must not rewrite its lock, and an unreachable ModDB must not be able to turn
        // sharing into a change to what is installed.
        if (!plan.LockCovers)
        {
            Console.WriteLine("syncing first…");
            var report = await new PackSyncer(moddb, http)
                .SyncAsync(manifest, store.ModsDir(id), store.LockPath(id));

            foreach (var step in report.Steps.Where(s => s.Action == SyncAction.Failed))
                Console.WriteLine($"  ! {step.ModId}: {step.Detail}");

            plan = await PublishPlan.PrepareAsync(
                manifest, store.LoadLock(id), moddb, syncFailures: report.Steps);
        }

        if (!plan.CanPublish) return Fail(plan.LockProblem ?? "this pack cannot be published");

        foreach (var mod in plan.Unresolvable)
            Console.WriteLine($"  ! {mod.ModId} is not on ModDB — recipients cannot install it");

        var isPublic = args.Contains("--public");

        // A public pack almost never wants a real server address in it, and an unlisted one
        // usually does. --keep-server overrides, because sometimes it is deliberate.
        var strip = isPublic && !args.Contains("--keep-server");

        if (plan.HasConnect)
            Console.WriteLine(strip
                ? $"  server address {plan.Connect} will be stripped"
                : $"  server address {plan.Connect} will be included");

        var document = store.PublishedDocument(id, strip);
        var published = link?.Url is { Length: > 0 } at ? at[(at.LastIndexOf('/') + 1)..] : null;
        var slug = ArgValue(args, "--slug") ?? published ?? id;

        // The URL is the pack. Publishing under a different slug does not move it — it
        // creates a second pack and leaves the first one live under the same name, which
        // is how you end up with two identical-looking packs and no idea which is which.
        if (link?.Published is not null && published is not null && slug != published)
            return Fail($"'{id}' is published at {link.Url}, and that address is its "
                        + "identity — publishing under another name would leave a second "
                        + $"copy behind. Withdraw it first with: cairn-cli unpublish {id}");

        var client = new CairnsClient(http, session.Server);

        // A revision differing from its predecessor in nothing but its number tells every
        // follower there is an update and then has none for them. Visibility and the
        // server address count as changes; the bytes alone are not the whole question.
        if (link is { Published: { } last }
            && !last.WouldChange(document, isPublic, strip))
        {
            // Unless it is not up any more. A withdrawal made on the site never reaches
            // this machine, so this refusal can be defending a pack that stopped being
            // served — and republishing it unchanged is exactly how it comes back.
            if (!await client.IsWithdrawnAsync(session.Username, slug))
                return Fail($"'{id}' has not changed since revision {link.Revision}");

            store.MarkWithdrawn(id);
            Console.WriteLine($"  {link.Url} was withdrawn — publishing brings it back");
        }
        var result = await client.PublishAsync(session, document, slug, isPublic);

        // Recorded so the pack knows where it lives and whether it has changed since.
        store.SaveLink(id, new PackLink
        {
            Role = PackRole.Author,
            Url = result.Url,
            Revision = result.Revision,
            Published = new PublishRecord
            {
                Fingerprint = PackLink.Fingerprint(document),
                Visibility = result.Visibility,
                Connect = strip ? "stripped" : "included",
            },
        });

        Console.WriteLine($"published {result.Url} (revision {result.Revision}, {result.Visibility})");
        return 0;
    }

    private static async Task<int> Unpublish(PackStore store, HttpClient http, string[] args)
    {
        if (args.Length < 2) return Fail("usage: cairn-cli unpublish <id>");

        var id = args[1];
        if (CairnsSession.Load() is not { } session)
            return Fail("not signed in — run: cairn-cli login");

        if (store.LoadLink(id) is not { Role: PackRole.Author } link)
            return Fail($"'{id}' has not been published from this machine");

        var slug = link.Url[(link.Url.LastIndexOf('/') + 1)..];
        await new CairnsClient(http, session.Server).WithdrawAsync(session, session.Username, slug);

        // The local record has to move too, and not only for tidiness — see MarkWithdrawn.
        // The URL stays: it is still their address, the server revives the pack at it, and
        // the slug is what the next publish defaults to.
        store.MarkWithdrawn(id);

        Console.WriteLine($"withdrew {link.Url} — publish again to bring it back");
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
