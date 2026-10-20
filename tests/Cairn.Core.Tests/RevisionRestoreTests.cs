using System.Text.Json.Nodes;
using Cairn.Core.Launch;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Putting an earlier published revision back into the author's own copy. The case it is
/// for: an author published a set that turned out broken, and wants the one before it —
/// played first, then published again as the newest revision.
/// </summary>
public class RevisionRestoreTests : IDisposable
{
    private const string Url = "https://cairns.gg/dizzyd/anego";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "Cairn-restoretest-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly PackStore _store;

    public RevisionRestoreTests() => _store = new PackStore(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string ConfigFile => Path.Combine(_store.DataDir("anego"), "ModConfig", "terrainslabs.json");

    /// <summary>
    /// The pack as it is after the bad publish: glassview moved on to 1.3.0, carryon added,
    /// and a setting changed. Published from here as revision 6 with the address stripped.
    /// </summary>
    private void PublishedBroken(PackLink? link = null)
    {
        _store.Create("anego", "1.22.5", "Anego", "host:42420");

        var manifest = _store.Load("anego");
        manifest.Mods = [new PackMod { ModId = "glassview" }, new PackMod { ModId = "carryon" }];
        manifest.ModConfig = new Dictionary<string, JsonObject>
        {
            ["terrainslabs.json"] = new() { ["compatibleMods"] = new JsonArray() },
        };
        _store.Save(manifest);

        new PackLock
        {
            GameVersion = "1.22.5",
            Mods =
            [
                new LockedMod { ModId = "glassview", Version = "1.3.0", FileName = "glassview_1.3.0.zip", Sha256 = "bb" },
                new LockedMod { ModId = "carryon", Version = "1.8.0", FileName = "carryon_1.8.0.zip", Sha256 = "cc" },
            ],
        }.Save(_store.LockPath("anego"));

        Directory.CreateDirectory(Path.GetDirectoryName(ConfigFile)!);
        File.WriteAllText(ConfigFile, """{ "compatibleMods": [], "slabHeight": 4 }""");

        _store.SaveLink("anego", link ?? new PackLink
        {
            Role = PackRole.Author,
            Url = Url,
            Revision = 6,
            Published = new PublishRecord
            {
                Connect = "stripped",
                Fingerprint = PackLink.Fingerprint(_store.PublishedDocument("anego", stripConnect: true)),
            },
        });
    }

    /// <summary>Revision 5 as the site archived it: before carryon, glassview at 1.2.0.</summary>
    private static PackBundle Revision5(string canonicalUrl = Url) => PackBundle.Parse($$"""
        {
          "formatVersion": 1,
          "pack": {
            "id": "anego", "name": "Anego", "gameVersion": "1.22.5",
            "mods": [ { "modid": "glassview" } ],
            "modConfig": { "terrainslabs.json": { "compatibleMods": [ "footprints" ] } }
          },
          "lock": {
            "gameVersion": "1.22.5",
            "mods": [
              { "modid": "glassview", "version": "1.2.0", "filename": "glassview_1.2.0.zip",
                "url": "https://mods.vintagestory.at/download?fileid=2", "releaseId": 1,
                "fileId": 2, "sha256": "aa" }
            ]
          },
          "publishedBy": "dizzyd",
          "canonicalUrl": "{{canonicalUrl}}",
          "revision": 5
        }
        """);

    [Fact]
    public void Restoring_puts_the_earlier_mods_and_lock_back()
    {
        PublishedBroken();

        _store.RestoreRevision("anego", Revision5());

        var manifest = _store.Load("anego");
        Assert.Equal("anego", manifest.Id);
        Assert.Equal(["glassview"], manifest.Mods.Select(m => m.ModId));

        var locked = _store.LoadLock("anego")!;
        Assert.Equal("1.2.0", locked.Mods.Single().Version);

        // The files the restored lock stops naming are left for the next sync to remove.
        // Without this, carryon and the newer glassview would stay in Mods, out of the lock
        // and loaded by the game all the same — the broken set, still running.
        Assert.Contains("carryon_1.8.0.zip", locked.Retired!);
        Assert.Contains("glassview_1.3.0.zip", locked.Retired!);
    }

    [Fact]
    public void A_server_address_the_revision_does_not_carry_is_kept()
    {
        PublishedBroken();

        _store.RestoreRevision("anego", Revision5());

        // It was stripped on the way up, which is not the same as the pack not having one.
        Assert.Equal("host:42420", _store.Load("anego").Connect);
    }

    [Fact]
    public void The_restored_settings_reach_the_files_and_so_the_next_publish()
    {
        PublishedBroken();

        _store.RestoreRevision("anego", Revision5());

        // Publishing reads carried settings back out of the files. A restore that changed
        // only the manifest would publish the very value it was meant to undo.
        var file = JsonNode.Parse(File.ReadAllText(ConfigFile))!;
        Assert.Equal("footprints", file["compatibleMods"]![0]!.GetValue<string>());
        Assert.Equal(4, file["slabHeight"]!.GetValue<int>());

        Assert.Contains("footprints", _store.PublishedDocument("anego", stripConnect: true));
    }

    [Fact]
    public void A_restored_pack_has_changes_to_publish_and_nothing_was_sent()
    {
        PublishedBroken();
        Assert.Equal(ShareStatus.Shared, _store.ShareStateFor("anego").Status);

        _store.RestoreRevision("anego", Revision5());

        // The publish record is untouched, so the pack says what is true: the site is still
        // serving revision 6, and this copy is different from it.
        Assert.Equal(ShareStatus.Pending, _store.ShareStateFor("anego").Status);
        Assert.Equal(6, _store.LoadLink("anego")!.Revision);
    }

    [Fact]
    public void A_withdrawn_pack_can_be_restored()
    {
        PublishedBroken(new PackLink { Role = PackRole.Author, Url = Url, Revision = 6, Withdrawn = true });

        _store.RestoreRevision("anego", Revision5());

        Assert.Equal(["glassview"], _store.Load("anego").Mods.Select(m => m.ModId));
    }

    public static TheoryData<string, PackLink?> NotYours => new()
    {
        { "never published", null },
        { "followed", new PackLink { Role = PackRole.Follower, Url = Url, Revision = 6, Following = true } },

        // Author and a URL, nothing published and not withdrawn: a taken-over pack, whose
        // URL is where it came from. That history is somebody else's.
        { "taken over", new PackLink { Role = PackRole.Author, Url = Url, Revision = 6 } },
    };

    [Theory]
    [MemberData(nameof(NotYours))]
    public void Only_a_pack_published_from_here_can_be_restored(string why, PackLink? link)
    {
        _store.Create("anego", "1.22.5", "Anego", null);
        if (link is not null) _store.SaveLink("anego", link);

        var before = File.ReadAllText(Path.Combine(_store.PackDir("anego"), "pack.json"));

        Assert.Throws<InvalidOperationException>(() => _store.RestoreRevision("anego", Revision5()));
        Assert.True(before == File.ReadAllText(Path.Combine(_store.PackDir("anego"), "pack.json")), why);
    }

    [Fact]
    public void A_revision_of_another_pack_is_refused_before_anything_is_written()
    {
        PublishedBroken();
        var before = File.ReadAllText(Path.Combine(_store.PackDir("anego"), "pack.json"));

        Assert.Throws<InvalidDataException>(() =>
            _store.RestoreRevision("anego", Revision5("https://cairns.gg/someone/else")));

        Assert.Equal(before, File.ReadAllText(Path.Combine(_store.PackDir("anego"), "pack.json")));
    }

    [Fact]
    public void The_preview_names_each_mod_that_would_move()
    {
        PublishedBroken();

        var preview = RevisionPreview.Of(_store, "anego", 5, Revision5());

        Assert.True(preview.Anything);
        Assert.Equal(5, preview.Revision);

        var byMod = preview.Changes.ToDictionary(c => c.ModId);
        Assert.Equal(ModChangeKind.Removed, byMod["carryon"].Kind);
        Assert.Equal(ModChangeKind.Relocked, byMod["glassview"].Kind);
        Assert.Equal("1.3.0 → 1.2.0", byMod["glassview"].Describe());

        Assert.StartsWith("Restoring revision 5:", preview.Summary);

        // Compared with the address treated as the restore will treat it: kept. A revision
        // published with it stripped must not read as taking it away.
        Assert.False(preview.Delta.ConnectChanged);
    }

    [Fact]
    public void A_revision_identical_to_the_pack_has_nothing_to_restore()
    {
        PublishedBroken();

        var same = PackBundle.Parse(_store.PublishedDocument("anego", stripConnect: true));
        var preview = RevisionPreview.Of(_store, "anego", 6, same);

        Assert.False(preview.Anything);
        Assert.Empty(preview.Changes);
    }

    [Fact]
    public void The_world_warning_names_the_packs_worlds()
    {
        var warning = RevisionPreview.WorldWarning(["Home", "Winter run"], preview: null);

        Assert.Contains("Home, Winter run", warning);
        Assert.Contains("Back up your worlds", warning);
    }

    [Fact]
    public void With_no_worlds_in_the_pack_the_warning_still_says_where_one_might_be()
    {
        // A pack played only on a server has its world there, and that world was played
        // with these mods all the same.
        Assert.Contains("on a server", RevisionPreview.WorldWarning([], preview: null));
    }

    [Theory]
    [InlineData("1.21.6", true)]
    [InlineData("1.22.5", false)]
    [InlineData("1.22.10", false)]
    public void A_revision_for_an_older_game_says_so_in_the_warning(string revisionGame, bool older)
    {
        PublishedBroken();

        var revision = PackBundle.Parse(Revision5().Pack is { } pack
            ? PackBundle.Serialize(Retargeted(pack, revisionGame)) : "");
        var preview = RevisionPreview.Of(_store, "anego", 5, revision);

        Assert.Equal(older, preview.OlderGame);
        Assert.Equal(older, RevisionPreview.WorldWarning(["Home"], preview).Contains("older game"));
    }

    private static PackManifest Retargeted(PackManifest pack, string game)
    {
        pack.GameVersion = game;
        return pack;
    }

    /// <summary>
    /// The case a restore is most often for. Launching applied the setting and recorded it;
    /// the author then changed it in game, published, and found it broke the pack. To the
    /// launch-time merge that difference is the player's own edit, and it keeps it — so a
    /// restore going through that merge left the broken value in the file, and the next
    /// publish read it back out and sent it again.
    /// </summary>
    [Fact]
    public void A_setting_changed_in_game_since_the_last_launch_is_still_restored()
    {
        PublishedBroken();

        // A launch: the pack's value goes in and is recorded.
        var manifest = _store.Load("anego");
        manifest.ModConfig = new Dictionary<string, JsonObject>
        {
            ["terrainslabs.json"] = new() { ["compatibleMods"] = new JsonArray("footprints") },
        };
        _store.Save(manifest);
        ModConfigFiles.Apply(_store.DataDir("anego"), manifest.ModConfig);

        // Changed in game, and the pack catching up with the file as the pane does.
        File.WriteAllText(ConfigFile, """{ "compatibleMods": [ "broken" ], "slabHeight": 4 }""");
        _store.RefreshModConfig("anego");

        var restored = _store.RestoreRevision("anego", Revision5());

        var file = JsonNode.Parse(File.ReadAllText(ConfigFile))!;
        Assert.Equal("footprints", file["compatibleMods"]![0]!.GetValue<string>());
        Assert.DoesNotContain("broken", _store.PublishedDocument("anego", stripConnect: true));
        Assert.Empty(restored.NotWritten);
    }

    [Fact]
    public void A_setting_that_could_not_be_written_is_reported()
    {
        PublishedBroken();

        // Comments are not something Cairn will write back around, so the file is refused.
        File.WriteAllText(ConfigFile, """{ /* mine */ "compatibleMods": [] }""");

        var restored = _store.RestoreRevision("anego", Revision5());

        Assert.Contains(restored.NotWritten, c => c.File == "terrainslabs.json");
    }

    [Fact]
    public void A_revision_published_while_the_site_had_another_address_is_still_this_packs()
    {
        PublishedBroken();

        // The history is asked for by owner and slug, so it lists this one; refusing it
        // here for its host would offer a revision that can never be restored.
        _store.RestoreRevision("anego", Revision5("https://old.cairns.example/dizzyd/anego"));

        Assert.Equal(["glassview"], _store.Load("anego").Mods.Select(m => m.ModId));
    }

    [Fact]
    public void The_preview_is_of_the_revision_asked_for_whatever_the_document_says()
    {
        PublishedBroken();

        var preview = RevisionPreview.Of(_store, "anego", 4, Revision5());

        Assert.Equal(4, preview.Revision);
        Assert.StartsWith("Restoring revision 4:", preview.Summary);
    }
}
