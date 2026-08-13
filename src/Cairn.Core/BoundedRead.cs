namespace Cairn.Core;

/// <summary>
/// Reading at most so much of a stream.
///
/// Three places need this and each had grown its own: a mod's <c>modinfo.json</c>, a mod
/// icon fetched from ModDB, and a mod assembly read for the hotkeys it registers. All three
/// read something out of an archive or a response that somebody else produced, and the
/// declared size — a zip header, a Content-Length — is that producer's claim about their
/// own bytes rather than a fact about them.
///
/// One byte past the limit is read on purpose, so a caller can tell "exactly at the limit"
/// from "more than we will take" without reading the rest to find out.
///
/// The buffer grows as bytes arrive rather than being reserved at the limit. The
/// overwhelming majority of what goes through here is small — a real modinfo.json is a few
/// hundred bytes — and reserving a megabyte per mod to read three hundred would trade one
/// memory problem for a smaller one.
/// </summary>
public static class BoundedRead
{
    public static byte[] AtMost(Stream stream, int limit)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];

        while (buffer.Length < limit)
        {
            var want = (int)Math.Min(chunk.Length, limit - buffer.Length);
            var read = stream.Read(chunk, 0, want);
            if (read == 0) break;

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    public static async Task<byte[]> AtMostAsync(
        Stream stream, int limit, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];

        while (buffer.Length < limit)
        {
            var want = (int)Math.Min(chunk.Length, limit - buffer.Length);
            var read = await stream.ReadAsync(chunk.AsMemory(0, want), ct).ConfigureAwait(false);
            if (read == 0) break;

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
