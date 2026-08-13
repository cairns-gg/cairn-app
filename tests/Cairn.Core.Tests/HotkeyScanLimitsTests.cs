using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Cairn.Core.Hotkeys;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// What the hotkey scan will read out of a mod zip, and how long it will spend on a lang
/// file.
///
/// Both inputs come out of archives somebody else wrote, and both are read when a tab is
/// opened rather than when a pack is synced — so the cost lands while somebody is looking
/// at the window.
/// </summary>
public class HotkeyScanLimitsTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "cairn-hotkey-" + Guid.NewGuid().ToString("n")[..8]);

    public HotkeyScanLimitsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>
    /// A zip whose ".dll" entries are not assemblies at all — the scan has to survive that
    /// anyway, and it means the fixture costs nothing to build.
    /// </summary>
    private string Zip(string name, int entries, int bytesEach)
    {
        var path = Path.Combine(_dir, name);
        var payload = new byte[bytesEach];

        using (var file = File.Create(path))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            using (var info = new StreamWriter(zip.CreateEntry("modinfo.json").Open()))
                info.Write("""{"modid":"test","name":"Test","version":"1.0.0"}""");

            for (var i = 0; i < entries; i++)
            {
                using var e = zip.CreateEntry($"lib{i}.dll").Open();
                e.Write(payload);
            }
        }

        return path;
    }

    /// <summary>
    /// A zip holding far more assemblies than any mod ships. Each is individually
    /// reasonable, so a size bound alone says nothing about it.
    /// </summary>
    [Fact]
    public void A_zip_full_of_assemblies_does_not_take_forever()
    {
        Zip("many.zip", entries: 20_000, bytesEach: 64);

        var started = Stopwatch.StartNew();
        var result = HotkeyCatalog.Read(_dir, gameAssembly: null);
        started.Stop();

        // The bound is the assertion; the clock is here so a regression that removes it
        // fails loudly rather than slowly.
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(20),
            $"scanning took {started.Elapsed.TotalSeconds:F1}s");
        Assert.NotNull(result);
    }

    /// <summary>
    /// An entry that declares itself larger than any assembly is passed over rather than
    /// read into memory to find out.
    /// </summary>
    [Fact]
    public void An_absurdly_large_entry_is_passed_over()
    {
        var path = Path.Combine(_dir, "huge.zip");

        using (var file = File.Create(path))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            using var e = zip.CreateEntry("huge.dll").Open();

            // Compressible, so the fixture stays small on disk while declaring a size well
            // past the cap — the shape of the thing this guards against.
            var chunk = new byte[1024 * 1024];
            for (var i = 0; i < HotkeyCatalog.MaxAssemblyBytes / chunk.Length + 2; i++)
                e.Write(chunk);
        }

        Assert.True(new FileInfo(path).Length < 5_000_000, "the fixture should be small on disk");

        // Reads without exhausting memory, and says nothing about a file it did not read.
        var result = HotkeyCatalog.Read(_dir, gameAssembly: null);
        Assert.NotNull(result);
    }

    /// <summary>
    /// The lang index used to ask Contains on every insert, which is quadratic over a file
    /// whose keys a mod chooses. Two hundred thousand keys sharing a tail was around
    /// 2×10^10 string comparisons.
    /// </summary>
    [Fact]
    public void A_lang_file_whose_keys_all_share_a_tail_is_still_linear()
    {
        var json = new StringBuilder("{");
        for (var i = 0; i < 200_000; i++)
        {
            if (i > 0) json.Append(',');
            json.Append($"\"mod{i}:hotkey-shared\":\"Value {i}\"");
        }
        json.Append('}');

        var path = Path.Combine(_dir, "lang.zip");
        using (var file = File.Create(path))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            using var e = new StreamWriter(zip.CreateEntry("assets/test/lang/en.json").Open());
            e.Write(json.ToString());
        }

        using var archive = ZipFile.OpenRead(path);

        var started = Stopwatch.StartNew();
        var lang = HotkeyLang.From(archive);
        started.Stop();

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(15),
            $"parsing took {started.Elapsed.TotalSeconds:F1}s");

        // And still answers the question it exists for: many candidates means it cannot say.
        Assert.Null(lang.Resolve("shared"));
    }

    /// <summary>
    /// The lang file is read out of the same untrusted archive as the assemblies, and was
    /// left unbounded when modinfo.json was capped.
    /// </summary>
    [Fact]
    public void A_lang_file_that_inflates_is_passed_over()
    {
        var path = Path.Combine(_dir, "bomb.zip");

        using (var file = File.Create(path))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            using var e = new StreamWriter(zip.CreateEntry("assets/test/lang/en.json").Open());
            e.Write("{\"a\":\"" + new string('x', HotkeyLang.MaxLangBytes + 1024) + "\"}");
        }

        Assert.True(new FileInfo(path).Length < 200_000, "the fixture should be small on disk");

        using var archive = ZipFile.OpenRead(path);
        var lang = HotkeyLang.From(archive);

        Assert.Equal(0, lang.Count);
    }
}
