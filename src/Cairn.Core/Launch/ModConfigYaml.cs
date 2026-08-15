using Cairn.Core;
using System.Text;
using System.Text.Json.Nodes;

namespace Cairn.Core.Launch;

/// <summary>
/// Reads and edits the flat YAML config files ConfigLib writes, without a YAML library and
/// without disturbing a byte it was not asked to change.
///
/// ConfigLib (Maltiez) is how a great many mods get a settings screen, and for the ones that
/// do not keep their own JSON config it stores the values itself, in
/// <c>ModConfig/&lt;domain&gt;.yaml</c>. In a real 74-mod pack that is seven mods — every
/// YAML file present, and the single largest reason a pack could not carry a mod setting.
///
/// **This is not a YAML parser and must never become one.** The files are generated from
/// each mod's <c>configlib-patches.json</c>, and generated to one shape: a
/// <c>version</c>, section banners as comment blocks, and <c>key: value</c> at column zero
/// with the description on the line before and a <c>(default: …)</c> note on the line after.
/// Measured across those seven files: 125 key/value lines, zero nested lines, and values
/// only ever <c>true</c>, <c>false</c>, an integer, a decimal or a quoted string.
///
/// So the whole of it is: understand that shape exactly, and <b>refuse anything else</b>. A
/// file with a list or a nested mapping in it is a file this cannot safely edit, and saying
/// so costs one mod's settings — where guessing costs somebody the config of a mod they are
/// playing. That is the same trade the comment rule in <see cref="ModConfigFiles"/> makes.
/// </summary>
internal static class ModConfigYaml
{
    /// <summary>
    /// ConfigLib's own key, and the one thing here that must never be written.
    ///
    /// <c>Config.Parse</c> compares it against the version in the mod's patch file, and on a
    /// mismatch does not merely decline the file — it calls <c>WriteConfigFile(defaultYaml)</c>
    /// and overwrites every setting in it with the mod's defaults. A pack that could set this
    /// would be a pack that could wipe somebody's config.
    /// </summary>
    public const string VersionKey = "version";

    /// <summary>
    /// The values in the file, or why it cannot be used.
    ///
    /// Flat by construction: a ConfigLib file has no sections, so the object is one level of
    /// scalars — which is exactly what the rest of the mod config code already works in.
    /// </summary>
    public static (JsonObject? Values, Message? Why) Parse(string text)
    {
        var values = new JsonObject();

        foreach (var line in Lines(text))
        {
            var content = text.AsSpan(line.Start, line.Length);

            if (content.IsWhiteSpace()) continue;
            if (content.TrimStart().StartsWith("#")) continue;

            if (KeyOf(content) is not { } key)
                return (null, new Message("modconfig-why-yaml"));

            if (values.ContainsKey(key.Name))
                return (null, new Message("modconfig-why-yaml-duplicate", key.Name));

            var (value, ok) = Scalar(content[key.ValueStart..]);
            if (!ok) return (null, new Message("modconfig-why-yaml"));

            values[key.Name] = value;
        }

        return (values, null);
    }

    /// <summary>
    /// Writes new values onto the lines that already hold them, and returns the whole file.
    ///
    /// Everything else is copied verbatim — the section banners, the descriptions, the
    /// <c>(default: …)</c> notes, the blank lines, the line endings. ConfigLib regenerates
    /// all of it on its next load anyway, but between Cairn writing and the game running,
    /// what is on disk is what a person opening the file sees, and a launcher that reformats
    /// somebody's config to change one number in it has done more than it said.
    /// </summary>
    /// <param name="writes">
    /// Keys that are known to exist in the file — <see cref="ModConfigFiles"/> never asks for
    /// one that does not, because ConfigLib's YAML save regenerates from its own settings and
    /// silently drops any key it does not recognise.
    /// </param>
    public static string Apply(string text, IReadOnlyDictionary<string, JsonNode?> writes)
    {
        var result = new StringBuilder(text.Length);
        var at = 0;

        foreach (var line in Lines(text))
        {
            // Everything between the last line and this one, which is the line terminator.
            result.Append(text, at, line.Start - at);
            at = line.Start + line.Length;

            var content = text.AsSpan(line.Start, line.Length);

            if (KeyOf(content) is { } key && Lookup(writes, key.Name) is { } replacement)
            {
                var (_, ok) = Scalar(content[key.ValueStart..]);

                if (ok)
                {
                    // Prefix is the key and its colon; suffix is whatever followed the value
                    // on the same line, which for a ConfigLib file is nothing and for a
                    // hand-edited one may be a comment.
                    var tail = content[key.ValueStart..];
                    var lead = tail.Length - tail.TrimStart().Length;
                    var end = key.ValueStart + lead + ValueLength(tail.TrimStart());

                    result.Append(text, line.Start, key.ValueStart + lead);
                    result.Append(Format(replacement));
                    result.Append(text, line.Start + end, line.Length - end);
                    continue;
                }
            }

            result.Append(text, line.Start, line.Length);
        }

        result.Append(text, at, text.Length - at);
        return result.ToString();
    }

    /// <summary>Whether the file names this setting, which is whether it may be written.</summary>
    public static bool Has(JsonObject values, string key) => Lookup(values, key) is not null;

    /// <summary>
    /// The file ConfigLib would have written, from the schema in the mod's own patch file.
    ///
    /// Deliberately the mod's <em>defaults</em> and not the pack's values, even though the
    /// pack's are what we are here for. Written this way the seeded file is the same starting
    /// point a launch that waited for ConfigLib would have had, and everything downstream —
    /// which values get applied, what is reported, what the record says somebody owns — is
    /// decided by the one merge that decides it for every other file. Writing the pack's
    /// values directly would make the merge find nothing to do and say nothing, and a launch
    /// that silently agreed with itself is how the last bug in here stayed invisible.
    ///
    /// None of ConfigLib's presentation: no section banners, no descriptions, no
    /// <c>(default: …)</c> notes. Those come from the mod's lang assets and are regenerated
    /// wholesale the first time it saves. A partial file is honoured as long as the version
    /// matches — <c>if (!values.ContainsKey(setting.YamlCode)) continue;</c> — so the two
    /// lines of provenance are worth more here than a reproduction that would be replaced
    /// within the session anyway.
    /// </summary>
    public static string Seed(ConfigLibSchema schema)
    {
        var text = new StringBuilder();

        text.Append("# Written by Cairn from this mod's own configlib-patches.json, before the\n");
        text.Append("# mod first ran. ConfigLib rewrites it in full the first time it loads.\n");
        text.Append($"{VersionKey}: {schema.Version}\n");

        foreach (var (key, value) in schema.Defaults)
            text.Append($"{key}: {Format(value)}\n");

        return text.ToString();
    }

    private static JsonNode? Lookup(IReadOnlyDictionary<string, JsonNode?> source, string key)
    {
        foreach (var (existing, value) in source)
            if (string.Equals(existing, key, StringComparison.OrdinalIgnoreCase))
                return value ?? JsonValue.Create((string?)null);

        return null;
    }

    private static JsonNode? Lookup(JsonObject source, string key)
    {
        foreach (var (existing, value) in source)
            if (string.Equals(existing, key, StringComparison.OrdinalIgnoreCase))
                return value ?? JsonValue.Create((string?)null);

        return null;
    }

    private readonly record struct Line(int Start, int Length);

    private static IEnumerable<Line> Lines(string text)
    {
        var start = 0;

        while (start <= text.Length)
        {
            var end = text.IndexOf('\n', start);
            if (end < 0) end = text.Length;

            var length = end - start;

            // The \r of a \r\n stays out of the content and is copied with the terminator.
            if (length > 0 && text[start + length - 1] == '\r') length--;

            yield return new Line(start, length);

            if (end == text.Length) yield break;
            start = end + 1;
        }
    }

    private readonly record struct Key(string Name, int ValueStart);

    /// <summary>
    /// The key a line declares, or null when the line is not a flat <c>key: value</c> at
    /// column zero.
    ///
    /// Indentation is what makes a line part of a mapping or a list rather than a setting of
    /// its own, so a leading space is enough to say this is not a shape this understands.
    /// </summary>
    private static Key? KeyOf(ReadOnlySpan<char> content)
    {
        if (content.Length == 0 || content[0] is ' ' or '\t' or '-') return null;

        var colon = content.IndexOf(':');
        if (colon <= 0) return null;

        var name = content[..colon];
        foreach (var c in name)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.'))
                return null;

        return new Key(name.ToString(), colon + 1);
    }

    /// <summary>
    /// The value a line holds, and whether it is one of the shapes this understands.
    ///
    /// A mapping key with nothing after it — <c>settings:</c> followed by an indented block —
    /// is deliberately not a null value but a refusal: it is the one line that would tell us
    /// the file is nested, and reading it as "null" would let the edit go ahead.
    /// </summary>
    private static (JsonNode? Value, bool Ok) Scalar(ReadOnlySpan<char> tail)
    {
        var text = tail.Trim();

        if (text.Length == 0) return (null, false);
        if (text[0] is '[' or '{' or '&' or '*' or '|' or '>') return (null, false);

        if (text[0] is '\'' or '"')
        {
            var quoted = Unquote(text);
            return quoted is null ? (null, false) : (JsonValue.Create(quoted), true);
        }

        // An unquoted scalar ends at " #", which is how YAML starts a trailing comment.
        var comment = text.IndexOf(" #", StringComparison.Ordinal);
        if (comment >= 0) text = text[..comment].TrimEnd();

        if (text.Length == 0) return (null, false);

        if (text.SequenceEqual("~") || text.Equals("null", StringComparison.OrdinalIgnoreCase))
            return (null, true);

        if (text.Equals("true", StringComparison.OrdinalIgnoreCase)) return (JsonValue.Create(true), true);
        if (text.Equals("false", StringComparison.OrdinalIgnoreCase)) return (JsonValue.Create(false), true);

        if (long.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var whole))
            return (JsonValue.Create(whole), true);

        if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var real))
            return (JsonValue.Create(real), true);

        return (JsonValue.Create(text.ToString()), true);
    }

    /// <summary>How much of the line the value occupies, so the rest can be copied through.</summary>
    private static int ValueLength(ReadOnlySpan<char> text)
    {
        if (text.Length == 0) return 0;

        if (text[0] is '\'' or '"')
        {
            var quote = text[0];

            for (var i = 1; i < text.Length; i++)
            {
                if (text[i] != quote) continue;

                // '' inside a single-quoted scalar is an escaped quote, not the end of it.
                if (quote == '\'' && i + 1 < text.Length && text[i + 1] == '\'') { i++; continue; }

                return i + 1;
            }

            return text.Length;
        }

        var comment = text.IndexOf(" #", StringComparison.Ordinal);
        return comment >= 0 ? text[..comment].TrimEnd().Length : text.TrimEnd().Length;
    }

    private static string? Unquote(ReadOnlySpan<char> text)
    {
        var quote = text[0];
        var length = ValueLength(text);

        // Unterminated, or something following the closing quote that is not a comment.
        if (length < 2 || text[length - 1] != quote) return null;

        var rest = text[length..].Trim();
        if (rest.Length > 0 && rest[0] != '#') return null;

        var inner = text[1..(length - 1)].ToString();
        return quote == '\'' ? inner.Replace("''", "'") : inner.Replace("\\\"", "\"");
    }

    /// <summary>
    /// A value written the way ConfigLib writes them, so a file Cairn has touched and one it
    /// has not differ only in the number.
    /// </summary>
    private static string Format(JsonNode? value) => value switch
    {
        null => "''",
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonValue v when v.TryGetValue<string>(out var s) => $"'{s.Replace("'", "''")}'",

        // Numbers keep the text they arrived with: 2 stays 2 rather than becoming 2.0, which
        // is what a manifest diff would otherwise show on a value nobody touched.
        _ => value.ToJsonString(),
    };
}
