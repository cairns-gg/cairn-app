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
    /// What a mod's zip said about its dependencies, and whether it could be asked.
    /// </summary>
    /// <param name="Problem">
    /// Null when the answer is trustworthy — including when the mod simply declares no
    /// dependencies. Non-null means the list is empty because nothing could be read, which
    /// is a different thing and has to be said out loud.
    /// </param>
    public sealed record Result(IReadOnlyList<string> Dependencies, string? Problem);

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
            if (entry is null) return new Result([], null);

            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            return new Result(Parse(doc.RootElement), null);
        }
        catch (JsonException e)
        {
            // The case worth naming: the file is there and we cannot read it, so any
            // dependency it declares is one this pack will not install.
            return new Result([],
                $"its modinfo.json could not be read, so any mods it requires are not "
                + $"installed ({e.Message})");
        }
        catch (Exception e) when (e is IOException or InvalidDataException
                                      or UnauthorizedAccessException)
        {
            return new Result([], $"its zip could not be opened ({e.Message})");
        }
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
