using System.Globalization;
using System.Text;

namespace Cairn.Core.Packs;

/// <summary>
/// Turns a display name into a pack id.
///
/// The id is a directory name, and it travels in shared bundles, so it has to stay in a
/// narrow ASCII alphabet. That is a machine's constraint, not something worth asking a
/// person about — so a name is all anyone types, and the id is derived from it.
/// </summary>
public static class PackId
{
    public const int MaxLength = 64;

    /// <summary>Used when a name has nothing usable in it at all, e.g. "日本語" or "!!!".</summary>
    public const string Fallback = "pack";

    /// <summary>
    /// The slug for <paramref name="name"/>, or "" when nothing survives. Idempotent:
    /// slugging an id returns that id, so a caller can pass either without checking.
    /// </summary>
    public static string From(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";

        var trimmed = name.Trim();
        var slug = new StringBuilder(trimmed.Length);

        foreach (var ch in trimmed)
        {
            // A name that arrived already decomposed carries its accents as separate
            // combining marks; drop them rather than turning each into a separator.
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;

            if (char.IsAsciiLetterOrDigit(ch))
            {
                slug.Append(char.ToLowerInvariant(ch));
                continue;
            }

            if (Fold(ch) is { } folded)
            {
                slug.Append(folded);
                continue;
            }

            // Everything else becomes a separator, including '_', so two names differing
            // only in punctuation cannot produce two different-looking ids.
            if (slug.Length > 0 && slug[^1] != '-') slug.Append('-');
        }

        return Trim(slug.ToString());
    }

    /// <summary>
    /// The ASCII letter an accented one stands for, or null if there is none.
    ///
    /// Done with a table rather than Normalize(FormD): the projects set
    /// InvariantGlobalization, where normalisation is silently a no-op, so "Café" would
    /// quietly slug to "caf". A table also keeps the mapping identical on every machine,
    /// which matters because an id is a directory name that travels in shared bundles —
    /// the same name must produce the same id whether or not ICU happens to be present.
    /// </summary>
    private static string? Fold(char ch)
    {
        var index = Accented.IndexOf(ch);
        if (index >= 0) return char.ToLowerInvariant(Folded[index]).ToString();

        // Letters that stand for more than one, so they cannot live in the table above.
        return char.ToLowerInvariant(ch) switch
        {
            'ß' => "ss",
            'æ' => "ae",
            'œ' => "oe",
            'ø' => "o",
            'đ' or 'ð' => "d",
            'ł' => "l",
            'þ' => "th",
            _ => null,
        };
    }

    // Latin-1 Supplement and Latin Extended-A, generated from Unicode decomposition and
    // pinned by a test that checks the two strings stay the same length — a mismatch
    // would silently shift every mapping after the edit.
    private const string Accented =
        "ÀÁÂÃÄÅÇÈÉÊËÌÍÎÏÑÒÓÔÕÖÙÚÛÜÝàáâãäåçèéêëìíîïñòóôõöù" +
        "úûüýÿĀāĂăĄąĆćĈĉĊċČčĎďĒēĔĕĖėĘęĚěĜĝĞğĠġĢģĤĥĨĩĪīĬĭĮ" +
        "įİĴĵĶķĹĺĻļĽľŃńŅņŇňŌōŎŏŐőŔŕŖŗŘřŚśŜŝŞşŠšŢţŤťŨũŪūŬŭ" +
        "ŮůŰűŲųŴŵŶŷŸŹźŻżŽž";

    private const string Folded =
        "AAAAAACEEEEIIIINOOOOOUUUUYaaaaaaceeeeiiiinooooou" +
        "uuuyyAaAaAaCcCcCcCcDdEeEeEeEeEeGgGgGgGgHhIiIiIiI" +
        "iIJjKkLlLlLlNnNnNnOoOoOoRrRrRrSsSsSsSsTtTtUuUuUu" +
        "UuUuUuWwYyYZzZzZz";

    /// <summary>
    /// The slug, guaranteed non-empty. Separate from <see cref="From"/> so a caller that
    /// wants to detect "this name produced nothing" still can.
    /// </summary>
    public static string FromOrFallback(string? name)
    {
        var slug = From(name);
        return slug.Length == 0 ? Fallback : slug;
    }

    /// <summary>
    /// <paramref name="wanted"/>, or the first free "<paramref name="wanted"/>-N" after it.
    /// Two packs called "Anego Server" are a reasonable thing to want.
    /// </summary>
    public static string MakeUnique(string wanted, Func<string, bool> taken)
    {
        if (!taken(wanted)) return wanted;

        for (var n = 2; n < 1000; n++)
        {
            var suffix = $"-{n}";
            // Truncated to leave room for the suffix, otherwise a maximum-length name
            // would produce an id one character over the limit.
            var candidate = Trim(wanted[..Math.Min(wanted.Length, MaxLength - suffix.Length)]) + suffix;
            if (!taken(candidate)) return candidate;
        }

        throw new InvalidOperationException($"No free id left for '{wanted}'.");
    }

    private static string Trim(string slug) =>
        slug.Length > MaxLength
            ? slug[..MaxLength].Trim('-')
            : slug.Trim('-');
}
