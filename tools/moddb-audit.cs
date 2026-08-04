#:project ../src/Cairn.Core/Cairn.Core.csproj

// File-based apps switch reflection-based serialisation off by default, and Cairn's DTOs
// have no source-generated context. Without this the script dies inside the very call it
// is meant to be exercising.
#:property JsonSerializerIsReflectionEnabledByDefault=true

// Downloads every mod entry ModDB publishes and runs Cairn's own parser over the lot,
// to find the shapes the API serves that we do not survive.
//
// This exists because jaunt 3.0.0-rc.1 — a release whose file had been removed, served as
// "fileid": null — made its whole mod undeserialisable, and took down every pack that
// required jaunt. That was found by a user hitting it. One mod in eight thousand is not
// something to find that way twice, and the only way to know what else is out there is to
// read all eight thousand.
//
//   dotnet run tools/moddb-audit.cs -- fetch      # build the corpus (slow, resumable)
//   dotnet run tools/moddb-audit.cs -- check      # run the parser over it
//
// The check phase drives the real ModDbClient through a handler that serves the cached
// bytes, rather than reimplementing the parse. A script with its own copy of the rules
// would only ever prove that the copy agrees with itself.

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Cairn.Core.ModDb;

// ---- options -------------------------------------------------------------------------

var command = args.Contains("--help") || args.Contains("-h")
    ? "help"
    : args.FirstOrDefault(a => !a.StartsWith('-')) ?? "check";

var dir = Opt("--dir") ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "cairn-moddb");
var modsDir = Path.Combine(dir, "mods");
var indexPath = Path.Combine(dir, "index.json");

// One request per second by default. ModDB publishes no rate limit and sends no headers
// about one, so the only safe reading is that there is a person paying for the bandwidth.
// A full sweep is ~8000 requests: two and a bit hours, run once.
var delay = TimeSpan.FromMilliseconds(int.TryParse(Opt("--delay"), out var d) ? d : 1000);
var limit = int.TryParse(Opt("--limit"), out var l) ? l : int.MaxValue;
var refresh = args.Contains("--refresh");
var gameVersion = Opt("--game") ?? "1.22.5";

string? Opt(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

return command switch
{
    "fetch" => await FetchAsync(),
    "check" => Check(),
    _ => Usage(),
};

int Usage()
{
    Console.WriteLine("""
        moddb-audit — read every mod on ModDB and check Cairn can parse it

          fetch    download the corpus (resumable; re-running costs no requests)
          check    run Cairn's parser over the corpus and report what it cannot read

        options
          --dir <path>     corpus location (default ~/.cache/cairn-moddb)
          --delay <ms>     between requests, fetch only (default 1000)
          --limit <n>      stop after n downloads, for a sample run
          --refresh        re-fetch mods that have released since they were cached
          --game <version> game version the resolve check targets (default 1.22.5)
        """);
    return 1;
}

// ---- fetch ---------------------------------------------------------------------------

async Task<int> FetchAsync()
{
    Directory.CreateDirectory(modsDir);

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

    // Identifies the traffic, so whoever runs ModDB can see what this is and say no. An
    // anonymous eight thousand requests is the kind of thing that gets an IP blocked.
    http.DefaultRequestHeaders.UserAgent.ParseAdd(
        "cairn-moddb-audit/1.0 (+https://github.com/dizzyd/cairn-app)");

    // The index is one request for all eight thousand names, so it is always worth
    // re-reading: it is also what says which cached entries have gone stale.
    Console.WriteLine("fetching the mod index...");
    var indexJson = await GetAsync(http, "https://mods.vintagestory.at/api/mods");
    if (indexJson is null) return Fail("could not fetch the mod index");

    await File.WriteAllTextAsync(indexPath, indexJson);

    using var index = JsonDocument.Parse(indexJson);
    var entries = index.RootElement.GetProperty("mods").EnumerateArray()
        .Select(m => (
            Id: m.GetProperty("modid").GetInt32(),
            Released: m.TryGetProperty("lastreleased", out var r) ? r.GetString() : null))
        .ToList();

    Console.WriteLine($"{entries.Count} mods listed");

    var done = 0;
    var fetched = 0;
    var skipped = 0;
    var failed = new List<int>();
    var consecutiveFailures = 0;
    var clock = Stopwatch.StartNew();

    foreach (var (id, released) in entries)
    {
        if (fetched >= limit)
        {
            Console.WriteLine($"stopping at --limit {limit}");
            break;
        }

        done++;
        var path = Path.Combine(modsDir, $"{id}.json");

        if (File.Exists(path) && !(refresh && HasReleasedSince(path, released)))
        {
            skipped++;
            continue;
        }

        // Paced before the request rather than after, so a resumed run that skips a
        // thousand cached files does not then burst a thousand requests.
        if (fetched > 0) await Task.Delay(delay);

        var body = await GetAsync(http, $"https://mods.vintagestory.at/api/mod/{id}");
        if (body is null)
        {
            failed.Add(id);

            // A server that has stopped answering is a server to stop asking. Continuing
            // to hammer it for another six thousand mods is exactly the behaviour that
            // earns a block, and the corpus would be junk anyway.
            if (++consecutiveFailures >= 10)
                return Fail($"10 requests failed in a row at modid {id} — stopping");

            continue;
        }

        consecutiveFailures = 0;

        // Written via a temp file: a Ctrl-C midway through a write would otherwise leave
        // truncated JSON that the check phase reports as ModDB's fault.
        var tmp = path + ".partial";
        await File.WriteAllTextAsync(tmp, body);
        File.Move(tmp, path, overwrite: true);
        fetched++;

        if (fetched % 100 == 0)
        {
            var rate = fetched / clock.Elapsed.TotalSeconds;
            var left = TimeSpan.FromSeconds((entries.Count - done) / Math.Max(rate, 0.01));
            Console.WriteLine(
                $"  {done}/{entries.Count}  fetched {fetched}  skipped {skipped}  ~{left:hh\\:mm} left");
        }
    }

    Console.WriteLine($"\nfetched {fetched}, already had {skipped}, failed {failed.Count}");
    if (failed.Count > 0)
        Console.WriteLine($"  failed ids: {string.Join(", ", failed.Take(20))}"
                          + (failed.Count > 20 ? $" (+{failed.Count - 20} more)" : ""));
    Console.WriteLine($"corpus: {modsDir}");
    return 0;
}

/// <summary>Whether the index says this mod has released since we cached it.</summary>
static bool HasReleasedSince(string path, string? released)
{
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var cached = doc.RootElement.GetProperty("mod").TryGetProperty("lastreleased", out var r)
            ? r.GetString()
            : null;
        return cached != released;
    }
    catch (Exception e) when (e is JsonException or IOException or KeyNotFoundException)
    {
        return true;    // unreadable cache is a reason to fetch, not to keep
    }
}

/// <summary>
/// One GET, with the backing-off a public API deserves. Returns null when it did not
/// work out; the caller decides whether that is one bad mod or a reason to stop.
/// </summary>
static async Task<string?> GetAsync(HttpClient http, string url)
{
    // 5s, 15s, 45s. Slow enough to be a real retreat rather than three more requests.
    TimeSpan[] backoff = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(45)];

    for (var attempt = 0; ; attempt++)
    {
        try
        {
            using var resp = await http.GetAsync(url);

            if (resp.IsSuccessStatusCode)
                return await resp.Content.ReadAsStringAsync();

            // 404 is an answer, not a failure to retry — a mod can be delisted between
            // the index being read and its entry being asked for.
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;

            var retryable = resp.StatusCode == HttpStatusCode.TooManyRequests
                            || (int)resp.StatusCode >= 500;

            if (!retryable || attempt >= backoff.Length)
            {
                Console.WriteLine($"  ! {(int)resp.StatusCode} {url}");
                return null;
            }

            // Retry-After is the server saying how long it wants to be left alone, and
            // it outranks whatever we would have picked.
            var wait = resp.Headers.RetryAfter?.Delta
                       ?? (resp.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)
                       ?? backoff[attempt];

            Console.WriteLine($"  … {(int)resp.StatusCode}, waiting {wait.TotalSeconds:0}s");
            await Task.Delay(wait < TimeSpan.Zero ? backoff[attempt] : wait);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            if (attempt >= backoff.Length)
            {
                Console.WriteLine($"  ! {e.Message} {url}");
                return null;
            }

            await Task.Delay(backoff[attempt]);
        }
    }
}

static int Fail(string message)
{
    Console.Error.WriteLine($"error: {message}");
    return 1;
}

// ---- check ---------------------------------------------------------------------------

int Check()
{
    if (!Directory.Exists(modsDir))
        return Fail($"no corpus at {modsDir} — run 'fetch' first");

    var files = Directory.GetFiles(modsDir, "*.json").Order().ToArray();
    if (files.Length == 0) return Fail($"corpus at {modsDir} is empty");

    Console.WriteLine($"checking {files.Length} mods against Cairn's parser\n");

    // The real client, reading the corpus instead of the network. Everything under test —
    // the DTOs, the null tolerance, the fileless-release filter, the JsonException
    // translation — is the shipped code path, not a restatement of it.
    var served = new Served();
    var client = new ModDbClient(new HttpClient(served));

    var unreadable = new List<(string File, string Why)>();
    var noUsableRelease = new List<string>();
    var filelessOnly = new List<string>();
    var census = new Census();

    foreach (var file in files)
    {
        served.Body = File.ReadAllBytes(file);
        var name = Path.GetFileNameWithoutExtension(file);

        // The census reads the raw JSON rather than the parsed object, because the whole
        // question is what the API sends that the DTOs never see.
        census.Record(served.Body, unreadable, name);

        ModDbMod mod;
        try
        {
            mod = client.GetModAsync(name).GetAwaiter().GetResult();
        }
        catch (ModDbException e)
        {
            // "has no mod with id" is a delisted entry, not a parse failure.
            if (!e.Message.Contains("could not read")) continue;
            unreadable.Add((name, e.Message));
            continue;
        }

        if (mod.Releases.Count == 0) continue;

        // Everything below is about releases that parse but cannot be installed — the
        // silent half of the jaunt bug, which a crash-only check would call clean.
        if (mod.Releases.All(r => string.IsNullOrWhiteSpace(r.MainFile)))
        {
            filelessOnly.Add($"{name} ({mod.Name})");
            continue;
        }

        var tagged = mod.Releases.Any(r => r.Tags.Contains(gameVersion));
        var resolved = client.ResolveAsync(name, gameVersion).GetAwaiter().GetResult();

        if (tagged && resolved is null)
            noUsableRelease.Add($"{name} ({mod.Name})");
    }

    Report("Entries Cairn cannot read", unreadable.Select(u => $"{u.File}: {u.Why}"));
    Report($"Mods where every release has no file", filelessOnly);
    Report($"Mods tagged {gameVersion} that resolve to nothing", noUsableRelease);
    census.Report();

    var broken = unreadable.Count;
    Console.WriteLine(broken == 0
        ? $"\nNo entry in {files.Length} defeats the parser."
        : $"\n{broken} of {files.Length} entries cannot be read.");

    return broken == 0 ? 0 : 1;
}

static void Report(string title, IEnumerable<string> lines)
{
    var all = lines.ToList();
    Console.WriteLine($"== {title}: {all.Count} ==");

    foreach (var line in all.Take(25)) Console.WriteLine($"  {line}");
    if (all.Count > 25) Console.WriteLine($"  … and {all.Count - 25} more");
    Console.WriteLine();
}

/// <summary>Serves one cached body to the client, whatever it asks for.</summary>
sealed class Served : HttpMessageHandler
{
    public byte[] Body = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Body)
            {
                Headers = { ContentType = new("application/json") },
            },
        });
}

/// <summary>
/// Which JSON kinds the API actually sends for each field, counted across the corpus.
///
/// The crash list says what breaks today. This says what could: a field seen as Null even
/// once, on something the DTOs model as a plain int or a non-null string, is the next
/// jaunt waiting for the mod that uses it to become popular.
/// </summary>
sealed class Census
{
    // Mirrors ModDbDtos. Listed rather than reflected because what matters is the CLR
    // type we chose, and a mismatch here should be noticed when the DTOs change.
    private static readonly Dictionary<string, string> ModFields = new()
    {
        ["modid"] = "int", ["assetid"] = "int", ["name"] = "string",
        ["urlalias"] = "string?", ["author"] = "string?", ["logofile"] = "string?",
        ["tags"] = "list", ["side"] = "string?", ["type"] = "string?", ["releases"] = "list",
    };

    private static readonly Dictionary<string, string> ReleaseFields = new()
    {
        ["releaseid"] = "int?", ["fileid"] = "int?", ["mainfile"] = "string",
        ["filename"] = "string", ["modidstr"] = "string?", ["modversion"] = "string",
        ["tags"] = "list",
    };

    private readonly Dictionary<string, Dictionary<JsonValueKind, int>> _mod = [];
    private readonly Dictionary<string, Dictionary<JsonValueKind, int>> _release = [];
    private readonly Dictionary<string, string> _example = [];

    public void Record(byte[] body, List<(string, string)> unreadable, string name)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException e)
        {
            unreadable.Add((name, "not JSON at all: " + e.Message));
            return;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("mod", out var mod)
                || mod.ValueKind != JsonValueKind.Object) return;

            foreach (var p in mod.EnumerateObject())
            {
                if (p.Name == "releases") continue;
                Tally(_mod, ModFields, p, name, "mod");
            }

            if (!mod.TryGetProperty("releases", out var releases)
                || releases.ValueKind != JsonValueKind.Array) return;

            foreach (var release in releases.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object) continue;
                foreach (var p in release.EnumerateObject())
                    Tally(_release, ReleaseFields, p, name, "release");
            }
        }
    }

    private void Tally(
        Dictionary<string, Dictionary<JsonValueKind, int>> into,
        Dictionary<string, string> known,
        JsonProperty p, string modFile, string where)
    {
        if (!known.ContainsKey(p.Name)) return;    // fields Cairn does not model cannot bite it

        if (!into.TryGetValue(p.Name, out var kinds)) into[p.Name] = kinds = [];
        kinds[p.Value.ValueKind] = kinds.GetValueOrDefault(p.Value.ValueKind) + 1;

        if (Surprising(known[p.Name], p.Value.ValueKind))
            _example.TryAdd($"{where}.{p.Name}={p.Value.ValueKind}", modFile);
    }

    /// <summary>Whether this kind is one the CLR type cannot hold.</summary>
    private static bool Surprising(string type, JsonValueKind kind) => type switch
    {
        "int" => kind is not JsonValueKind.Number,
        "int?" => kind is not (JsonValueKind.Number or JsonValueKind.Null),
        "string" => kind is not JsonValueKind.String,
        "string?" => kind is not (JsonValueKind.String or JsonValueKind.Null),
        "list" => kind is not (JsonValueKind.Array or JsonValueKind.Null),
        _ => false,
    };

    public void Report()
    {
        Console.WriteLine("== Field shapes observed ==");
        Dump("mod", _mod, ModFields);
        Dump("release", _release, ReleaseFields);

        Console.WriteLine($"== Shapes the DTOs do not allow: {_example.Count} ==");
        if (_example.Count == 0)
            Console.WriteLine("  none — every field held a kind its CLR type accepts");

        foreach (var (what, example) in _example.OrderBy(e => e.Key))
            Console.WriteLine($"  {what,-40} first seen in {example}.json");

        Console.WriteLine();
    }

    private static void Dump(
        string label,
        Dictionary<string, Dictionary<JsonValueKind, int>> counts,
        Dictionary<string, string> types)
    {
        foreach (var (field, kinds) in counts.OrderBy(c => c.Key))
        {
            var shapes = string.Join("  ", kinds.OrderByDescending(k => k.Value)
                .Select(k => $"{k.Key}={k.Value:n0}"));
            Console.WriteLine($"  {label}.{field,-12} {types[field],-8} {shapes}");
        }

        Console.WriteLine();
    }
}
