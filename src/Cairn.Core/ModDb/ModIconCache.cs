using System.Security.Cryptography;
using System.Text;

namespace Cairn.Core.ModDb;

/// <summary>
/// Keeps mod icons on disk so browsing ModDB does not re-download the same images on
/// every search. A search returns dozens of results and the same mods come back for
/// related queries, so without this a session fetches the same icon repeatedly.
///
/// Nothing here ever throws: an icon is decoration, and a CDN hiccup must not be able to
/// break searching. Every failure is reported as "no icon".
/// </summary>
public sealed class ModIconCache(HttpClient http, string? root = null)
{
    /// <summary>Icons are a few KB; anything this large is not one, so it is not stored.</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    public string Root { get; } = root ?? CairnPaths.IconCacheRoot;

    /// <summary>
    /// Where <paramref name="url"/> is cached. Derived from a hash of the URL, so it is
    /// stable across runs and cannot be steered out of the cache directory by a hostile
    /// filename — these URLs come from a remote API.
    /// </summary>
    public string PathFor(string url)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        return Path.Combine(Root, hash[..32] + Extension(url));
    }

    public bool IsCached(string url) => File.Exists(PathFor(url));

    /// <summary>
    /// The local path for <paramref name="url"/>, downloading it if this is the first
    /// time. Null when there is nothing usable — no URL, a failed fetch, or a response
    /// too large to be an icon.
    /// </summary>
    public async Task<string?> GetAsync(string? url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var path = PathFor(url);
        if (File.Exists(path)) return path;

        try
        {
            using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            if (response.Content.Headers.ContentLength > MaxBytes) return null;

            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > MaxBytes) return null;

            Directory.CreateDirectory(Root);

            // Written via a temporary file and moved into place: two searches can want the
            // same icon at once, and a half-written file would be cached as if complete.
            var staging = Path.Combine(Root, Path.GetRandomFileName());
            await File.WriteAllBytesAsync(staging, bytes, ct).ConfigureAwait(false);
            File.Move(staging, path, overwrite: true);

            return path;
        }
        catch (Exception e) when (e is HttpRequestException or IOException
                                      or TaskCanceledException or OperationCanceledException
                                      or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Total bytes held, for reporting. 0 when nothing has been cached.</summary>
    public long Size()
    {
        try
        {
            if (!Directory.Exists(Root)) return 0;
            return new DirectoryInfo(Root).EnumerateFiles().Sum(f => f.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public void Clear()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A cache that will not clear is not worth failing over.
        }
    }

    /// <summary>
    /// The image extension the URL implies, defaulting to .img. Only used to keep the
    /// cache directory browsable — nothing reads it back.
    /// </summary>
    private static string Extension(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        var extension = Path.GetExtension(path).ToLowerInvariant();

        return extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp"
            ? extension
            : ".img";
    }
}
