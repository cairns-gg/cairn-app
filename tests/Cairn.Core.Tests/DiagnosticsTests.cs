using Cairn.Core;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// The report somebody pastes into a public issue tracker.
///
/// Two things are being tested and only one of them is the content. The other is what must
/// never be in it: this machine holds a cairns.gg token and a Vintage Story session, and
/// every absolute path on it is named after its owner. A report is worthless if people
/// learn not to send it.
/// </summary>
public class DiagnosticsTests
{
    private static PackManifest Pack(params string[] mods) => new()
    {
        Id = "anego",
        Name = "Anego Server",
        GameVersion = "1.22.5",
        Mods = [.. mods.Select(m => new PackMod { ModId = m })],
    };

    private static PackLock Lock(params string[] mods) => new()
    {
        GameVersion = "1.22.5",
        Mods = [.. mods.Select(m => new LockedMod { ModId = m, Version = "1.0.0" })],
    };

    [Fact]
    public void The_report_names_the_build_and_the_platform()
    {
        var report = Diagnostics.Report();

        Assert.Contains("Cairn diagnostics", report);
        Assert.Contains(CairnVersion.Current, report);
        Assert.Contains(Cairn.Core.Updates.UpdateChecker.ThisPlatform, report);
    }

    [Fact]
    public void A_home_directory_never_reaches_the_report()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // The whole reason Redact exists: /Users/<name> is a real person's name, and this
        // text is going somewhere public.
        Assert.DoesNotContain(home, Diagnostics.Report());
        Assert.Equal("~/.cairn/packs", Diagnostics.Redact(Path.Combine(home, ".cairn", "packs")));
    }

    [Fact]
    public void Redaction_reaches_inside_log_lines_too()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var line = $"could not open {Path.Combine(home, ".cairn", "games")} — permission denied";

        var report = Diagnostics.Report(log: [line]);

        Assert.DoesNotContain(home, report);
        Assert.Contains("permission denied", report);
    }

    [Fact]
    public void A_pack_is_described_by_what_is_actually_installed()
    {
        var locked = Lock("carryon", "carryonlib");
        locked.Mods[1].RequiredBy = ["carryon"];

        var report = Diagnostics.Report(Pack("carryon"), locked);

        Assert.Contains("Pack 'anego' — Anego Server", report);
        Assert.Contains("carryon", report);

        // A library nobody asked for explains itself, exactly as it does in the pane.
        Assert.Contains("required by carryon", report);
    }

    [Fact]
    public void A_mod_the_lock_does_not_have_is_called_out()
    {
        // The single most useful line in the report: it is what stops the pack launching
        // and what stops it publishing, and it is invisible in a list of what worked.
        var report = Diagnostics.Report(Pack("carryon", "unchisel"), Lock("carryon"));

        Assert.Contains("NOT INSTALLED: unchisel", report);
    }

    [Fact]
    public void A_server_address_is_reported_as_present_and_never_quoted()
    {
        var pack = Pack("carryon");
        pack.Connect = "play.anego.example:42420";

        var report = Diagnostics.Report(pack, Lock("carryon"));

        // Publishing already treats the address as the one thing somebody may not want
        // shared. A diagnostics report is more public than a published pack, not less.
        Assert.DoesNotContain("anego.example", report);
        Assert.Contains("connect    set", report);
    }

    /// <summary>
    /// The lock, the disk and the zip all describing the same mod, so the interesting case
    /// — them disagreeing — is visible without anybody being asked to go and look.
    /// </summary>
    public sealed class ModDetail : IDisposable
    {
        private readonly string _mods = Path.Combine(
            Path.GetTempPath(), "cairn-diag-" + Guid.NewGuid().ToString("n")[..8]);

        public ModDetail() => Directory.CreateDirectory(_mods);

        public void Dispose()
        {
            if (Directory.Exists(_mods)) Directory.Delete(_mods, recursive: true);
        }

        private string Plant(string name, string modInfo)
        {
            using var buffer = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(
                       buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = zip.CreateEntry("modinfo.json");
                using var writer = new StreamWriter(entry.Open());
                writer.Write(modInfo);
            }

            var bytes = buffer.ToArray();
            File.WriteAllBytes(Path.Combine(_mods, name), bytes);

            return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        }

        private static (PackManifest Pack, PackLock Lock) PackWith(LockedMod mod)
        {
            var pack = new PackManifest
            {
                Id = "anego", GameVersion = "1.22.5",
                Mods = [new PackMod { ModId = mod.ModId }],
            };

            return (pack, new PackLock { GameVersion = "1.22.5", Mods = [mod] });
        }

        [Fact]
        public void A_mod_is_described_from_its_own_zip_as_well_as_the_lock()
        {
            var sha = Plant("genelib_3.2.0.zip", """
                {"type":"code","modid":"genelib","name":"Genelib","version":"3.2.0",
                 "authors":["sekelsta"],"dependencies":{"game":"1.22.0"}}
                """);

            var (pack, locked) = PackWith(new LockedMod
            {
                ModId = "genelib", Version = "3.2.0", FileName = "genelib_3.2.0.zip",
                Url = "https://moddbcdn.vintagestory.at/genelib_3.2.0.zip", Sha256 = sha,
            });

            var report = Diagnostics.Report(pack, locked, modsDir: _mods);

            Assert.Contains("\"Genelib\" by sekelsta", report);
            Assert.Contains("sha256 matches the lock", report);
            Assert.Contains("requires   game 1.22.0", report);

            // The host, not the whole URL — the question is whether it came from somewhere
            // ModDB serves, and the rest is noise in a report somebody has to read.
            Assert.Contains("moddbcdn.vintagestory.at", report);
        }

        [Fact]
        public void A_file_the_lock_claims_and_disk_does_not_have_is_shouted_about()
        {
            var (pack, locked) = PackWith(new LockedMod
            {
                ModId = "ghost", Version = "1.0.0", FileName = "ghost_1.0.0.zip",
                Sha256 = new string('a', 64),
            });

            var report = Diagnostics.Report(pack, locked, modsDir: _mods);

            Assert.Contains("MISSING FROM DISK", report);
        }

        [Fact]
        public void A_checksum_that_has_moved_is_reported_as_a_difference()
        {
            Plant("swapped_1.0.0.zip", """{"modid":"swapped","version":"1.0.0"}""");

            var (pack, locked) = PackWith(new LockedMod
            {
                ModId = "swapped", Version = "1.0.0", FileName = "swapped_1.0.0.zip",
                Sha256 = new string('b', 64),   // what the lock recorded, not what is there
            });

            var report = Diagnostics.Report(pack, locked, modsDir: _mods);

            // A file swapped by hand behaves like a different mod than the pack believes it
            // installed, and reads as "it just stopped working" without this line.
            Assert.Contains("DIFFERS from the lock", report);
        }

        [Fact]
        public void A_zip_declaring_a_different_version_from_the_lock_says_so()
        {
            var sha = Plant("drifted_1.0.0.zip",
                """{"modid":"drifted","name":"Drifted","version":"9.9.9"}""");

            var (pack, locked) = PackWith(new LockedMod
            {
                ModId = "drifted", Version = "1.0.0", FileName = "drifted_1.0.0.zip",
                Sha256 = sha,
            });

            var report = Diagnostics.Report(pack, locked, modsDir: _mods);

            Assert.Contains("version disagrees with the lock (1.0.0)", report);
        }

        [Fact]
        public void An_unreadable_zip_is_described_rather_than_skipped()
        {
            File.WriteAllText(Path.Combine(_mods, "broken_1.0.0.zip"), "not an archive");

            var (pack, locked) = PackWith(new LockedMod
            {
                ModId = "broken", Version = "1.0.0", FileName = "broken_1.0.0.zip",
            });

            var report = Diagnostics.Report(pack, locked, modsDir: _mods);

            Assert.Contains("unreadable", report);
            Assert.Contains("zip could not be opened", report);
        }

        [Fact]
        public void Without_a_mods_directory_the_lock_is_still_reported()
        {
            // The CLI with no pack, and any caller that cannot reach the files: a report
            // with less in it beats one that throws.
            var (pack, locked) = PackWith(new LockedMod
            {
                ModId = "genelib", Version = "3.2.0", FileName = "genelib_3.2.0.zip",
            });

            var report = Diagnostics.Report(pack, locked, modsDir: null);

            Assert.Contains("genelib 3.2.0", report);
            Assert.Contains("(not inspected)", report);
        }
    }

    [Fact]
    public void Only_the_tail_of_a_long_log_is_carried()
    {
        var lines = Enumerable.Range(1, 500).Select(i => $"line {i}").ToList();

        var report = Diagnostics.Report(log: lines);

        Assert.Contains("line 500", report);
        Assert.DoesNotContain("line 1\n", report);
        Assert.Contains($"last {Diagnostics.LogLines} of 500", report);
    }

    [Fact]
    public void A_pack_that_was_never_synced_says_so_rather_than_looking_empty()
    {
        var report = Diagnostics.Report(Pack("carryon"), locked: null);

        Assert.Contains("never synced", report);
    }

    [Fact]
    public void Nothing_is_inspected_that_was_not_asked_for()
    {
        // No pack, no log, no library: still a usable report rather than an exception.
        // This is the shape the CLI produces with no arguments, and the one somebody gets
        // when the thing that is broken is the pack list itself.
        var report = Diagnostics.Report();

        Assert.Contains("(not inspected)", report);
        Assert.DoesNotContain("Pack '", report);
    }
}
