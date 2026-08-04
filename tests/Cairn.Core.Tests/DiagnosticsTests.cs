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
