using System.Net;
using Cairn.Core.ModDb;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// The rules around a mod named by address: what an address may be, what the file gets
/// called, and how the rest of Cairn — validation, the update merge, publishing, the
/// version-change preview — treats an entry that has one. The install itself is in
/// <see cref="PackSyncUrlTests"/>.
/// </summary>
public class ModUrlTests
{
    [Theory]
    [InlineData("https://files.example/mod.zip")]
    [InlineData("https://files.example/download?id=5")]
    [InlineData("http://127.0.0.1:8080/mod.zip")]
    [InlineData("http://localhost/mod.zip")]
    public void An_https_address_or_a_loopback_one_is_fine(string url) =>
        Assert.Null(ModUrl.Problem(url));

    [Theory]
    [InlineData("http://files.example/mod.zip")]
    [InlineData("ftp://files.example/mod.zip")]
    [InlineData("file:///tmp/mod.zip")]
    [InlineData("mod.zip")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_not(string? url) =>
        Assert.NotNull(ModUrl.Problem(url));

    [Fact]
    public void Plaintext_across_a_network_is_named_as_the_reason()
    {
        Assert.Contains("https", ModUrl.Problem("http://files.example/mod.zip"));
    }

    [Theory]
    [InlineData("https://files.example/anego_1.2.0.zip", "anego_1.2.0.zip")]
    [InlineData("https://files.example/a%20mod.zip", "a mod.zip")]
    [InlineData("https://files.example/download?id=5", "anegotweaks.zip")]
    [InlineData("https://files.example/mod.dll", "anegotweaks.zip")]
    [InlineData("https://files.example/", "anegotweaks.zip")]
    public void The_file_is_named_after_the_address_when_that_is_a_zip_name_and_the_mod_otherwise(
        string url, string expected) =>
        Assert.Equal(expected, ModUrl.FileNameFor(url, "anegotweaks"));

    [Fact]
    public void A_mod_id_that_cannot_be_a_filename_yields_nothing()
    {
        Assert.Null(ModUrl.FileNameFor("https://files.example/download?id=5", "../evil"));
    }

    [Theory]
    [InlineData("https://files.example/mod.zip", true)]
    [InlineData("  https://files.example/mod.zip ", true)]
    [InlineData("glassview", false)]
    [InlineData("", false)]
    public void An_address_is_told_from_a_search_term(string text, bool url) =>
        Assert.Equal(url, ModUrl.LooksLikeUrl(text));

    [Fact]
    public void The_manifest_refuses_a_plaintext_address_and_a_pin_beside_an_address()
    {
        var pack = new PackManifest
        {
            Id = "anego",
            GameVersion = "1.22.5",
            Mods =
            [
                new PackMod { ModId = "clear", Url = "http://files.example/mod.zip" },
                new PackMod { ModId = "both", Url = "https://files.example/mod.zip", Version = "1.0.0" },
                new PackMod { ModId = "fine", Url = "https://files.example/mod.zip" },
            ],
        };

        var problems = pack.ModProblems().ToDictionary(p => p.Mod.ModId, p => p.Problem);

        Assert.Contains("https", problems["clear"]);
        Assert.Contains("pin", problems["both"]);
        Assert.DoesNotContain("fine", problems.Keys);
    }

    [Fact]
    public void The_address_survives_the_manifest_round_trip_and_is_absent_when_unset()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cairn-modurl-" + Guid.NewGuid().ToString("n")[..8]);
        try
        {
            var path = Path.Combine(dir, "pack.json");
            new PackManifest
            {
                Id = "anego",
                GameVersion = "1.22.5",
                Mods =
                [
                    new PackMod { ModId = "anegotweaks", Url = "https://files.example/mod.zip" },
                    new PackMod { ModId = "olla" },
                ],
            }.Save(path);

            var text = File.ReadAllText(path);
            Assert.Contains("\"url\": \"https://files.example/mod.zip\"", text);
            Assert.Equal(1, text.Split("\"url\"").Length - 1);

            var back = PackManifest.Load(path);
            Assert.Equal("https://files.example/mod.zip", back.Mods[0].Url);
            Assert.True(back.Mods[0].IsFromUrl);
            Assert.False(back.Mods[1].IsFromUrl);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_lock_writes_fromUrl_only_for_the_entries_it_is_true_of()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cairn-modurl-" + Guid.NewGuid().ToString("n")[..8]);
        try
        {
            var path = Path.Combine(dir, "pack.lock.json");
            new PackLock
            {
                GameVersion = "1.22.5",
                Mods =
                [
                    new LockedMod { ModId = "anegotweaks", Version = "1.0.0", FromUrl = true },
                    new LockedMod { ModId = "olla", Version = "1.0.0" },
                ],
            }.Save(path);

            var text = File.ReadAllText(path);
            Assert.Equal(1, text.Split("\"fromUrl\"").Length - 1);

            var back = PackLock.Load(path)!;
            Assert.True(back.Mods[0].FromUrl);
            Assert.False(back.Mods[1].FromUrl);

            // Cleared like every other location, and still known for what it was.
            back.Mods[0].Url = "https://files.example/mod.zip";
            back.ClearResolvedLocations();
            Assert.Equal("", back.Mods[0].Url);
            Assert.True(back.Mods[0].FromUrl);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // ---- the update merge ----

    private static PackManifest Pack(params PackMod[] mods) => new()
    {
        Id = "anego", GameVersion = "1.22.5", Mods = [.. mods],
    };

    private static PackMod At(string modId, string url) => new() { ModId = modId, Url = url };

    private static PackLock Locked(string modId, string version) => new()
    {
        GameVersion = "1.22.5",
        Mods = [new LockedMod { ModId = modId, Version = version, FromUrl = true }],
    };

    [Fact]
    public void An_author_moving_a_mods_address_is_a_change_a_follower_takes()
    {
        var @base = Pack(At("anegotweaks", "https://files.example/v1.zip"));
        var mine = Pack(At("anegotweaks", "https://files.example/v1.zip"));
        var theirs = Pack(At("anegotweaks", "https://files.example/v2.zip"));

        var plan = PackUpdatePlan.Between(mine, theirs, @base,
            myLock: Locked("anegotweaks", "1.0.0"), theirLock: Locked("anegotweaks", "2.0.0"));

        var change = Assert.Single(plan.Changes);
        Assert.Equal(ModChangeKind.Repinned, change.Kind);
        Assert.True(change.Take);
        Assert.Equal("1.0.0 → 2.0.0", change.Describe());
        Assert.True(plan.AnyChange);

        Assert.Equal("https://files.example/v2.zip", Assert.Single(plan.Merge().Mods).Url);
    }

    [Fact]
    public void An_address_the_follower_changed_themselves_is_asked_about_and_kept_when_they_say_so()
    {
        var @base = Pack(At("anegotweaks", "https://files.example/v1.zip"));
        var mine = Pack(At("anegotweaks", "https://mine.example/fork.zip"));
        var theirs = Pack(At("anegotweaks", "https://files.example/v2.zip"));

        var plan = PackUpdatePlan.Between(mine, theirs, @base);

        var change = Assert.Single(plan.Changes);
        Assert.Equal(ModChangeKind.PinConflict, change.Kind);
        Assert.False(change.Take);

        Assert.Equal("https://mine.example/fork.zip", Assert.Single(plan.Merge().Mods).Url);

        change.Take = true;
        Assert.Equal("https://files.example/v2.zip", Assert.Single(plan.Merge().Mods).Url);
    }

    [Fact]
    public void The_same_address_on_both_sides_is_no_change()
    {
        var pack = Pack(At("anegotweaks", "https://files.example/v1.zip"));

        var plan = PackUpdatePlan.Between(pack, pack, pack,
            myLock: Locked("anegotweaks", "1.0.0"), theirLock: Locked("anegotweaks", "1.0.0"));

        Assert.Empty(plan.Changes);
        Assert.False(plan.AnyChange);
    }

    [Fact]
    public void A_merge_carries_every_field_of_an_entry_and_not_just_its_version()
    {
        var theirs = Pack(
            new PackMod { ModId = "anegotweaks", Url = "https://files.example/v1.zip" },
            new PackMod { ModId = "oreveintracers", AcceptedFor = "1.22.5" });

        var plan = PackUpdatePlan.Between(Pack(), theirs, Pack());
        var merged = plan.Merge();

        Assert.Equal("https://files.example/v1.zip", merged.Mods[0].Url);
        Assert.Equal("1.22.5", merged.Mods[1].AcceptedFor);

        plan.Reset = true;
        Assert.Equal("https://files.example/v1.zip", plan.Merge().Mods[0].Url);
    }

    // ---- what else reads a manifest entry ----

    /// <summary>Fails every request, so a test can assert that none was made.</summary>
    private sealed class Offline : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    [Fact]
    public async Task Publishing_does_not_look_for_a_URL_mod_on_ModDB_and_says_where_it_is_from()
    {
        var offline = new Offline();
        var moddb = new ModDbClient(new HttpClient(offline));
        var pack = Pack(At("anegotweaks", "https://files.example/v1.zip"));

        var plan = await PublishPlan.PrepareAsync(pack, Locked("anegotweaks", "1.0.0"), moddb);

        Assert.Equal(0, offline.Requests);
        Assert.Empty(plan.Unresolvable);
        Assert.Equal("anegotweaks 1.0.0 (from files.example)", Assert.Single(plan.Mods).Describe());
    }

    [Fact]
    public async Task A_version_change_leaves_a_URL_mod_where_it_is_without_asking_ModDB()
    {
        var offline = new Offline();
        var moddb = new ModDbClient(new HttpClient(offline));
        var pack = Pack(At("anegotweaks", "https://files.example/v1.zip"));

        var plan = await GameVersionChange.PreviewAsync(moddb, pack, Locked("anegotweaks", "1.0.0"), "1.23.0");

        Assert.Equal(0, offline.Requests);
        var verdict = Assert.Single(plan.Mods);
        Assert.Equal(ModOutcome.Unchanged, verdict.Outcome);
        Assert.Contains("files.example", verdict.Note);
        Assert.False(plan.AnythingBreaks);
    }

    [Fact]
    public void The_update_cache_fingerprint_moves_with_the_address()
    {
        var a = ModUpdateCache.Fingerprint(Pack(At("anegotweaks", "https://files.example/v1.zip")), null);
        var b = ModUpdateCache.Fingerprint(Pack(At("anegotweaks", "https://files.example/v2.zip")), null);

        Assert.NotEqual(a, b);
    }
}
