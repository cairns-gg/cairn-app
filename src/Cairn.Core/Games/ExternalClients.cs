using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cairn.Core.Games;

/// <summary>
/// A client somebody built themselves and pointed Cairn at.
/// </summary>
/// <param name="Directory">Where it is. Outside Cairn's root, and it stays there.</param>
/// <param name="Label">What to call it — "Optimum". Shown wherever an install is named.</param>
/// <param name="Executable">
/// The binary to run, bare filename. The same thing the variant marker carries and for the
/// same reason: an Optimum tree contains the stock executable too, so "run the game in this
/// directory" gets you vanilla while every message says otherwise.
/// </param>
public sealed record ExternalClient(
    [property: JsonPropertyName("directory")] string Directory,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("executable")] string Executable);

/// <summary>
/// Clients Cairn did not build, kept on Cairn's side rather than marked in their own tree.
///
/// The obvious implementation is to write a <c>.cairn-variant</c> into the directory, which
/// is what Cairn's own builds carry — and it is wrong here for one reason: rebuilding is the
/// whole point of pointing at your own client. Optimum's packager rewrites its output
/// directory, so a marker left in there disappears on the next build, and what is left is an
/// install that reads as the stock game with a pack still pointed at it. That launches
/// vanilla, silently, with nothing on screen able to say so — the exact substitution the
/// marker exists to prevent, arrived at from a direction the marker cannot see.
///
/// Keeping the record here survives their rebuild, and never writes into a directory Cairn
/// does not own. If a rebuild renames the launcher the executable is simply not there, and
/// <see cref="GameInstall.TryAt(string, VariantSpec?)"/> already refuses rather than falling
/// back to the stock binary.
///
/// Read and written per call rather than cached, like everything else keyed on
/// <see cref="CairnPaths.Root"/>: the root moves while Cairn is running.
/// </summary>
public sealed class ExternalClients(string path)
{
    /// <summary>
    /// Lives among the install directories it is about. Invisible to
    /// <see cref="GameStore.ListInstalled"/>, which enumerates directories.
    /// </summary>
    public const string FileName = "external.json";

    public static ExternalClients In(string gamesRoot) =>
        new(Path.Combine(gamesRoot, FileName));

    /// <summary>Where the record lives. Named around System.IO.Path, not after it.</summary>
    public string RecordPath => path;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>
    /// Everything recorded, whether or not it is still there.
    ///
    /// Deliberately not filtered by existence: a client on a drive that is not mounted right
    /// now has not been un-chosen, and dropping it here would quietly forget it. Existence is
    /// <see cref="GameStore.ListExternal"/>'s question.
    /// </summary>
    public IReadOnlyList<ExternalClient> All
    {
        get
        {
            try
            {
                return File.Exists(path)
                    ? JsonSerializer.Deserialize<List<ExternalClient>>(File.ReadAllText(path)) ?? []
                    : [];
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                // An unreadable file means no client was ever pointed at, which drops packs
                // back to the stock game with a note. The other direction — guessing at a
                // half-parsed record — decides which binary to execute from a damaged file.
                return [];
            }
        }
    }

    /// <summary>What was recorded about a directory, or null if nothing was.</summary>
    public ExternalClient? For(string directory) =>
        All.FirstOrDefault(c => SamePath(c.Directory, directory));

    /// <summary>
    /// Records a client, replacing any earlier record of the same directory.
    ///
    /// Replacing rather than appending, so re-pointing at a tree whose launcher was renamed
    /// corrects it instead of leaving two records where the first one wins.
    /// </summary>
    public void Remember(ExternalClient client)
    {
        var kept = All.Where(c => !SamePath(c.Directory, client.Directory)).ToList();
        kept.Add(client with { Directory = Normalise(client.Directory) });
        Save(kept);
    }

    /// <summary>Whether anything was actually forgotten.</summary>
    public bool Forget(string directory)
    {
        var kept = All.Where(c => !SamePath(c.Directory, directory)).ToList();
        if (kept.Count == All.Count) return false;

        Save(kept);
        return true;
    }

    private void Save(List<ExternalClient> clients)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(clients, Options));
    }

    private static string Normalise(string dir) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));

    /// <summary>Linux file systems are case-sensitive; macOS and Windows are not.</summary>
    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Normalise(a), Normalise(b),
                OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException)
        {
            // GetFullPath throws on a path with characters the platform will not have. Two
            // records that cannot both be resolved are not the same one.
            return false;
        }
    }
}
