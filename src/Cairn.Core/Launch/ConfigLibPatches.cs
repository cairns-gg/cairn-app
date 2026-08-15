using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cairn.Core.Launch;

/// <summary>
/// What a mod's own <c>configlib-patches.json</c> says about the YAML file ConfigLib
/// generates for it: the version number, and every setting's name and shipped default.
/// </summary>
/// <param name="Version">
/// ConfigLib's <c>version</c>, which must appear at the top of the file and must match, or
/// ConfigLib overwrites every setting in it. See <see cref="ModConfigYaml.VersionKey"/>.
/// </param>
/// <param name="Defaults">
/// Setting name to the value the mod ships, in the order the patch file lists them. The name
/// is ConfigLib's <c>YamlCode</c> — measured against a real pack, the names in the schema and
/// the keys in the generated file agree exactly.
/// </param>
internal sealed record ConfigLibSchema(int Version, IReadOnlyList<KeyValuePair<string, JsonNode?>> Defaults);

/// <summary>
/// Reads ConfigLib's schema out of the mod zips a pack has already downloaded, so a config
/// file can be written before the mod that owns it has ever run.
///
/// This exists to close one gap. ConfigLib generates <c>ModConfig/&lt;domain&gt;.yaml</c> the
/// first time a mod loads, so a pack's value for one of those settings could only land on the
/// launch *after* the first — and for a setting that feeds worldgen, "one launch later" is a
/// world that was generated with the wrong answer and has to be thrown away. On a dedicated
/// server following a pack that is the whole point of the pack, missed.
///
/// Seeding the file was ruled out while the version number looked unknowable from outside the
/// game — a wrong one makes <c>Config.Parse</c> call <c>WriteConfigFile(defaultConfig)</c> and
/// overwrite every setting with the mod's defaults, so guessing it could wipe somebody's
/// config. It is not unknowable. It is the <c>version</c> field of the same
/// <c>configlib-patches.json</c> the file is generated from, inside a zip Cairn already opens
/// for <see cref="Hotkeys.HotkeyCatalog"/>, and it agrees with the generated file exactly.
///
/// Measured across the 11 mods shipping a patch file in a real 63-mod pack: every one carries
/// a <c>version</c>, and every setting in a dict-shaped <c>settings</c> block carries both a
/// <c>name</c> and a <c>default</c> — 118 settings, none missing either.
/// </summary>
internal static class ConfigLibPatches
{
    /// <summary>
    /// A schema is a handful of settings with a label and a range each. The largest in a real
    /// pack is betterruins at 65 settings and 34KB; the cap is well clear of anything real and
    /// is here because this parses a stream out of an archive somebody else built.
    /// </summary>
    private const int MaxPatchBytes = 512 * 1024;

    /// <summary>
    /// The schema for <c>&lt;domain&gt;.yaml</c>, or null when there is nothing to be sure of.
    ///
    /// Null rather than a guess in every doubtful case: no zip carries the domain's patch
    /// file, the file will not parse, its <c>version</c> is missing, or it has no settings this
    /// understands. The caller waits for ConfigLib to write the file itself, which is what
    /// happened before this existed and is always safe.
    /// </summary>
    public static ConfigLibSchema? For(string modsDir, string domain)
    {
        var wanted = $"assets/{domain}/config/configlib-patches.json";

        foreach (var zip in Zips(modsDir))
        {
            try
            {
                using var archive = ZipFile.OpenRead(zip);

                var entry = archive.Entries.FirstOrDefault(
                    e => e.FullName.Equals(wanted, StringComparison.OrdinalIgnoreCase));

                if (entry is null || entry.Length > MaxPatchBytes) continue;

                using var raw = entry.Open();
                var bytes = BoundedRead.AtMost(raw, MaxPatchBytes + 1);
                if (bytes.Length > MaxPatchBytes) continue;

                if (Parse(bytes) is { } schema) return schema;
            }
            catch (Exception e) when (e is IOException or InvalidDataException
                                          or UnauthorizedAccessException or JsonException)
            {
                // An unreadable zip is one mod's settings arriving a launch later, which is
                // exactly where this started. Never a reason to fail a launch.
            }
        }

        return null;
    }

    private static ConfigLibSchema? Parse(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        // A patch file naming a `file` is one where ConfigLib is only a settings screen over
        // the mod's own JSON config — four of the eleven in a real pack. It generates no
        // <domain>.yaml at all, so writing one would be a file nothing ever reads, holding
        // settings that go on being wrong.
        if (root.TryGetProperty("file", out _)) return null;

        if (!root.TryGetProperty("version", out var version)
            || !version.TryGetInt32(out var number)) return null;

        if (!root.TryGetProperty("settings", out var settings)
            || settings.ValueKind != JsonValueKind.Object) return null;

        var defaults = new List<KeyValuePair<string, JsonNode?>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Grouped by type — boolean, integer, float, number, string — and the group names are
        // not a fixed list to match against. Only the shape of what is inside one matters.
        foreach (var group in settings.EnumerateObject())
        {
            if (group.Value.ValueKind != JsonValueKind.Object) continue;

            foreach (var setting in group.Value.EnumerateObject())
            {
                if (setting.Value.ValueKind != JsonValueKind.Object) continue;

                if (!setting.Value.TryGetProperty("name", out var name)
                    || name.ValueKind != JsonValueKind.String
                    || name.GetString() is not { Length: > 0 } code) continue;

                if (!setting.Value.TryGetProperty("default", out var shipped)) continue;

                // The same rule ModConfigYaml applies to a file it reads, applied to a file it
                // is about to write: a scalar on one line, or nothing. Measured over 191
                // settings in every ConfigLib mod to hand, defaults are only ever a number, a
                // bool or a string — but a list or a mapping would be written as JSON and the
                // next launch would refuse to read back the file this one wrote.
                if (shipped.ValueKind is not (JsonValueKind.True or JsonValueKind.False
                    or JsonValueKind.Number or JsonValueKind.String)) continue;

                // A name that is not a flat key is the same problem from the other side. Both
                // skip the one setting rather than the file: ConfigLib fills in whatever is
                // missing, so the rest of the pack's values still land a launch earlier.
                if (!IsFlatKey(code)) continue;

                // Two groups naming one setting is a file we do not understand, and the YAML
                // it generates could only hold one of them. First wins, and the duplicate is
                // dropped rather than being allowed to decide which.
                if (!seen.Add(code)) continue;

                defaults.Add(new(code, JsonNode.Parse(shipped.GetRawText())));
            }
        }

        return defaults.Count == 0 ? null : new ConfigLibSchema(number, defaults);
    }

    /// <summary>
    /// Whether a name can be written as <c>key: value</c> at column zero and read back as the
    /// same key. Deliberately the character set <c>ModConfigYaml.KeyOf</c> accepts, since a
    /// name outside it is one this could write and then be unable to edit.
    /// </summary>
    private static bool IsFlatKey(string name)
    {
        foreach (var c in name)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.')) return false;

        return true;
    }

    private static IEnumerable<string> Zips(string modsDir)
    {
        string[] files;

        try
        {
            files = Directory.Exists(modsDir) ? Directory.GetFiles(modsDir, "*.zip") : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        return files;
    }
}
