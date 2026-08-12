using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cairn.Core.Launch;

/// <summary>
/// Reading and writing the game's <c>clientsettings.json</c> without disturbing the parts
/// Cairn does not own.
///
/// Two things edit that file — the session merge and the mod-path confinement — and both
/// have to leave the fifty-odd settings beside their key exactly as the game wrote them.
/// So it is read as a tree and put back as one, rather than deserialised into a type that
/// would silently drop whatever it has no property for.
///
/// Nothing here throws. A launch must not fail because settings could not be parsed: the
/// worst outcome of giving up is the game asking you to log in, or a mod path staying as it
/// was, and neither is worth refusing to start over.
/// </summary>
internal static class ClientSettingsFile
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static JsonObject? TryLoad(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Staged and moved, so an interrupted write never leaves the game a half-parsed
    /// settings file — which it would rewrite from defaults, losing everything in it.
    /// </summary>
    public static void Write(string path, JsonObject root)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Owner-only, because PackData merges the Vintage Story login into every
            // pack's copy of this file at launch so one sign-in reaches all of them —
            // which makes each one a credential, not a preferences file.
            var staging = path + "." + Path.GetRandomFileName();
            OwnerOnly.WriteText(staging, root.ToJsonString(Json));
            File.Move(staging, path, overwrite: true);
            OwnerOnly.Tighten(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Callers each have a tolerable failure: a re-login, or a mod path left alone.
        }
    }
}
