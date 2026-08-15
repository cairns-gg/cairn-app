using System.Diagnostics;
using System.Runtime.InteropServices;
using Cairn.Core;
using Cairn.Core.Games;
using Cairn.Core.Launch;
using Cairn.Core.ModDb;
using Cairn.Core.Packs;
using Cairn.Core.Runtime;
using Cairn.Core.Servers;

namespace Cairn.Server;

/// <summary>
/// The headless end of Cairn: follow a published pack, keep a server on it, and be a thing
/// systemd can supervise.
///
/// A separate program from cairn-cli rather than more verbs on it. The CLI is a development
/// tool with two dozen commands and is deliberately not shipped; this has five, is meant to
/// be dropped into a VM or a container by somebody who will read one page of documentation,
/// and every one of its commands is about a server. Both are thin: the rules — what a sync
/// installs, which install can host, which .NET that needs — live in Core and are the same
/// ones the launcher applies.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Usage();
            return args.Length == 0 ? 1 : 0;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) }.Bounded();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("cairn-server/0.1 (+https://github.com/dizzyd/cairn)");

        var packs = new PackStore();

        // Its own install tree. A server and a client of the same version are different
        // things wearing the same version number, and a machine can hold both.
        var games = new GameStore(CairnPaths.ServersRoot);
        var runtimes = new RuntimeStore();

        try
        {
            return args[0] switch
            {
                "install" => await Install(packs, games, runtimes, http, args),
                "update" => await Update(packs, http, args),
                "run" => await Run(packs, games, runtimes, http, args),
                "command" => await Command(packs, args),
                "unit" => Unit(packs, args),
                "list" => List(packs),
                _ => Fail($"unknown command '{args[0]}' — try: cairn-server help"),
            };
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidDataException
                                      or InvalidOperationException or GameInstallException
                                      or DotnetRuntimeException)
        {
            return Fail(e.Message);
        }
    }

    private static void Usage()
    {
        Console.WriteLine("""
            cairn-server - runs a Vintage Story server on a Cairn pack

              cairn-server install <url|file> [--id <id>] [--follow|--fork]
                                                            follow a pack and install its server
              cairn-server run [<id>]                       sync, then run the server in the foreground
              cairn-server update [<id>]                    take the author's newer revision
              cairn-server command [<id>] <text>            send a console command to a running server
              cairn-server unit [<id>] [--user] [--write]   systemd unit for it
              cairn-server list                             packs on this machine

            With one pack installed, <id> can be left out.
            """);
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return 1;
    }

    /// <summary>
    /// The pack an argument names, or the only one there is.
    ///
    /// A box that hosts one world is the common case and naming it every time is noise; a
    /// box that hosts three has to be told which, rather than being guessed at.
    /// </summary>
    private static string Resolve(PackStore packs, string[] args, int at = 1)
    {
        var named = args.Length > at && !args[at].StartsWith('-') ? args[at] : null;

        if (named is not null)
        {
            if (!packs.Exists(named)) throw new InvalidOperationException($"no pack '{named}'");
            return named;
        }

        return SolePack(packs);
    }

    private static string SolePack(PackStore packs)
    {
        var all = packs.ListIds().ToList();

        return all.Count switch
        {
            1 => all[0],
            0 => throw new InvalidOperationException(
                "no packs installed — start with: cairn-server install <url>"),
            _ => throw new InvalidOperationException(
                $"which pack? {string.Join(", ", all)}"),
        };
    }

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    // ---- install ----

    private static async Task<int> Install(
        PackStore packs, GameStore games, RuntimeStore runtimes, HttpClient http, string[] args)
    {
        if (args.Length < 2)
            return Fail("usage: cairn-server install <url|file> [--id <id>] [--follow|--fork]");

        var source = args[1];

        if (args.Contains("--follow") && args.Contains("--fork"))
            return Fail("--follow and --fork ask for opposite things; pass one");

        // A pack decides which mods get installed, so it must not arrive over a connection
        // anyone on the path can rewrite. The same rule the other front-ends apply.
        if (PackSources.IsRewritableInFlight(source))
            return Fail("refusing to import over http; use https");

        // Through DocumentUrl, because a pack's canonical URL is the page a person reads
        // and that address serves HTML — the same rule the update check applies. Fetching
        // it raw got a web page and reported it as invalid JSON, which is true and tells
        // whoever pasted the URL nothing about what to paste instead.
        string json;
        string? fetchedFrom = null;

        if (PackSources.IsRemote(source))
        {
            using var response = await http.GetAsync(PackUpdateCheck.DocumentUrl(source));
            response.EnsureSuccessStatusCode();

            // The address it actually came from, so a follow is recorded against that
            // rather than against whatever the document names itself — and against where
            // the fetch landed rather than where it was aimed, because a redirect crosses
            // hosts silently. See PackSources.LandingAddress. A file gets none.
            fetchedFrom = PackSources.LandingAddress(response, PackUpdateCheck.DocumentUrl(source));
            json = await response.Content.ReadAsStringAsync();
        }
        else
        {
            json = File.ReadAllText(source);
        }

        var bundle = PackBundle.Parse(json);

        ImportIntent? intent = args.Contains("--fork") ? ImportIntent.Fork
            : args.Contains("--follow") ? ImportIntent.Follow
            : null;

        // A published document out of a file, with nobody having said which they meant. It
        // forks, because the only address on offer is the file's own word for where it
        // lives. Said out loud rather than done quietly: this is a server, nobody is
        // watching it, and `update` refuses outright on a pack that follows nothing — so
        // an administrator who wanted the other answer would otherwise find out weeks
        // later, from a pack that had silently stopped taking revisions.
        var unaskedFork = intent is null && fetchedFrom is null && bundle.IsPublished;

        var manifest = packs.Import(
            bundle, ArgValue(args, "--id"), sourceUrl: fetchedFrom, intent: intent);

        // What actually happened, read back rather than asserted. This used to say
        // "following" whatever it had just done, which for a file was the one case where
        // it had not.
        var link = packs.LoadLink(manifest.Id);
        var verb = link is { Role: PackRole.Follower } ? "following" : "installed";

        Console.WriteLine($"{verb} '{manifest.Id}' for game {manifest.GameVersion} "
                          + $"({manifest.Mods.Count} mods)");

        if (link is { Role: PackRole.Follower })
        {
            Console.WriteLine($"  taking revisions from {link.Url}");
        }
        else if (unaskedFork)
        {
            Console.WriteLine($"  this pack says it comes from {bundle.CanonicalUrl}");
            Console.WriteLine("  installed as a copy of its own, so 'update' has nothing to "
                              + "check back with");
            Console.WriteLine("  pass --follow to keep it in step with that address instead");
        }

        await Prepare(packs, games, runtimes, http, manifest.Id);

        Console.WriteLine();
        Console.WriteLine($"  run it now:      cairn-server run {manifest.Id}");
        Console.WriteLine($"  or as a service: cairn-server unit {manifest.Id} --write");
        return 0;
    }

    /// <summary>Syncs the pack and makes sure a server for its version can start.</summary>
    private static async Task<GameInstall> Prepare(
        PackStore packs, GameStore games, RuntimeStore runtimes, HttpClient http, string id)
    {
        var manifest = packs.Load(id);
        var moddb = new ModDbClient(http);

        var report = await new PackSyncer(moddb, http).SyncAsync(
            manifest, packs.ModsDir(id), packs.LockPath(id),
            new Progress<SyncStep>(s => Console.WriteLine($"  {Describe(s)}")),
            side: ModSide.Server);

        if (report.Failed)
            throw new InvalidOperationException("sync did not complete; not starting a server");

        var provisioner = new GameProvisioner(http, games, runtimes);
        var last = "";

        var server = await provisioner.EnsureServerAsync(
            manifest.GameVersion,
            new Progress<ProvisionStep>(p =>
            {
                if (p.Phase == "downloading") Console.Write($"\r  {p.Detail}      ");
                else if (p.Phase != last) Console.WriteLine($"\r  {p.Detail}          ");
                last = p.Phase;
            }));

        Console.WriteLine($"  server {server.Version} at {server.Directory}");
        return server;
    }

    private static string Describe(SyncStep step) => step.Action switch
    {
        SyncAction.Downloaded => $"+ {step.ModId} {step.Detail}",
        SyncAction.Updated => $"^ {step.ModId} {step.Detail}",
        SyncAction.Removed => $"- {step.ModId} {step.Detail}",
        SyncAction.Unchanged => $"= {step.ModId} {step.Detail}",
        SyncAction.Failed => $"! {step.ModId} {step.Detail}",
        SyncAction.Warned => $"? {step.ModId} {step.Detail}",
        _ => $"  {step.ModId} {step.Detail}",
    };

    // ---- update ----

    /// <summary>
    /// Takes the author's newer revision, on purpose and never on its own.
    ///
    /// A server follows a pack the way a player's copy does, but the consequence differs: a
    /// mod set that moves under a live world is a world that may not load, and nobody is
    /// sitting at the console to see it happen. So this is a command an administrator runs
    /// when they are ready for it, and <c>run</c> installs what the lock already says.
    /// </summary>
    private static async Task<int> Update(PackStore packs, HttpClient http, string[] args)
    {
        var id = Resolve(packs, args);
        var link = packs.LoadLink(id);

        if (!PackUpdateCheck.CanCheck(link))
            return Fail($"'{id}' does not follow anybody — nothing to update from");

        var bundle = await PackUpdateCheck.FetchAsync(link, http);
        if (bundle?.Pack is null)
            return Fail($"{link!.Url} did not answer with a pack");

        var plan = PackUpdatePlan.Between(
            packs.Load(id), bundle.Pack, packs.LoadUpstream(id),
            link!.Revision, bundle.Revision ?? 0, packs.LoadLocalState(id));

        if (!plan.AnyChange)
        {
            Console.WriteLine($"'{id}' is already on the author's newest revision");
            return 0;
        }

        Console.WriteLine(plan.Summary());
        packs.ApplyUpdate(id, plan, bundle);

        Console.WriteLine();
        Console.WriteLine($"updated '{id}' — the running server keeps the mods it started "
                          + "with until it is restarted");
        Console.WriteLine($"  restart it with: systemctl restart cairn-server@{id}");
        return 0;
    }

    // ---- run ----

    private static async Task<int> Run(
        PackStore packs, GameStore games, RuntimeStore runtimes, HttpClient http, string[] args)
    {
        var id = Resolve(packs, args);
        var socket = CairnPaths.ConsoleSocket(id);

        // One server per pack. Two sharing a data directory is a corrupted world, and the
        // socket is the cheapest thing that already exists per pack to ask with.
        if (await ServerConsole.SendAsync(socket, "/version"))
            return Fail($"a server for '{id}' is already running");

        var server = await Prepare(packs, games, runtimes, http, id);

        // The pack's mod config, on the side that mostly owns it: a good half of these
        // settings are server-side rules — who may loot a grave, how fast food spoils, what
        // view distance a client may ask for — and an admin following a pack should get the
        // author's answer without being told to go and edit files under ~/.cairn.
        //
        // ModConfigFiles directly rather than PackData.BeforeLaunch, which also merges a
        // login and applies hotkeys into clientsettings.json. There is no keyboard here and
        // no session to carry, and writing a client settings file next to a dedicated server
        // would be a file that nothing ever reads.
        //
        // Before Apply, so the pack's own values are not recorded as the mod's. Nothing on a
        // server reads the baseline today — it is the Mod config tab's, and this program has
        // no tab — but a pack directory is the same thing on both ends, and an admin who
        // copies one to a desktop to publish it should not find the tab unable to say what
        // they changed because the pack spent its life on a server.
        ModConfigFiles.Capture(packs.DataDir(id));

        foreach (var change in ModConfigFiles.Apply(
                     packs.DataDir(id), packs.Load(id).ModConfig, packs.ModsDir(id)))
            Console.WriteLine($"  {change.Describe()}");

        var options = new LaunchOptions
        {
            DataPath = packs.DataDir(id),
            ModPaths = { packs.ModsDir(id) },
            PreferredDotnetRoot = runtimes.RootFor(server),
        };

        var launcher = new GameLauncher(server);
        var runtime = launcher.ResolveRuntime(options);

        if (!runtime.Resolved)
            return Fail($"{server.Version} needs .NET {server.RequiredFramework} and none was found");

        Console.WriteLine($"using runtime {runtime.Describe()}");
        Console.WriteLine($"launching: {server.Executable} "
                          + $"{string.Join(' ', launcher.BuildArguments(options))}");

        var psi = launcher.BuildStartInfo(options);

        // stdin only. stdout and stderr stay ours, so journald captures the server's own
        // output with no relay in the middle to lose or reorder a line.
        psi.RedirectStandardInput = true;

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException($"could not start {server.Executable}");

        Console.WriteLine($"server started (pid {process.Id})");

        using var stopping = new CancellationTokenSource();
        var console = ServerConsole.ListenAsync(
            socket, line => WriteTo(process, line), stopping.Token);

        // SIGTERM is what systemctl stop sends, and killing a server mid-save loses what it
        // was saving. Turned into the server's own /stop, which flushes the world and exits.
        using var sigterm = PosixSignalRegistration.Create(
            PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; _ = Shutdown(process); });
        using var sigint = PosixSignalRegistration.Create(
            PosixSignal.SIGINT, ctx => { ctx.Cancel = true; _ = Shutdown(process); });

        _ = ForwardOwnInput(process, stopping.Token);

        await process.WaitForExitAsync();
        await stopping.CancelAsync();
        try { await console; } catch (OperationCanceledException) { }

        // Here as well as on the way in, and for the same reason PackData.AfterExit does it:
        // the first run of a pack is exactly the one where the mods' config files do not
        // exist yet when it starts, so this is the first moment they can be seen at all.
        ModConfigFiles.Capture(packs.DataDir(id));

        Console.WriteLine($"server exited with {process.ExitCode}");
        return process.ExitCode;
    }

    private static async Task Shutdown(Process process)
    {
        Console.WriteLine("stopping: asking the server to save and exit (/stop)");

        try
        {
            await process.StandardInput.WriteLineAsync("/stop");
            await process.StandardInput.FlushAsync();
        }
        catch (IOException)
        {
            // Already gone, or its stdin is closed. Nothing left worth doing gently.
        }
    }

    private static async Task WriteTo(Process process, string line)
    {
        Console.WriteLine($"console: {line}");
        await process.StandardInput.WriteLineAsync(line);
        await process.StandardInput.FlushAsync();
    }

    /// <summary>
    /// Passes this program's own stdin through, so a foreground run is a console.
    ///
    /// Ends quietly at end of input, which is the normal case under systemd: stdin is
    /// /dev/null there and reads nothing, and a service that stopped because its input was
    /// empty would be a strange thing to debug.
    /// </summary>
    private static async Task ForwardOwnInput(Process process, CancellationToken ct)
    {
        try
        {
            using var input = Console.OpenStandardInput();
            using var reader = new StreamReader(input);

            while (!ct.IsCancellationRequested
                   && await reader.ReadLineAsync(ct) is { } line)
            {
                await process.StandardInput.WriteLineAsync(line);
                await process.StandardInput.FlushAsync(ct);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        {
        }
    }

    // ---- command ----

    private static async Task<int> Command(PackStore packs, string[] args)
    {
        // The id is optional and the command is free text, so one of them has to give way:
        // a first argument is only an id when it names a pack *and* something follows it.
        // Otherwise "cairn-server command /whitelist add dizzy" reads its own command as
        // the name of a pack and reports that no such pack exists — which is true, useless,
        // and says nothing about what went wrong.
        var hasId = args.Length > 2 && packs.Exists(args[1]);
        var id = hasId ? args[1] : SolePack(packs);
        var text = string.Join(' ', args.Skip(hasId ? 2 : 1)).Trim();

        if (text.Length == 0)
            return Fail("usage: cairn-server command [<id>] <text>   e.g. \"/whitelist add dizzy\"");

        var sent = await ServerConsole.SendAsync(CairnPaths.ConsoleSocket(id), text);

        if (!sent)
            return Fail($"no server running for '{id}'");

        Console.WriteLine($"sent to '{id}': {text}");
        Console.WriteLine("output goes to the server's log — journalctl -u "
                          + $"cairn-server@{id} -n 20");
        return 0;
    }

    // ---- unit ----

    /// <summary>
    /// This program's path, as systemd will have to name it.
    ///
    /// Not simply Environment.ProcessPath: run as "dotnet cairn-server.dll" — which is how
    /// it is run while being developed — that is the path of the .NET host, and a unit
    /// built from it says ExecStart=/usr/…/dotnet run %i. That is not this program, it is a
    /// plausible-looking command that would do something else entirely, in a file nobody
    /// reads again until the service will not start.
    /// </summary>
    private static string SelfPath()
    {
        const string installed = "/usr/local/bin/cairn-server";

        var path = Environment.ProcessPath;
        var name = path is null ? null : Path.GetFileNameWithoutExtension(path);

        return name is not null
               && name.StartsWith("cairn-server", StringComparison.OrdinalIgnoreCase)
            ? path!
            : installed;
    }

    private static int Unit(PackStore packs, string[] args)
    {
        var id = Resolve(packs, args);
        var user = args.Contains("--user");

        var exec = ArgValue(args, "--exec") ?? SelfPath();

        var unit = new ServerUnit
        {
            ExecutablePath = exec,
            Scope = user ? UnitScope.User : UnitScope.System,
            User = user ? null : "cairn",
            Home = user ? null : "/var/lib/cairn",
        };

        if (!File.Exists(exec))
            Console.WriteLine($"# note: {exec} is not there yet — "
                              + "put the binary where ExecStart says, or pass --exec <path>");

        if (!args.Contains("--write"))
        {
            Console.WriteLine($"# {unit.FilePath}");
            Console.WriteLine();
            Console.Write(unit.Render());
            Console.WriteLine();
            Console.WriteLine("# write it with --write, or redirect this yourself");
            return 0;
        }

        try
        {
            Directory.CreateDirectory(unit.DirectoryPath);
            File.WriteAllText(unit.FilePath, unit.Render());
        }
        catch (UnauthorizedAccessException)
        {
            return Fail($"cannot write {unit.FilePath} — run with sudo, or use --user for a "
                        + "unit of your own");
        }

        Console.WriteLine($"wrote {unit.FilePath}");
        Console.WriteLine();
        Console.WriteLine("then:");
        foreach (var step in unit.NextSteps(id)) Console.WriteLine($"  {step}");
        return 0;
    }

    // ---- list ----

    private static int List(PackStore packs)
    {
        var ids = packs.ListIds().ToList();

        if (ids.Count == 0)
        {
            Console.WriteLine("no packs — start with: cairn-server install <url>");
            return 0;
        }

        foreach (var id in ids)
        {
            var manifest = packs.Load(id);
            var running = File.Exists(CairnPaths.ConsoleSocket(id)) ? "running" : "";
            Console.WriteLine($"  {id,-24} game {manifest.GameVersion,-10} "
                              + $"{manifest.Mods.Count,3} mods  {running}");
        }

        return 0;
    }
}
