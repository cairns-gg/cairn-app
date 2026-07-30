using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

public class PackBundleTests : IDisposable
{
    private readonly string _authorRoot = Path.Combine(
        Path.GetTempPath(), "Cairn-author-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly string _recipientRoot = Path.Combine(
        Path.GetTempPath(), "Cairn-recip-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly PackStore _author;
    private readonly PackStore _recipient;

    public PackBundleTests()
    {
        _author = new PackStore(_authorRoot);
        _recipient = new PackStore(_recipientRoot);
    }

    public void Dispose()
    {
        foreach (var d in new[] { _authorRoot, _recipientRoot })
            if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
    }

    private PackManifest AuthorPack()
    {
        var manifest = _author.Create("anego", "1.22.5", "Anego Server", "host:42420");
        manifest.Mods.Add(new PackMod { ModId = "glassview" });
        manifest.Mods.Add(new PackMod { ModId = "unchisel" });
        _author.Save(manifest);

        new PackLock
        {
            GameVersion = "1.22.5",
            Mods =
            [
                new LockedMod { ModId = "glassview", Version = "1.3.0", FileName = "glassview_1.3.0.zip",
                                Url = "https://example/g.zip", Sha256 = new string('a', 64), Side = "client" },
                new LockedMod { ModId = "unchisel", Version = "1.2.0", FileName = "unchisel_1.2.0.zip",
                                Url = "https://example/u.zip", Sha256 = new string('b', 64), Side = "client" },
            ],
        }.Save(_author.LockPath("anego"));

        return manifest;
    }

    [Fact]
    public void A_pack_round_trips_through_export_and_import()
    {
        AuthorPack();

        var shared = _author.Export("anego");
        var imported = _recipient.Import(PackBundle.Parse(shared));

        Assert.Equal("anego", imported.Id);
        Assert.Equal("Anego Server", imported.Name);
        Assert.Equal("1.22.5", imported.GameVersion);
        Assert.Equal("host:42420", imported.Connect);
        Assert.Equal(2, imported.Mods.Count);
        Assert.True(_recipient.Exists("anego"));
    }

    [Fact]
    public void Importing_with_the_lock_pins_the_authors_exact_versions()
    {
        AuthorPack();

        var imported = _recipient.Import(PackBundle.Parse(_author.Export("anego")));

        // Pinning is what makes a shared pack reproducible: without it the recipient
        // would resolve newest-compatible, which may not be what the author tested.
        Assert.Equal("1.3.0", imported.Mods.Single(m => m.ModId == "glassview").Version);
        Assert.Equal("1.2.0", imported.Mods.Single(m => m.ModId == "unchisel").Version);

        // The author's lock travels too, so the first sync can verify the bytes.
        var lockFile = _recipient.LoadLock("anego");
        Assert.NotNull(lockFile);
        Assert.Equal(new string('a', 64), lockFile!.Mods.Single(m => m.ModId == "glassview").Sha256);
    }

    [Fact]
    public void Importing_loose_tracks_newest_instead_of_pinning()
    {
        AuthorPack();

        var imported = _recipient.Import(PackBundle.Parse(_author.Export("anego")), pinToLock: false);

        Assert.All(imported.Mods, m => Assert.Null(m.Version));
    }

    [Fact]
    public void Exporting_without_the_lock_shares_intent_only()
    {
        AuthorPack();

        var shared = _author.Export("anego", includeLock: false);
        var bundle = PackBundle.Parse(shared);

        Assert.Null(bundle.Lock);

        var imported = _recipient.Import(bundle);
        Assert.All(imported.Mods, m => Assert.Null(m.Version));
        Assert.Null(_recipient.LoadLock("anego"));
    }

    [Fact]
    public void An_id_collision_can_be_resolved_by_renaming_on_import()
    {
        AuthorPack();
        var shared = _author.Export("anego");

        _recipient.Import(PackBundle.Parse(shared));

        // Second import of the same pack collides...
        var clash = Assert.Throws<InvalidOperationException>(
            () => _recipient.Import(PackBundle.Parse(shared)));
        Assert.Contains("already exists", clash.Message);

        // ...unless it is given a new id.
        var renamed = _recipient.Import(PackBundle.Parse(shared), asId: "anego-copy");
        Assert.Equal("anego-copy", renamed.Id);
        Assert.True(_recipient.Exists("anego-copy"));
    }

    [Fact]
    public void An_imported_id_is_validated_like_any_other()
    {
        AuthorPack();
        var shared = _author.Export("anego");

        Assert.Throws<InvalidOperationException>(
            () => _recipient.Import(PackBundle.Parse(shared), asId: "../../escape"));

        Assert.False(Directory.Exists(Path.Combine(_recipientRoot, "..", "..", "escape")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"formatVersion":1}""")]
    public void Junk_is_rejected_with_an_explanation(string json)
        => Assert.Throws<InvalidDataException>(() => PackBundle.Parse(json));

    [Fact]
    public void A_bundle_from_a_newer_Cairn_is_refused_rather_than_half_understood()
    {
        var future = """
        { "formatVersion": 99, "pack": { "id": "x", "gameVersion": "1.22.5", "mods": [] } }
        """;

        var e = Assert.Throws<InvalidDataException>(() => PackBundle.Parse(future));
        Assert.Contains("newer Cairn", e.Message);
    }

    [Fact]
    public void A_bundle_carrying_an_invalid_manifest_is_refused()
    {
        // ">=" is the trap the manifest layer exists to catch; it must not sneak in
        // through an import either.
        var bad = """
        { "formatVersion": 1, "pack": { "id": "x", "gameVersion": ">=1.22.0", "mods": [] } }
        """;

        Assert.Throws<InvalidDataException>(() => PackBundle.Parse(bad));
    }
}
