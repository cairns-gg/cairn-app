using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cairn.Core.Cairns;

/// <summary>
/// The token this machine holds for cairns.gg, and who it belongs to.
///
/// Kept apart from settings.json because it is a credential rather than a preference: it
/// is written with owner-only permissions and is the one file here worth being careful
/// with.
/// </summary>
public sealed class CairnsSession
{
    [JsonPropertyName("server")] public string Server { get; set; } = CairnsClient.DefaultServer;
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("username")] public string Username { get; set; } = "";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string Path => CairnPaths.AuthPath;

    public static CairnsSession? Load()
    {
        if (!File.Exists(Path)) return null;

        try
        {
            var session = JsonSerializer.Deserialize<CairnsSession>(File.ReadAllText(Path), Json);
            return string.IsNullOrWhiteSpace(session?.Token) ? null : session;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            // An unreadable auth file means signed out, not broken. Signing in again
            // rewrites it, which is a better answer than refusing to start.
            return null;
        }
    }

    public void Save()
    {
        // Owner-only from the moment it exists. This used to write the file and narrow it
        // afterwards, which left the token on disk at 0644 for as long as that took — and
        // a descriptor opened in the window keeps its access across the change. See
        // OwnerOnly, and the directory is narrowed too so containment covers whatever
        // ends up alongside it.
        OwnerOnly.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        OwnerOnly.WriteText(Path, JsonSerializer.Serialize(this, Json));
    }

    public static void Clear()
    {
        if (File.Exists(Path)) File.Delete(Path);
    }
}
