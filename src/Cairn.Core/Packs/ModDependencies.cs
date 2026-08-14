using System.IO.Compression;
using System.Text.Json;

namespace Cairn.Core.Packs;

/// <summary>
/// The mods a downloaded mod requires, read out of its own <c>modinfo.json</c>.
///
/// ModDB does not publish this — neither its mod nor its release objects carry dependency
/// data, and the only trace is prose in changelog HTML. The zip is the only source, which
/// is why a pack's full mod set is not knowable until it has been downloaded.
/// </summary>
public static class ModDependencies
{
    /// <summary>
    /// Asset domains that ship with the game. A mod may depend on these, and asking ModDB
    /// for them finds nothing.
    /// </summary>
    private static readonly HashSet<string> BuiltIn =
        new(["game", "survival", "creative"], StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Lenient = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static bool IsBuiltIn(string modId) => BuiltIn.Contains(modId);

    /// <summary>
    /// The most a <c>modinfo.json</c> may be before Cairn refuses to read it.
    ///
    /// A mod zip is somebody else's archive, and this file is read on every sync — which
    /// means on every Play, and unattended under systemd on a server. Without a bound,
    /// <see cref="JsonDocument.Parse(Stream, JsonDocumentOptions)"/> over the entry's
    /// <see cref="DeflateStream"/> reads to the end into a doubling buffer and keeps it, so
    /// a ~1 MB entry — DEFLATE tops out near 1000:1 — becomes about a gigabyte resident and
    /// twice that at peak. The pack then fails in the same place on every retry and cannot
    /// be launched again without deleting the zip by hand, because the lock is never
    /// written.
    ///
    /// A megabyte is enormous for what this file is. Measured across a spread of the ~8,000
    /// mods ModDB lists, by reading each zip's central directory rather than downloading it:
    /// 38 real <c>modinfo.json</c> entries ran from 159 bytes to 649, median 329. The cap is
    /// therefore about 1,600 times the largest one anybody has actually published, which is
    /// the right side to err on — it exists to stop an allocation, not to police a format,
    /// and a mod that trips it is telling you something.
    /// </summary>
    public const int MaxModInfoBytes = 1024 * 1024;

    /// <summary>
    /// What a mod's zip said about its dependencies, and whether it could be asked.
    /// </summary>
    /// <param name="Problem">
    /// Null when the answer is trustworthy — including when the mod simply declares no
    /// dependencies. Non-null means the list is empty because nothing could be read, which
    /// is a different thing and has to be said out loud.
    /// </param>
    public sealed record Result(IReadOnlyList<string> Dependencies, string? Problem);

    /// <summary>
    /// Everything a mod's own <c>modinfo.json</c> is willing to say about it.
    ///
    /// Sync needs only the dependency ids, but a bug report wants the lot: what the mod
    /// calls itself is how you tell a renamed or repackaged zip from the one ModDB serves,
    /// and the declared version is how you tell a lockfile that has drifted from the file
    /// actually sitting on disk.
    /// </summary>
    /// <param name="Requires">
    /// Raw, in declaration order, with the versions kept and <c>game</c> left in. Sync
    /// strips those because it cannot install them; a reader wants to see them.
    /// </param>
    public sealed record ModInfoSummary(
        string? ModId,
        string? Name,
        string? Version,
        string? Type,
        IReadOnlyList<string> Authors,
        IReadOnlyList<KeyValuePair<string, string?>> Requires,
        string? Problem)
    {
        /// <summary>"genelib 3.2.0 'Genelib' by sekelsta", or whatever survives.</summary>
        public string Describe()
        {
            if (Problem is not null) return Lang.Get("deps-unreadable", Problem);

            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(ModId)) parts.Add(ModId!);
            if (!string.IsNullOrWhiteSpace(Version)) parts.Add(Version!);
            if (!string.IsNullOrWhiteSpace(Name)) parts.Add($"\"{Name}\"");
            if (Authors.Count > 0) parts.Add(Lang.Get("mods-by", string.Join(", ", Authors)));
            if (!string.IsNullOrWhiteSpace(Type)) parts.Add($"({Type})");

            return parts.Count == 0 ? Lang.Get("deps-no-modinfo") : string.Join(" ", parts);
        }

        public string DescribeRequires() => Requires.Count == 0
            ? Lang.Get("deps-nothing")
            : string.Join(", ", Requires.Select(
                r => r.Value is null ? r.Key : $"{r.Key} {r.Value}"));
    }

    /// <summary>
    /// Mod ids the zip at <paramref name="path"/> declares a dependency on, with the game's
    /// own domains removed.
    ///
    /// This used to swallow every failure and return an empty list, on the grounds that a
    /// mod whose archive is odd has already failed in a way the game reports better. That
    /// holds for a corrupt zip and not for the case that matters: a perfectly good zip
    /// whose <c>modinfo.json</c> the game accepts and this does not. The game parses with
    /// Newtonsoft, which also allows single-quoted strings and unquoted property names,
    /// so a mod can load in-game while its dependencies are invisible here — and the
    /// symptom is the game disabling it for a missing dependency nobody was told about,
    /// which is the exact failure reading this file at all is meant to prevent.
    ///
    /// So it still never throws, and it now says when the emptiness is ignorance.
    /// </summary>
    public static Result Read(string path)
    {
        var info = Describe(path);

        return new Result(
            [.. info.Requires.Select(r => r.Key)
                .Where(id => !string.IsNullOrWhiteSpace(id) && !IsBuiltIn(id))],
            info.Problem);
    }

    /// <summary>
    /// Reads the whole of a mod's <c>modinfo.json</c>. The one place that knows how to open
    /// a mod zip, so <see cref="Read"/> and a diagnostics report cannot disagree about
    /// whether a given file is readable.
    /// </summary>
    public static ModInfoSummary Describe(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);

            // Root only, as the game requires. Ordinal-ignore-case because authors write
            // ModInfo.json, modInfo.json and modinfo.json in roughly equal measure.
            var entry = zip.Entries.FirstOrDefault(
                e => string.Equals(e.FullName, "modinfo.json", StringComparison.OrdinalIgnoreCase));

            // Not a problem to report. A zip with no modinfo.json at its root is not a mod
            // the game would load either, so it is already being reported by something in
            // a far better position to explain it — and warning here would fire on every
            // sync for every pack carrying one.
            if (entry is null) return Empty(null);

            // What the archive says it will be, before a byte is decompressed. This is the
            // cheap half and it costs nothing on an ordinary mod.
            if (entry.Length > MaxModInfoBytes) return Empty(TooBig(entry.Length));

            using var stream = entry.Open();

            // And what actually arrives. Belt and braces rather than the load-bearing
            // check: .NET bounds a read-mode entry stream at the declared uncompressed
            // length, so a zip that understates its entry truncates instead of inflating
            // and lands in the JsonException path below — asserted in ModInfoSizeTests,
            // because it is a framework guarantee this leans on rather than anything here.
            // Counting anyway costs nothing and is what would hold if that ever changed.
            // Reading one byte past the cap distinguishes "exactly at the limit" from
            // "more than we will take", and bounds the allocation either way.
            var bytes = BoundedRead.AtMost(stream, MaxModInfoBytes + 1);

            if (bytes.Length > MaxModInfoBytes) return Empty(TooBig(null));

            // Parsed from a stream over the bytes already in hand rather than from the
            // decompressor, so the bound above is what limits memory. Still a Stream and
            // not the ReadOnlyMemory overload: that one rejects a UTF-8 BOM, and mod
            // authors on Windows write plenty of them.
            using var bounded = new MemoryStream(bytes, writable: false);
            using var doc = JsonDocument.Parse(bounded, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            var root = doc.RootElement;

            return new ModInfoSummary(
                Text(root, "modid"),
                Text(root, "name"),
                Text(root, "version"),
                Text(root, "type"),
                Strings(root, "authors"),
                Pairs(root, "dependencies"),
                null);
        }
        catch (JsonException e)
        {
            // The case worth naming: the file is there and we cannot read it, so any
            // dependency it declares is one this pack will not install.
            return Empty(Lang.Get("deps-modinfo-unreadable", e.Message));
        }
        catch (Exception e) when (e is IOException or InvalidDataException
                                      or UnauthorizedAccessException)
        {
            return Empty(Lang.Get("deps-zip-unopenable", e.Message));
        }

        static ModInfoSummary Empty(string? problem) => new(null, null, null, null, [], [], problem);

        // Phrased like the other problems here: what was not read, and what that costs.
        // The declared size is quoted when there is one, because "declares 1.1 GB" and
        // "kept coming" are different things to whoever has to look at the mod.
        static string TooBig(long? declared) => declared is { } n
            ? Lang.Get("deps-modinfo-too-big-size", Bytes.Human(n))
            : Lang.Get("deps-modinfo-too-big");
    }


    /// <summary>A string property, whatever case the author wrote it in.</summary>
    private static string? Text(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;

        var found = obj.EnumerateObject().FirstOrDefault(
            p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        return found.Value.ValueKind == JsonValueKind.String ? found.Value.GetString() : null;
    }

    /// <summary>
    /// A list of strings, tolerating the author who wrote one bare string instead — the
    /// game accepts both for <c>authors</c>, so a report that refused would lose the name.
    /// </summary>
    private static IReadOnlyList<string> Strings(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return [];

        var found = obj.EnumerateObject().FirstOrDefault(
            p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        return found.Value.ValueKind switch
        {
            JsonValueKind.String => [found.Value.GetString()!],
            JsonValueKind.Array =>
                [.. found.Value.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)],
            _ => [],
        };
    }

    /// <summary>An id → declared-minimum map, kept in the order the author wrote it.</summary>
    private static IReadOnlyList<KeyValuePair<string, string?>> Pairs(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return [];

        var found = obj.EnumerateObject().FirstOrDefault(
            p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        if (found.Value.ValueKind != JsonValueKind.Object) return [];

        return [.. found.Value.EnumerateObject().Select(p => new KeyValuePair<string, string?>(
            p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null))];
    }

    /// <summary>
    /// Pulls the ids out of a parsed <c>modinfo.json</c>. Split out so it can be tested
    /// without building a zip.
    /// </summary>
    public static IReadOnlyList<string> Parse(JsonElement modInfo)
    {
        if (modInfo.ValueKind != JsonValueKind.Object) return [];

        // The key is optional — plenty of mods omit it entirely rather than writing {}.
        var dependencies = modInfo.EnumerateObject().FirstOrDefault(
            p => string.Equals(p.Name, "dependencies", StringComparison.OrdinalIgnoreCase));

        if (dependencies.Value.ValueKind != JsonValueKind.Object) return [];

        // A map of id -> minimum version. The version is deliberately dropped: it is a
        // minimum rather than a pin, and it goes through the same splitter that reads
        // "1.0.0-pre.8" as [1, 0, 0, 0, 8], so it is not a value to resolve against.
        return [.. dependencies.Value.EnumerateObject()
            .Select(p => p.Name)
            .Where(id => !string.IsNullOrWhiteSpace(id) && !IsBuiltIn(id))];
    }
}
