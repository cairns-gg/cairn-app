using System.IO.Compression;
using System.Text.Json;

namespace Cairn.Core.Hotkeys;

/// <summary>
/// A mod's own translations, for turning the lang key it registered into the sentence the
/// controls screen would show.
///
/// Mods rarely pass a readable name to <c>RegisterHotKey</c>. They pass a key —
/// <c>"scribe:hotkey-scribepinhud"</c> — and the game resolves it against the mod's assets
/// at runtime. Reading it here is what turns a list of ids into a list somebody can make
/// decisions from: "hotkey-scribepinhud" and "Scribe Mod: Expand/collapse pinned-task HUD"
/// are the same row, and only one of them answers "do I want this on P?".
///
/// English only, deliberately. This is an authoring tool — the author picks the bindings
/// once and the result is a set of key codes that mean the same thing in every language —
/// and shipping a language picker for a list of labels is a lot of machinery for a list of
/// labels.
/// </summary>
public sealed class HotkeyLang
{
    /// <summary>Indexed twice: by the key as written, and by the part after the domain.</summary>
    private readonly Dictionary<string, string> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Suffix index, for a mod whose registered name is only the tail of the key it stores.
    /// Kept apart from the exact lookup so it is only consulted when that fails, and only
    /// when it is unambiguous.
    /// </summary>
    /// <summary>
    /// Tail → the distinct sentences registered under it.
    ///
    /// A set rather than a list because this only ever answers "is there exactly one", and
    /// because the list version asked <c>Contains</c> on every insert. That is linear per
    /// add and quadratic over a file, which is invisible on a real lang file — a few
    /// hundred keys — and is a mod's to choose: keys come out of an archive somebody else
    /// wrote, and a couple of hundred thousand of them sharing a tail is around 2×10^10
    /// string comparisons on a tab somebody opened.
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> _tails = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The most a mod's <c>en.json</c> may be before it is passed over. A translation table
    /// runs to tens of kilobytes; this is far enough above that to be about stopping an
    /// allocation rather than judging a file.
    /// </summary>
    public const int MaxLangBytes = 8 * 1024 * 1024;

    public int Count => _entries.Count;

    /// <summary>
    /// Reads every <c>assets/&lt;domain&gt;/lang/en.json</c> in the archive. Never throws:
    /// a mod with unreadable translations is a mod whose rows show their codes.
    /// </summary>
    public static HotkeyLang From(ZipArchive archive)
    {
        var lang = new HotkeyLang();
        lang.ReadFrom(archive);
        return lang;
    }

    /// <summary>
    /// Folds another archive's translations in.
    ///
    /// One table for the whole pack, because a key's domain is not the mod that registered
    /// it: XLib registers <c>xskills:hotkey-effectframehotkey</c>, and the <c>xskills</c>
    /// translations ship in the xSkills zip beside it. Resolving per mod leaves those rows
    /// showing ids for no reason other than which file we happened to be holding.
    /// </summary>
    public void ReadFrom(ZipArchive archive)
    {
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.EndsWith("/lang/en.json", StringComparison.OrdinalIgnoreCase)) continue;
            if (!entry.FullName.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)) continue;

            // Bounded for the same reason modinfo.json is, and it was left out when that
            // one was done: this parses a stream out of somebody else's archive, and
            // JsonDocument over a DeflateStream reads to the end into a doubling buffer.
            // A lang file is a translation table — the largest a mod ships is measured in
            // tens of kilobytes — so the cap is orders of magnitude clear of anything real.
            if (entry.Length > MaxLangBytes) continue;

            try
            {
                using var raw = entry.Open();
                var bytes = BoundedRead.AtMost(raw, MaxLangBytes + 1);
                if (bytes.Length > MaxLangBytes) continue;

                using var stream = new MemoryStream(bytes, writable: false);
                using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });

                if (document.RootElement.ValueKind != JsonValueKind.Object) continue;

                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String) continue;
                    Add(property.Name, property.Value.GetString()!);
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException)
            {
                // One unreadable lang file costs its own labels and nothing else.
            }
        }
    }

    private void Add(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        _entries.TryAdd(key, value);

        // Files disagree about whether to write the domain: some store "hotkey-x", others
        // "mymod:hotkey-x". Indexing both means the caller does not have to know which.
        var colon = key.IndexOf(':');
        if (colon > 0 && colon < key.Length - 1) _entries.TryAdd(key[(colon + 1)..], value);

        foreach (var separator in stackalloc[] { ':', '-' })
        {
            var at = key.LastIndexOf(separator);
            if (at > 0 && at < key.Length - 1)
            {
                var tail = key[(at + 1)..];
                if (!_tails.TryGetValue(tail, out var seen)) _tails[tail] = seen = [];
                seen.Add(value);
            }
        }
    }

    /// <summary>
    /// The sentence for a registered name, or null to leave the row showing its code.
    ///
    /// A name with a space in it is already a sentence — several mods pass "Status Hud
    /// Menu" straight in — and looking that up would at best find nothing.
    /// </summary>
    public string? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains(' ')) return null;

        if (_entries.TryGetValue(name, out var exact)) return exact;

        var colon = name.IndexOf(':');
        if (colon > 0 && _entries.TryGetValue(name[(colon + 1)..], out var bare)) return bare;

        // Last resort, and only when there is one answer: a mod that registers
        // "hotkey-editmode" while its file says "xlibfork:xpdrops-hotkey-editmode" is
        // reachable no other way, and two candidates mean we cannot say which.
        return _tails.TryGetValue(name, out var candidates) && candidates.Count == 1
            ? candidates.First()
            : null;
    }

    /// <summary>
    /// The label for a hotkey, given whatever name was registered and the code it was
    /// registered under.
    ///
    /// The code is the fallback probe rather than the answer. <c>hotkey-&lt;code&gt;</c> is
    /// a widespread convention — scribe, xSkills and others all write it — and trying it
    /// recovers the rows where the name was assembled at runtime and never reached the IL.
    ///
    /// Returns null when the row should show its code, which includes the case of a name
    /// that is still plainly a key: showing "xskills:hotkey-cateyesonoff" is showing an id
    /// with extra punctuation.
    /// </summary>
    public string? Label(string? name, string code)
    {
        if (Resolve(name) is { } resolved) return resolved;

        if (Resolve("hotkey-" + code) is { } byConvention) return byConvention;

        // A name with a space in it was never a key — it is the label, written out by a mod
        // that did not bother with translations.
        return name is not null && name.Contains(' ') ? name : null;
    }
}
