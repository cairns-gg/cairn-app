using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Remembering what a check answered.
///
/// The check costs one ModDB request per unpinned mod, so pressing the button twice is
/// thirty requests to be told the same thing. What has to hold is that the answer is only
/// reused while it is still true: time alone is not enough, because adding a mod or
/// retargeting a version changes what the check would say well inside the lifetime.
/// </summary>
public class ModUpdateCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-updcache-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly ModUpdateCache _cache;
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    public ModUpdateCacheTests() => _cache = new ModUpdateCache(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static PackManifest Pack(params string[] mods) => new()
    {
        Id = "anego",
        GameVersion = "1.22.5",
        Mods = [.. mods.Select(m => new PackMod { ModId = m })],
    };

    private static PackLock Lock(params (string Mod, string Version)[] mods) => new()
    {
        GameVersion = "1.22.5",
        Mods = [.. mods.Select(m => new LockedMod { ModId = m.Mod, Version = m.Version })],
    };

    private static List<ModUpdate> Updates(params string[] mods) =>
        [.. mods.Select(m => new ModUpdate(m, "1.0.0", "2.0.0"))];

    [Fact]
    public void An_answer_is_given_back_while_it_stands()
    {
        var print = ModUpdateCache.Fingerprint(Pack("carryon"), Lock(("carryon", "1.0.0")));

        _cache.Save("anego", print, Updates("carryon"), Now);

        var got = _cache.Get("anego", print, Now.AddMinutes(9));

        Assert.NotNull(got);
        Assert.Equal("carryon", got.Single().ModId);
    }

    [Fact]
    public void An_answer_older_than_its_lifetime_is_not()
    {
        var print = ModUpdateCache.Fingerprint(Pack("carryon"), null);

        _cache.Save("anego", print, Updates("carryon"), Now);

        Assert.Null(_cache.Get("anego", print, Now + ModUpdateCache.Lifetime.Add(TimeSpan.FromSeconds(1))));
    }

    [Fact]
    public void Nothing_remembered_is_not_an_error()
    {
        Assert.Null(_cache.Get("never-checked", "abc", Now));
    }

    // ---- the fingerprint, which is what makes this safe ----

    [Fact]
    public void Adding_a_mod_changes_what_the_check_would_say()
    {
        var before = ModUpdateCache.Fingerprint(Pack("carryon"), null);
        var after = ModUpdateCache.Fingerprint(Pack("carryon", "genelib"), null);

        Assert.NotEqual(before, after);

        // The point: the old answer is not handed back for the new pack, without anything
        // having to remember to invalidate it.
        _cache.Save("anego", before, Updates("carryon"), Now);
        Assert.Null(_cache.Get("anego", after, Now));
    }

    [Fact]
    public void Pinning_a_mod_changes_it_too()
    {
        var loose = Pack("carryon");
        var pinned = Pack("carryon");
        pinned.Mods[0].Version = "1.0.0";

        // A pinned mod is skipped by the check entirely, so the answer is a different one.
        Assert.NotEqual(
            ModUpdateCache.Fingerprint(loose, null),
            ModUpdateCache.Fingerprint(pinned, null));
    }

    [Fact]
    public void Retargeting_the_game_version_changes_it()
    {
        var before = Pack("carryon");
        var after = Pack("carryon");
        after.GameVersion = "1.22.6";

        Assert.NotEqual(
            ModUpdateCache.Fingerprint(before, null),
            ModUpdateCache.Fingerprint(after, null));
    }

    [Fact]
    public void Syncing_changes_it()
    {
        // What is installed is half the answer: once the update is applied, the same
        // remembered list would go on claiming it is available.
        Assert.NotEqual(
            ModUpdateCache.Fingerprint(Pack("carryon"), Lock(("carryon", "1.0.0"))),
            ModUpdateCache.Fingerprint(Pack("carryon"), Lock(("carryon", "2.0.0"))));
    }

    [Fact]
    public void Order_does_not_change_it()
    {
        // Manifest order is presentation, not meaning; a reordered pack must not throw the
        // answer away.
        Assert.Equal(
            ModUpdateCache.Fingerprint(Pack("carryon", "genelib"), null),
            ModUpdateCache.Fingerprint(Pack("genelib", "carryon"), null));
    }

    // ---- clock and clearing ----

    [Fact]
    public void An_answer_from_the_future_is_refused()
    {
        // A clock correction or a suspended laptop would otherwise make an answer look
        // freshly made for as long as the skew lasts.
        var print = ModUpdateCache.Fingerprint(Pack("carryon"), null);

        _cache.Save("anego", print, Updates("carryon"), Now.AddHours(2));

        Assert.Null(_cache.Get("anego", print, Now));
    }

    [Fact]
    public void One_pack_can_be_forgotten_without_touching_the_others()
    {
        var print = ModUpdateCache.Fingerprint(Pack("carryon"), null);

        _cache.Save("anego", print, Updates("carryon"), Now);
        _cache.Save("other", print, Updates("carryon"), Now);

        _cache.Clear("anego");

        Assert.Null(_cache.Get("anego", print, Now));
        Assert.NotNull(_cache.Get("other", print, Now));
    }

    [Fact]
    public void Clearing_everything_reports_what_it_freed()
    {
        var print = ModUpdateCache.Fingerprint(Pack("carryon"), null);

        _cache.Save("anego", print, Updates("carryon"), Now);
        _cache.Save("other", print, Updates("genelib"), Now);

        Assert.Equal(2, _cache.Count());
        Assert.True(_cache.Size() > 0);

        var freed = _cache.Clear();

        Assert.True(freed > 0);
        Assert.Equal(0, _cache.Count());
        Assert.Null(_cache.Get("anego", print, Now));
    }

    [Fact]
    public void An_unreadable_answer_reads_as_none()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "anego.json"), "{ not json");

        // A cache that cannot be read means the same as no cache, and is not worth failing
        // a check over.
        Assert.Null(_cache.Get("anego", "whatever", Now));
    }

    [Fact]
    public void A_remembered_answer_reports_no_progress()
    {
        // Worth stating because the progress line is driven by these reports: a cached
        // answer returns without asking ModDB anything, so nothing is reported and the
        // indicator never appears — which is right, since there is nothing to wait for.
        var print = ModUpdateCache.Fingerprint(Pack("carryon"), null);
        _cache.Save("anego", print, Updates("carryon"), Now);

        Assert.NotNull(_cache.Get("anego", print, Now.AddMinutes(1)));
    }
}
