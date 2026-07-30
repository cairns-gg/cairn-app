using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cairn.Core.Packs;

/// <summary>
/// A pack in one file, for handing to someone else.
///
/// Carries the manifest (what the pack asks for) and optionally the lockfile (exactly
/// what the author had installed). Including the lock is what makes a shared pack
/// reproducible rather than merely similar — without it a recipient resolves the newest
/// compatible release, which may not be the one the author tested.
/// </summary>
public sealed class PackBundle
{
    /// <summary>Bumped if the shape ever changes incompatibly.</summary>
    public const int CurrentFormat = 1;

    [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; } = CurrentFormat;
    [JsonPropertyName("pack")] public PackManifest? Pack { get; set; }
    [JsonPropertyName("lock")] public PackLock? Lock { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(PackManifest manifest, PackLock? locked = null) =>
        JsonSerializer.Serialize(
            new PackBundle { Pack = manifest, Lock = locked }, Options);

    /// <exception cref="InvalidDataException">The text is not a usable pack bundle.</exception>
    public static PackBundle Parse(string json)
    {
        PackBundle? bundle;
        try
        {
            bundle = JsonSerializer.Deserialize<PackBundle>(json, Options);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"Not valid JSON: {e.Message}", e);
        }

        if (bundle?.Pack is null)
            throw new InvalidDataException("This does not look like a shared pack — no 'pack' section.");

        if (bundle.FormatVersion > CurrentFormat)
            throw new InvalidDataException(
                $"This pack was exported by a newer Cairn (format {bundle.FormatVersion}).");

        var problems = bundle.Pack.Validate().ToList();
        if (problems.Count > 0)
            throw new InvalidDataException("The shared pack is not valid:\n  " + string.Join("\n  ", problems));

        return bundle;
    }

    /// <summary>
    /// Rewrites the manifest so every mod is pinned to the version in the lock. This is
    /// how an imported pack reproduces the author's set: sync resolves manifest pins, so
    /// carrying the lock alone would not constrain anything.
    /// </summary>
    public void PinToLock()
    {
        if (Pack is null || Lock is null) return;

        foreach (var mod in Pack.Mods)
        {
            var locked = Lock.Mods.FirstOrDefault(
                m => string.Equals(m.ModId, mod.ModId, StringComparison.OrdinalIgnoreCase));

            if (locked is not null && GameVersions.IsPlausibleVersion(locked.Version))
                mod.Version = locked.Version;
        }
    }
}
