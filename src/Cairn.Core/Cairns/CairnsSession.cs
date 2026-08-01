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
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(this, Json));

        // Best effort: no-op on Windows, and a token in a file only this user can read is
        // the point rather than a guarantee anybody should lean on.
        try
        {
            File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (PlatformNotSupportedException)
        {
        }
    }

    public static void Clear()
    {
        if (File.Exists(Path)) File.Delete(Path);
    }
}
