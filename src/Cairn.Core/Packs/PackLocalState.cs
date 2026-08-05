using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cairn.Core.Packs;

/// <summary>
/// What this machine has decided about this pack, as opposed to what the pack is.
///
/// The manifest is intent and travels; the lock is what was installed and travels; this is
/// neither. It records answers a person gave about their own copy, which are meaningless
/// to anybody else and must never be published — so it lives in its own file and
/// <see cref="PackBundle"/> has no idea it exists.
///
/// Deliberately a shape that can grow. It arrived to hold one thing, and the reason it is
/// a document rather than a list in <c>cairns.json</c> is that the next thing to remember
/// about a local copy — a dismissed warning, a per-pack preference — belongs here too and
/// should not have to move anything to get in.
/// </summary>
public sealed class PackLocalState
{
    /// <summary>
    /// Mods the author ships that this copy has chosen to do without, and does not want
    /// asked about again.
    ///
    /// Only ever set by somebody ticking a box. An inferred version of this would be worse
    /// than no version: the failure mode of guessing is a pack that silently stops
    /// mentioning a mod, which is exactly what a person who never asked for silence would
    /// want to know about.
    /// </summary>
    [JsonPropertyName("declinedMods")] public List<string> DeclinedMods { get; set; } = [];

    /// <summary>
    /// When this pack's author was last asked whether they had published. Unix seconds, so
    /// the file stays readable by eye; 0 for never.
    ///
    /// Here rather than in memory because the thing worth preventing survives a restart:
    /// clicking between two followed packs asked their servers again on every click, and a
    /// launcher reopened five times in an afternoon asked five more times.
    /// </summary>
    [JsonPropertyName("lastUpdateCheck")] public long LastUpdateCheck { get; set; }

    /// <summary>
    /// Whether this copy of somebody else's pack may be edited here.
    ///
    /// A followed pack is somebody's curation, and adding to it, dropping from it or moving
    /// its versions is a decision to stop running what they run. That is allowed and always
    /// was — it is not a rule, and nothing in Core or the CLI consults this. It is a
    /// statement of intent, so that diverging is something a person chose rather than
    /// something they did by reaching for the nearest button.
    ///
    /// Sticky, because unlocking to add one mod and being re-locked on the next launch
    /// would be a nuisance rather than a safeguard. The way back is a reset, which relocks
    /// as a consequence of there being nothing left to guard.
    /// </summary>
    [JsonPropertyName("unlocked")] public bool Unlocked { get; set; }

    public DateTimeOffset? LastChecked => LastUpdateCheck > 0
        ? DateTimeOffset.FromUnixTimeSeconds(LastUpdateCheck)
        : null;

    public void RecordCheck(DateTimeOffset when) => LastUpdateCheck = when.ToUnixTimeSeconds();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Whether this mod has been declined. Case-insensitively, as everywhere else that
    /// compares a modid.
    /// </summary>
    public bool HasDeclined(string modId) =>
        DeclinedMods.Contains(modId, StringComparer.OrdinalIgnoreCase);

    public void Decline(string modId)
    {
        if (!HasDeclined(modId)) DeclinedMods.Add(modId);
    }

    /// <summary>
    /// Forgets a decline for a mod that is in the pack again.
    ///
    /// Adding it back by hand is a clearer statement than any box was, and leaving the
    /// record would mean removing it a second time went unmentioned for ever.
    /// </summary>
    public void Restore(string modId) =>
        DeclinedMods.RemoveAll(m => string.Equals(m, modId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Empty state when there is no file, which is the ordinary case.</summary>
    public static PackLocalState Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<PackLocalState>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Unreadable means nothing was declined, which shows warnings that may have
            // been answered before. That direction is the safe one: the other silently
            // suppresses something nobody asked to suppress.
            return new();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }
}
