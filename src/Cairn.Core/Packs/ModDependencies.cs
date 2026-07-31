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
    /// Mod ids the zip at <paramref name="path"/> declares a dependency on, with the game's
    /// own domains removed. Empty when the file is unreadable, is not a zip, carries no
    /// <c>modinfo.json</c>, or declares no dependencies — all of which are ordinary.
    ///
    /// Deliberately silent about a zip it cannot read. Sync calls this for every installed
    /// mod on every run, and a mod whose archive is odd has already failed in a way the
    /// game will report far better than a guess from here would.
    /// </summary>
    public static IReadOnlyList<string> Read(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);

            // Root only, as the game requires. Ordinal-ignore-case because authors write
            // ModInfo.json, modInfo.json and modinfo.json in roughly equal measure.
            var entry = zip.Entries.FirstOrDefault(
                e => string.Equals(e.FullName, "modinfo.json", StringComparison.OrdinalIgnoreCase));

            if (entry is null) return [];

            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            return Parse(doc.RootElement);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException
                                      or UnauthorizedAccessException)
        {
            return [];
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
