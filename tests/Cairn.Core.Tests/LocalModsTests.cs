using System.IO.Compression;
using Cairn.Core.Launch;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// A folder of mods somebody is writing, loaded alongside every pack.
///
/// The folder is handed to the game as it is; what these hold to is the prediction made
/// about it at launch. The game keeps the highest version of a duplicated mod id and
/// disables the rest, so a rebuilt mod that is not newer than the pack's silently loses —
/// and the whole value of this type is saying so before somebody spends an evening testing
/// the old code.
/// </summary>
public class LocalModsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-localmods-" + Guid.NewGuid().ToString("n")[..8]);

    private string Local => Path.Combine(_root, "local");
    private string PackMods => Path.Combine(_root, "pack", "Mods");

    public LocalModsTests()
    {
        Directory.CreateDirectory(Local);
        Directory.CreateDirectory(PackMods);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static void Zip(string path, string modInfo)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("modinfo.json").Open());
        writer.Write(modInfo);
    }

    private static string Info(string id, string? version) => version is null
        ? $$"""{ "type": "code", "modid": "{{id}}", "name": "{{id}}" }"""
        : $$"""{ "type": "code", "modid": "{{id}}", "name": "{{id}}", "version": "{{version}}" }""";

    private void InPack(string id, string? version) =>
        Zip(Path.Combine(PackMods, $"{id}_{version}.zip"), Info(id, version));

    private void InLocal(string id, string? version) =>
        Zip(Path.Combine(Local, $"{id}_{version}.zip"), Info(id, version));

    private LocalMod Only(LocalModsPlan plan) => Assert.Single(plan.Mods);

    // ---- the folder itself ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Nothing_set_adds_nothing_and_says_nothing(string? folder)
    {
        var plan = LocalMods.Plan(folder, PackMods);

        Assert.Null(plan.ModPath);
        Assert.Empty(plan.Lines);
    }

    [Fact]
    public void A_folder_that_has_gone_is_left_out_and_said()
    {
        var gone = Path.Combine(_root, "unplugged");

        var plan = LocalMods.Plan(gone, PackMods);

        Assert.Null(plan.ModPath);
        Assert.Equal("launch-local-missing", Assert.Single(plan.Lines).Key);
    }

    [Fact]
    public void Every_launch_names_the_folder_even_with_nothing_in_it()
    {
        // Somebody forgets this is on. The first line of every launch is the reminder.
        var plan = LocalMods.Plan(Local, PackMods);

        Assert.Equal(Local, plan.ModPath);
        Assert.Equal("launch-local-from", Assert.Single(plan.Lines).Key);
    }

    // ---- what the game will decide ----

    [Fact]
    public void A_mod_the_pack_does_not_have_is_simply_added()
    {
        InLocal("mymod", "0.1.0");

        var mod = Only(LocalMods.Plan(Local, PackMods));

        Assert.Equal(LocalModOutcome.Adds, mod.Outcome);
        Assert.Equal("mymod", mod.ModId);
    }

    [Fact]
    public void A_higher_version_replaces_the_packs()
    {
        InPack("mymod", "1.0.0");
        InLocal("mymod", "1.0.1");

        var mod = Only(LocalMods.Plan(Local, PackMods));

        Assert.Equal(LocalModOutcome.Overrides, mod.Outcome);
        Assert.Equal("1.0.0", mod.PackVersion);
        Assert.Equal("launch-local-overrides", mod.Line.Key);
    }

    [Theory]
    [InlineData("1.0.0", "1.1.0")]
    [InlineData("1.9.0", "1.10.0")]   // numerically, not lexically
    public void A_lower_version_loses_and_says_how_to_win(string local, string pack)
    {
        InPack("mymod", pack);
        InLocal("mymod", local);

        var mod = Only(LocalMods.Plan(Local, PackMods));

        Assert.Equal(LocalModOutcome.Loses, mod.Outcome);
        Assert.Equal("launch-local-loses", mod.Line.Key);
    }

    [Theory]
    [InlineData("1.0.0-dev")]
    [InlineData("1.0.0-dev.3")]
    [InlineData("1.0.0-rc.1")]
    public void A_pre_release_of_the_packs_version_loses_and_says_why(string local)
    {
        // The trap: it reads as newer to everybody except the game.
        InPack("mymod", "1.0.0");
        InLocal("mymod", local);

        var mod = Only(LocalMods.Plan(Local, PackMods));

        Assert.Equal(LocalModOutcome.Loses, mod.Outcome);
        Assert.Equal("launch-local-loses-prerelease", mod.Line.Key);
    }

    [Fact]
    public void A_pre_release_of_a_later_version_wins()
    {
        InPack("mymod", "1.0.0");
        InLocal("mymod", "1.0.1-dev");

        Assert.Equal(LocalModOutcome.Overrides, Only(LocalMods.Plan(Local, PackMods)).Outcome);
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("1.0", "1.0.0")]      // the game calls each lower than the other
    public void The_same_version_is_not_promised_either_way(string local, string pack)
    {
        InPack("mymod", pack);
        InLocal("mymod", local);

        var mod = Only(LocalMods.Plan(Local, PackMods));

        Assert.Equal(LocalModOutcome.Undecided, mod.Outcome);
        Assert.Equal("launch-local-tie", mod.Line.Key);
    }

    [Fact]
    public void Without_a_version_nothing_is_promised()
    {
        InPack("mymod", "1.0.0");
        InLocal("mymod", null);

        Assert.Equal(LocalModOutcome.Undecided, Only(LocalMods.Plan(Local, PackMods)).Outcome);
    }

    [Fact]
    public void The_highest_of_the_packs_copies_is_the_one_to_beat()
    {
        InPack("mymod", "1.0.0");
        InPack("mymod", "1.2.0");
        InLocal("mymod", "1.1.0");

        var mod = Only(LocalMods.Plan(Local, PackMods));

        Assert.Equal(LocalModOutcome.Loses, mod.Outcome);
        Assert.Equal("1.2.0", mod.PackVersion);
    }

    // ---- what counts as a mod ----

    [Fact]
    public void An_unpacked_folder_is_read_like_a_zip()
    {
        // What a mod project's build writes before anything zips it.
        InPack("mymod", "1.0.0");

        var dir = Directory.CreateDirectory(Path.Combine(Local, "mymod")).FullName;
        File.WriteAllText(Path.Combine(dir, "ModInfo.json"), Info("mymod", "1.0.1"));

        var mod = Only(LocalMods.Plan(Local, PackMods));

        Assert.Equal(LocalModOutcome.Overrides, mod.Outcome);
        Assert.Equal("mymod", mod.Entry);
    }

    [Fact]
    public void A_mod_with_no_modid_is_known_by_its_name_as_the_game_derives_it()
    {
        InPack("mymod", "1.0.0");
        Zip(Path.Combine(Local, "x.zip"), """{ "type": "code", "name": "My Mod", "version": "2.0.0" }""");

        var mod = Only(LocalMods.Plan(Local, PackMods));

        Assert.Equal("mymod", mod.ModId);
        Assert.Equal(LocalModOutcome.Overrides, mod.Outcome);
    }

    [Theory]
    [InlineData("Tweak.cs")]
    [InlineData("Tweak.DLL")]
    public void A_code_mod_is_loaded_without_a_prediction(string name)
    {
        File.WriteAllText(Path.Combine(Local, name), "");

        var mod = Only(LocalMods.Plan(Local, PackMods));

        Assert.Equal(LocalModOutcome.Unread, mod.Outcome);
        Assert.Equal("launch-local-code", mod.Line.Key);
    }

    [Fact]
    public void A_folder_with_no_modinfo_is_said_rather_than_skipped()
    {
        // The game tries to load it and fails, so its absence from the log would be a lie.
        Directory.CreateDirectory(Path.Combine(Local, "notes"));

        var mod = Only(LocalMods.Plan(Local, PackMods));

        Assert.Equal(LocalModOutcome.Unread, mod.Outcome);
        Assert.Equal("launch-local-unread", mod.Line.Key);
    }

    [Fact]
    public void A_mod_with_no_name_is_said_to_be_refused_rather_than_loading()
    {
        // The game marks `name` required and disables the mod without it — found by running
        // a dedicated server on a folder mod whose modinfo.json left it out, after this had
        // reported it as loading.
        var dir = Directory.CreateDirectory(Path.Combine(Local, "newthing")).FullName;
        File.WriteAllText(Path.Combine(dir, "modinfo.json"),
            """{ "modid": "newthing", "version": "0.1.0", "type": "content" }""");

        var mod = Only(LocalMods.Plan(Local, PackMods));

        Assert.Equal(LocalModOutcome.Unread, mod.Outcome);
        Assert.Equal("launch-local-refused", mod.Line.Key);
        Assert.Contains("\"name\"", mod.Line.Text);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData(".DS_Store")]
    [InlineData("build.log")]
    public void Files_the_game_ignores_are_ignored(string name)
    {
        File.WriteAllText(Path.Combine(Local, name), "");

        Assert.Empty(LocalMods.Plan(Local, PackMods).Mods);
    }

    // ---- the reader underneath ----

    [Fact]
    public void Describe_reads_an_unpacked_folder()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "unpacked")).FullName;
        File.WriteAllText(Path.Combine(dir, "modinfo.json"),
            """{ "modid": "mymod", "version": "1.2.3", "dependencies": { "game": "", "genelib": "1.0.0" } }""");

        var info = ModDependencies.Describe(dir);

        Assert.Null(info.Problem);
        Assert.Equal("mymod", info.ModId);
        Assert.Equal("1.2.3", info.Version);
        Assert.Equal(["genelib"], ModDependencies.Read(dir).Dependencies);
    }

    [Fact]
    public void Describe_bounds_a_folders_modinfo_as_it_does_a_zips()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "huge")).FullName;
        File.WriteAllText(Path.Combine(dir, "modinfo.json"),
            new string(' ', ModDependencies.MaxModInfoBytes + 1) + "{}");

        Assert.NotNull(ModDependencies.Describe(dir).Problem);
    }
}
