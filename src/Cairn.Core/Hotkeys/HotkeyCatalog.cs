using System.IO.Compression;

namespace Cairn.Core.Hotkeys;

/// <summary>One hotkey a pack contains, and where it came from.</summary>
/// <param name="Source">The mod's file name, or "Vintage Story" for the game's own.</param>
public sealed record HotkeyEntry(
    string Code,
    string? Name,
    KeyBinding? Default,
    HotkeyKind Kind,
    string Source)
{
    /// <summary>
    /// The controls a player's hands know without looking: movement and the mouse buttons.
    ///
    /// A pack filling in a binding for a mod somebody has never run is a service; the same
    /// pack quietly moving their jump key is not. So these are marked rather than blocked —
    /// an author who means to move one can, and has to say so first. The distinction is a
    /// field the game already records, which beats a list of key names guessed at here.
    /// </summary>
    public bool IsPlayerControl => Kind is HotkeyKind.MovementControls or HotkeyKind.MouseControls
                                        or HotkeyKind.MouseModifiers;

    /// <summary>What to call that on the row, or empty for an ordinary hotkey.</summary>
    public string ControlLabel => Kind switch
    {
        HotkeyKind.MovementControls => "movement control",
        HotkeyKind.MouseControls => "mouse button",
        HotkeyKind.MouseModifiers => "click modifier",
        _ => "",
    };

    /// <summary>The game's own, rather than one a mod brought.</summary>
    public bool IsGame => Source == HotkeyCatalog.GameSource;

    /// <summary>
    /// The label, or the hotkey's own id where the mod gave no readable name and its
    /// translations did not supply one. See <see cref="HotkeyLang.Label"/>.
    /// </summary>
    public string Display => string.IsNullOrWhiteSpace(Name) ? Code : Name;
}

/// <summary>
/// Every hotkey a pack's mods register, plus the game's own, read out of the files on disk.
///
/// The point is to answer "what will collide?" before anything is launched. Twenty mods
/// bring twenty sets of defaults, several land on the same key, and today the author finds
/// that out in game, one keypress at a time, and every person who installs the pack finds
/// it out again.
///
/// Nothing here executes a mod. See <see cref="HotkeyScan"/> for why that is a rule rather
/// than a convenience.
/// </summary>
public static class HotkeyCatalog
{
    /// <summary>What the game's own bindings are filed under, so a row can say where it came from.</summary>
    public const string GameSource = "Vintage Story";

    /// <summary>
    /// Where the game keeps its own hotkey registrations, inside an install directory.
    ///
    /// Worth reading, because most collisions are between a mod and vanilla rather than
    /// between two mods: a pack knowing that three of its mods want P is useful, and knowing
    /// that one of them wants E — the inventory key — is what stops a bad launch.
    /// </summary>
    public static string? GameAssemblyIn(string? installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory)) return null;

        var path = Path.Combine(installDirectory, "VintagestoryLib.dll");
        return File.Exists(path) ? path : null;
    }

    /// <summary>What the scan could and could not read, so a caller can say so.</summary>
    /// <param name="Unreadable">
    /// Registrations whose code was built at runtime, so there is no hotkey to show at all.
    /// Distinct from <see cref="Keyless"/>: these are missing from <paramref name="Entries"/>,
    /// and reporting the two as one number counts a hotkey that is on screen as one that
    /// is not. See <see cref="HotkeyScan"/>.
    /// </param>
    public sealed record Result(IReadOnlyList<HotkeyEntry> Entries, int Unreadable, int ModsScanned)
    {
        /// <summary>
        /// Hotkeys that are here and bindable, but whose shipped key was computed rather
        /// than written, so the pack cannot say what it would be without the pack's own
        /// answer. They show a dash where a default would go.
        /// </summary>
        public int Keyless => Entries.Count(e => e.Default is null);


        /// <summary>
        /// What this pack fires twice on, as its mods ship it — before anybody has opened an
        /// editor and before it has ever been launched.
        ///
        /// The pack's own bindings are not in scope here, because a catalogue read off the
        /// files does not know them; a caller holding a manifest passes its own
        /// <see cref="BoundHotkey"/> values to <see cref="HotkeyClashes.Find"/> instead, and
        /// gets the same rule applied to the answer that is actually in force. One rule,
        /// two questions.
        /// </summary>
        public IReadOnlyList<HotkeyClash> Clashes() =>
            HotkeyClashes.Find(Entries.Select(e => new BoundHotkey(e.Code, e.Default, e.IsGame)));
    }

    /// <summary>
    /// Reads every zip in a pack's Mods directory, and the game's own assembly when one is
    /// given. Both are optional: a pack with no code mods has no hotkeys, and a pack whose
    /// game version is not installed yet can still be edited for the mods it has.
    /// </summary>
    public static Result Read(string modsDir, string? gameAssembly = null)
    {
        var entries = new List<HotkeyEntry>();
        var unreadable = 0;
        var scanned = 0;

        if (gameAssembly is not null && File.Exists(gameAssembly))
        {
            try
            {
                foreach (var r in HotkeyScan.Read(File.ReadAllBytes(gameAssembly), out var missed))
                    entries.Add(new HotkeyEntry(r.Code, r.Name, r.Default, r.Kind, GameSource));

                scanned++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The game's own bindings are a nicety here; the pack's are the point.
            }
        }

        // One translation table for the whole pack, filled before anything is labelled: a
        // lang key's domain names a mod, not the zip it was registered from, and several
        // mods register keys belonging to a library shipped beside them.
        var lang = new HotkeyLang();
        var zips = Zips(modsDir).ToList();

        foreach (var zip in zips)
        {
            try
            {
                using var archive = ZipFile.OpenRead(zip);
                lang.ReadFrom(archive);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // Unreadable zip: its own rows will say so by showing codes.
            }
        }

        foreach (var zip in zips)
        {
            scanned++;

            foreach (var (code, name, binding, kind, missed) in FromZip(zip))
            {
                if (code is null) { unreadable += missed; continue; }

                entries.Add(new HotkeyEntry(
                    code, lang.Label(name, code), binding, kind, Path.GetFileName(zip)));
            }
        }

        // Deduplicated on code: the same mod present twice — two forks, or a library both
        // ship — is one hotkey as far as the game is concerned, and two rows in an editor
        // is two chances to disagree with yourself.
        var deduped = entries
            .GroupBy(e => e.Code, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.FirstOrDefault(e => e.Default is not null) ?? g.First())
            .OrderBy(e => e.IsGame ? 0 : 1)
            .ThenBy(e => e.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new Result(deduped, unreadable, scanned);
    }

    /// <summary>The mod's own id, out of the modinfo.json every mod zip carries.</summary>
    private static string? ModIdIn(ZipArchive archive)
    {
        var entry = archive.Entries.FirstOrDefault(
            e => e.FullName.Equals("modinfo.json", StringComparison.OrdinalIgnoreCase));

        if (entry is null) return null;

        try
        {
            using var stream = entry.Open();
            using var document = System.Text.Json.JsonDocument.Parse(stream, new()
            {
                AllowTrailingCommas = true,
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
            });

            // Mods write it in whatever casing they like; the game reads it either way.
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.NameEquals("modid") && property.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                    return property.Value.GetString();
        }
        catch (Exception e) when (e is IOException or InvalidDataException
                                      or System.Text.Json.JsonException)
        {
            // A mod with no readable modinfo still has its literal registrations.
        }

        return null;
    }

    /// <summary>
    /// A cheap description of what is in the Mods directory, for a caller deciding whether
    /// a scan it already did is still the answer.
    ///
    /// Reading seventy archives is a second of disk, which is why the result is kept; a
    /// directory listing is not, which is why this can be asked every time the list is
    /// looked at. Nothing else can be asked instead: a pack gains mods from sync, from an
    /// update, from an import and from somebody dropping a zip in the folder, and a cache
    /// invalidated at each of those places is one that will be missed at the next one.
    ///
    /// Names, sizes and write times rather than hashes, because the question is "has this
    /// changed since I looked" and not "is this the same as that". Two different scans
    /// never meet.
    /// </summary>
    public static string Stamp(string modsDir)
    {
        var parts = new List<string>();

        foreach (var zip in Zips(modsDir))
        {
            try
            {
                var file = new FileInfo(zip);
                parts.Add($"{file.Name}:{file.Length}:{file.LastWriteTimeUtc.Ticks}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A file that will not answer is still a file that is there, and one that
                // appears or disappears still has to read as a change.
                parts.Add(Path.GetFileName(zip));
            }
        }

        return string.Join("|", parts);
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
            files = [];
        }

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private static List<(string? Code, string? Name, KeyBinding? Default, HotkeyKind Kind, int Missed)>
        FromZip(string path)
    {
        var results = new List<(string?, string?, KeyBinding?, HotkeyKind, int)>();

        try
        {
            using var archive = ZipFile.OpenRead(path);

            // What the mod calls itself, for the codes it builds out of its own id.
            var modId = ModIdIn(archive);

            foreach (var entry in archive.Entries)
            {
                if (!entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;

                // Read into memory: PEReader wants random access, and a mod assembly is
                // measured in hundreds of kilobytes.
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);

                var registrations = HotkeyScan.Read(buffer.ToArray(), out var missed, modId);
                if (missed > 0) results.Add((null, null, null, HotkeyKind.Unknown, missed));
                foreach (var r in registrations)
                    results.Add((r.Code, r.Name, r.Default, r.Kind, 0));
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // A truncated download or a zip we cannot open. Sync has its own opinion about
            // that file; the hotkey list is not the place to raise it.
        }

        return results;
    }
}
