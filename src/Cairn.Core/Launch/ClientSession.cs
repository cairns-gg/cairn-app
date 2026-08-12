using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cairn.Core.Launch;

/// <summary>
/// The handful of values in clientsettings.json that say who you are logged in as.
///
/// Packs get their own data path so their worlds and configs stay apart, but a separate
/// login per pack would be a poor trade. These keys are therefore carried between packs
/// while everything else — keybinds, graphics, dialog positions, the fifty-odd bool and
/// int settings — stays per-pack.
///
/// Merging named keys rather than copying the file is the whole point: copying would give
/// one login at the cost of one shared set of preferences.
/// </summary>
public sealed class ClientSession
{
    /// <summary>
    /// The auth-bearing keys, all inside the file's "stringSettings" object. Taken from a
    /// real clientsettings.json rather than guessed.
    /// </summary>
    public static readonly string[] Keys =
    [
        "sessionkey",
        "sessionsignature",
        "playeruid",
        "mptoken",
        "entitlements",
        "useremail",
        "playername",
    ];

    private const string Bucket = "stringSettings";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public Dictionary<string, string> Values { get; init; } = [];

    /// <summary>
    /// The key that actually proves a login. The others travel with it, but several —
    /// playername especially — survive signing out, so treating any of them as evidence
    /// would let a logged-out pack overwrite a good session.
    /// </summary>
    private const string Credential = "sessionkey";

    public bool IsEmpty =>
        !Values.TryGetValue(Credential, out var key) || string.IsNullOrEmpty(key);

    /// <summary>
    /// Reads the session out of a clientsettings.json, or an empty session if the file is
    /// missing or unreadable. Never throws: a launch must not fail because settings could
    /// not be parsed.
    /// </summary>
    public static ClientSession ReadFrom(string clientSettingsPath)
    {
        var root = ClientSettingsFile.TryLoad(clientSettingsPath);
        if (root?[Bucket] is not JsonObject strings) return new ClientSession();

        var values = new Dictionary<string, string>();
        foreach (var key in Keys)
            if (strings[key]?.GetValue<string>() is { } value)
                values[key] = value;

        return new ClientSession { Values = values };
    }

    /// <summary>
    /// Writes these keys into a clientsettings.json, leaving every other setting exactly
    /// as it was. Creates a minimal file when none exists.
    /// </summary>
    public void MergeInto(string clientSettingsPath)
    {
        if (IsEmpty) return;

        var root = ClientSettingsFile.TryLoad(clientSettingsPath) ?? new JsonObject();

        if (root[Bucket] is not JsonObject strings)
        {
            strings = new JsonObject();
            root[Bucket] = strings;
        }

        foreach (var (key, value) in Values) strings[key] = value;

        ClientSettingsFile.Write(clientSettingsPath, root);
    }

    /// <summary>
    /// Takes the session keys back out of a settings file, leaving everything else.
    ///
    /// For a settings file seeded by copying the player's own: the copy is meant to carry
    /// their keybinds and graphics, and a login is neither. Left in, it would also be
    /// elected: <see cref="CaptureLatest"/> takes the newest session on the machine by file
    /// timestamp, and a file copied a moment ago is the newest by construction — so making
    /// a pack would sign every other pack back in as whoever the shared data path last was.
    ///
    /// The real login arrives immediately afterwards through <see cref="MergeInto"/>, from
    /// Cairn's own record, which is the one place that knows which session is current.
    /// </summary>
    public static void Forget(string clientSettingsPath)
    {
        var root = ClientSettingsFile.TryLoad(clientSettingsPath);
        if (root?[Bucket] is not JsonObject strings) return;

        var removed = false;
        foreach (var key in Keys) removed |= strings.Remove(key);

        if (removed) ClientSettingsFile.Write(clientSettingsPath, root);
    }

    /// <summary>Cairn's own record of the session, kept beside the packs.</summary>
    public static ClientSession Load(string sessionPath)
    {
        try
        {
            if (!File.Exists(sessionPath)) return new ClientSession();

            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(sessionPath), Json);

            return new ClientSession { Values = values ?? [] };
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new ClientSession();
        }
    }

    public void Save(string sessionPath)
    {
        if (IsEmpty) return;

        try
        {
            // These are the keys that are the Vintage Story login — a session key, an
            // mptoken, an entitlements blob. They were written with no mode at all, so
            // they landed at 0644 under an ordinary umask and stayed there.
            OwnerOnly.CreateDirectory(Path.GetDirectoryName(sessionPath)!);

            // Staged and moved, like the caches: a half-written session file would be
            // read back as a partial login. The staging file is created owner-only and
            // File.Move carries the mode with it, so there is no moment at which the
            // login is on disk readable by anybody else.
            var staging = sessionPath + "." + Path.GetRandomFileName();
            OwnerOnly.WriteText(staging, JsonSerializer.Serialize(Values, Json));
            File.Move(staging, sessionPath, overwrite: true);
            OwnerOnly.Tighten(sessionPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing the record costs one re-login, not a launch.
        }
    }

}
