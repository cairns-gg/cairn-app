using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cairn.Core;

/// <summary>
/// Cairn's own preferences — the handful of answers that belong to the application rather
/// than to any pack. <c>settings.json</c> in the root.
///
/// This exists because the first two settings were written by whichever feature owned them,
/// and the interface scale owned the file: <c>UiScale.Save</c> serialised a type with one
/// property on it and moved that over the top of whatever was there. Adding a second setting
/// that way is not "one more property", it is a setting that vanishes the first time
/// somebody drags the scale slider. A file with more than one thing in it needs one type
/// that knows all of them, and a write that starts by reading.
///
/// <see cref="Update"/> is therefore the only way to change one. Load-mutate-save, so a
/// caller cannot express the bug.
/// </summary>
public sealed class CairnSettings
{
    /// <summary>
    /// How large the interface is drawn, 1.0 to 2.0. Named exactly as it was written before
    /// this type existed — the property name is the key in a file people already have, and
    /// renaming it would silently reset everybody's scale to the default.
    /// </summary>
    public double UiScale { get; set; } = 1.0;

    /// <summary>
    /// The language chosen in Preferences, or null to work it out — which is what
    /// <see cref="LanguageChoice"/> does, and which for most people is the right answer
    /// because it follows the language they already play Vintage Story in.
    ///
    /// Null rather than "en" for the automatic case: storing the resolved answer would freeze
    /// somebody's launcher in whatever language they happened to start it in once, and
    /// silently stop following the game.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// Anything in the file this build does not know about, kept so it survives a write.
    ///
    /// The same failure this type was created to fix, one version along: somebody runs a
    /// newer Cairn, it writes a setting, they go back to an older one, and the older one
    /// helpfully erases it. Round-tripping the rest costs a dictionary.
    /// </summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }

    /// <summary>
    /// Nulls are left out, so "work it out" is the absence of a key rather than a key
    /// holding null. The file is meant to be readable, and a settings file listing every
    /// setting nobody has chosen reads as a list of things that are switched off.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Never throws: an unreadable settings file costs the defaults, not a start-up.</summary>
    public static CairnSettings Load()
    {
        try
        {
            if (!File.Exists(CairnPaths.SettingsPath)) return new CairnSettings();

            return JsonSerializer.Deserialize<CairnSettings>(
                File.ReadAllText(CairnPaths.SettingsPath), Json) ?? new CairnSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new CairnSettings();
        }
    }

    /// <summary>
    /// Changes one setting without touching the others: reads what is on disk, applies the
    /// change, writes it back. The only way to save, because the alternative is the bug in
    /// this type's summary.
    /// </summary>
    public static void Update(Action<CairnSettings> change)
    {
        var settings = Load();
        change(settings);
        settings.Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(CairnPaths.Root);

            // Staged and moved, like the caches: a half-written file reads as corrupt, and
            // a corrupt settings file is every preference at once.
            var staging = CairnPaths.SettingsPath + "." + Path.GetRandomFileName();
            File.WriteAllText(staging, JsonSerializer.Serialize(this, Json));
            File.Move(staging, CairnPaths.SettingsPath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing a preference costs one re-selection.
        }
    }
}
