using System.IO.Compression;
using System.Text;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// A mod zip is somebody else's archive, and its modinfo.json is read on every sync — so on
/// every Play, and unattended under systemd on a server. An entry that inflates without
/// bound turns that into a pack that cannot be launched again without deleting the zip by
/// hand, because the run dies before the lock is written.
///
/// Both halves of the bound are exercised here: what the archive declares, and what it
/// actually produces. Only the second survives a zip built to lie.
/// </summary>
public class ModInfoSizeTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "cairn-modinfo-" + Guid.NewGuid().ToString("n")[..8]);

    public ModInfoSizeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string WriteZip(string name, byte[] modInfo)
    {
        var path = Path.Combine(_dir, name);

        using (var file = File.Create(path))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            using var entry = zip.CreateEntry("modinfo.json").Open();
            entry.Write(modInfo);
        }

        return path;
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private const string Ordinary =
        """
        {"type":"content","modid":"olla","name":"Olla","version":"1.0.0",
         "authors":["someone"],"dependencies":{"game":"1.22.5","genelib":"3.2.0"}}
        """;

    [Fact]
    public void An_ordinary_modinfo_still_reads()
    {
        var info = ModDependencies.Describe(WriteZip("ok.zip", Utf8(Ordinary)));

        Assert.Null(info.Problem);
        Assert.Equal("olla", info.ModId);
        Assert.Equal("1.0.0", info.Version);
        Assert.Contains(info.Requires, r => r.Key == "genelib");

        // And the sync-facing view drops the game's own domains, as it always did.
        Assert.Equal(["genelib"], ModDependencies.Read(WriteZip("ok2.zip", Utf8(Ordinary))).Dependencies);
    }

    /// <summary>
    /// Guards the change rather than the vulnerability. Parsing moved off the decompressor
    /// and onto the bytes already read, and the ReadOnlyMemory overload of JsonDocument
    /// rejects a UTF-8 BOM where the Stream one skips it — so a mod authored on Windows
    /// would have started reporting an unreadable modinfo.json.
    /// </summary>
    [Fact]
    public void A_modinfo_with_a_byte_order_mark_still_reads()
    {
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Utf8(Ordinary)).ToArray();
        var info = ModDependencies.Describe(WriteZip("bom.zip", withBom));

        Assert.Null(info.Problem);
        Assert.Equal("olla", info.ModId);
    }

    [Fact]
    public void A_modinfo_at_the_limit_is_still_read()
    {
        // Padded with a long description to exactly the cap. Real ones run 159–649 bytes,
        // so this is already absurd; it is here to prove the boundary is not off by one.
        var pad = ModDependencies.MaxModInfoBytes - Utf8(Ordinary).Length - 20;
        var big = $$"""{"modid":"olla","version":"1.0.0","description":"{{new string('x', pad)}}"}""";
        var bytes = Utf8(big);

        Assert.True(bytes.Length <= ModDependencies.MaxModInfoBytes);

        var info = ModDependencies.Describe(WriteZip("limit.zip", bytes));

        Assert.Null(info.Problem);
        Assert.Equal("olla", info.ModId);
    }

    /// <summary>
    /// The cheap half: the archive says how big the entry is, and that is enough to refuse
    /// before anything is decompressed.
    /// </summary>
    [Fact]
    public void A_modinfo_past_the_cap_is_refused_without_reading_it()
    {
        // 8 MB of highly compressible JSON — a few KB on disk, and the shape of the real
        // attack, which is a small file that inflates.
        var bomb = Utf8($$"""{"modid":"olla","description":"{{new string('a', 8 * 1024 * 1024)}}"}""");
        var path = WriteZip("bomb.zip", bomb);

        Assert.True(new FileInfo(path).Length < 100_000, "the fixture should be small on disk");

        var info = ModDependencies.Describe(path);

        Assert.NotNull(info.Problem);
        Assert.Contains("far larger than any real one", info.Problem);
        Assert.Contains("8 MB", info.Problem);

        // Never throws, and says the emptiness is ignorance rather than "no dependencies".
        var read = ModDependencies.Read(path);
        Assert.Empty(read.Dependencies);
        Assert.NotNull(read.Problem);
    }

    /// <summary>
    /// A zip that understates its own entry gets nowhere, and it is worth recording exactly
    /// why, because the reason is a framework guarantee rather than anything Cairn does.
    ///
    /// .NET bounds a read-mode entry stream at the declared uncompressed length, so an
    /// understated header cannot yield more bytes than it claims — it truncates instead,
    /// and the truncated JSON is reported as unreadable. That makes the declared length a
    /// trustworthy upper bound and the cheap check the load-bearing one. The byte counter
    /// in ModDependencies stays regardless: it costs nothing, and it is what would hold if
    /// this behaviour ever changed.
    ///
    /// Asserted rather than assumed, in the same spirit as the archive-extraction
    /// assumptions this codebase already leans on.
    /// </summary>
    [Fact]
    public void A_zip_that_understates_its_entry_truncates_rather_than_inflating()
    {
        var bomb = Utf8($$"""{"modid":"olla","description":"{{new string('a', 8 * 1024 * 1024)}}"}""");
        var path = WriteZip("liar.zip", bomb);

        Understate(path, bomb.Length, claimed: 120);

        using (var zip = ZipFile.OpenRead(path))
        {
            var entry = zip.Entries.Single();

            // The lie took: the entry reports a harmless size...
            Assert.Equal(120, entry.Length);

            // ...and the runtime holds it to that, which is the guarantee being recorded.
            using var stream = entry.Open();
            using var read = new MemoryStream();
            stream.CopyTo(read);
            Assert.Equal(120, read.Length);
        }

        // So the outcome is "unreadable", not a gigabyte of memory and not a hang.
        var info = ModDependencies.Describe(path);

        Assert.NotNull(info.Problem);
        Assert.Contains("could not be read", info.Problem);
        Assert.Empty(ModDependencies.Read(path).Dependencies);
    }

    /// <summary>
    /// Rewrites the uncompressed-size field wherever it appears — the local file header and
    /// the central directory record — leaving the compressed data untouched. ZipArchive
    /// reads the length from the central directory and the deflate stream is bounded by the
    /// compressed size, so the entry reports <paramref name="claimed"/> bytes and then
    /// produces millions.
    /// </summary>
    private static void Understate(string path, int actual, uint claimed)
    {
        var bytes = File.ReadAllBytes(path);
        var want = BitConverter.GetBytes((uint)actual);
        var with = BitConverter.GetBytes(claimed);
        var patched = 0;

        // Local file header: signature PK\3\4, uncompressed size at +22.
        // Central directory:  signature PK\1\2, uncompressed size at +24.
        foreach (var (sig, offset) in new (byte[] Sig, int Offset)[]
                 {
                     ([0x50, 0x4B, 0x03, 0x04], 22),
                     ([0x50, 0x4B, 0x01, 0x02], 24),
                 })
        {
            for (var i = 0; i + offset + 4 <= bytes.Length; i++)
            {
                if (bytes[i] != sig[0] || bytes[i + 1] != sig[1]
                    || bytes[i + 2] != sig[2] || bytes[i + 3] != sig[3]) continue;

                if (!bytes.AsSpan(i + offset, 4).SequenceEqual(want)) continue;

                with.CopyTo(bytes, i + offset);
                patched++;
            }
        }

        Assert.Equal(2, patched);
        File.WriteAllBytes(path, bytes);
    }
}
