using Cairn.Core;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// The guard on a mod's filename, which used to be a private method of PackSyncer.
///
/// Two other places needed it and did not have it. InstallImport wrote whatever ModDB's
/// API called a release straight into the lockfile, and Diagnostics combined a name out of
/// that lock with a directory and then reported whether the result existed, how large it
/// was and what a zip there contained — an existence-and-size oracle for any path on the
/// machine, printed into the text people are asked to paste into a bug report.
/// </summary>
public class ModFileNameTests
{
    [Theory]
    [InlineData("../../../../evil.zip")]
    [InlineData("..\\..\\evil.zip")]
    [InlineData("/tmp/evil.zip")]
    [InlineData("sub/dir/evil.zip")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_name_that_is_not_only_a_name_is_refused(string name)
    {
        Assert.False(ModFileName.IsBare(name));
        Assert.Null(ModFileName.Safe(name));
        Assert.Contains("plain file name", ModFileName.Problem(name));
    }

    [Fact]
    public void An_alternate_data_stream_is_refused()
    {
        // Windows writes "mod.zip:hidden" as a stream hanging off mod.zip: File.Create
        // makes it, and neither a directory listing nor the sweep ever sees it. The colon
        // survives Path.GetFileName unchanged, so nothing else here would catch it.
        Assert.False(ModFileName.IsBare("olla_1.0.0.zip:hidden"));
        Assert.Null(ModFileName.Safe("olla_1.0.0.zip:hidden"));
    }

    [Theory]
    [InlineData("readme.txt")]
    [InlineData("olla_1.0.0")]
    [InlineData(".zip")]
    [InlineData("mod.zip.bak")]
    public void A_kind_of_file_Cairn_cannot_later_remove_is_refused(string name)
    {
        // The set that may be written and the set the sweep clears are the same set on
        // purpose. Anything outside it would sit in the mod path for ever, named by no
        // lock and removed by nothing.
        Assert.True(ModFileName.IsBare(name));
        Assert.False(ModFileName.HasModExtension(name));
        Assert.Null(ModFileName.Safe(name));
        Assert.Contains("Cairn installs", ModFileName.Problem(name));
    }

    [Theory]
    [InlineData("olla_1.0.0.zip")]
    [InlineData("SomeMod.ZIP")]
    [InlineData("legacy.dll")]
    [InlineData("snippet.cs")]
    public void The_kinds_ModDB_actually_serves_are_accepted(string name)
    {
        // ModDB takes zip, dll and cs for a release — see docs/moddb-listing.md — so all
        // three can come back from its API and refusing them would be refusing real mods.
        Assert.Equal(name, ModFileName.Safe(name));
        Assert.Null(ModFileName.Problem(name));
    }

    [Fact]
    public void Diagnostics_refuses_to_follow_a_filename_out_of_the_mods_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "cairn-diag-" + Guid.NewGuid().ToString("n")[..8]);
        var modsDir = Path.Combine(root, "Mods");
        Directory.CreateDirectory(modsDir);

        // Something to find, so a report that followed the name would have plenty to say.
        var secret = Path.Combine(root, "secret.zip");
        File.WriteAllText(secret, new string('x', 4242));

        try
        {
            var locked = new PackLock
            {
                GameVersion = "1.22.5",
                Mods = [new LockedMod { ModId = "olla", Version = "1.0.0", FileName = "../secret.zip" }],
            };

            var report = Diagnostics.Report(
                pack: new PackManifest { Id = "p", GameVersion = "1.22.5" },
                locked: locked,
                modsDir: modsDir);

            Assert.Contains("not inspected", report);

            // Neither its size nor its existence is confirmed either way.
            Assert.DoesNotContain("4,242", report);
            Assert.DoesNotContain("MISSING FROM DISK", report);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
