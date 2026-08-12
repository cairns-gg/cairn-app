using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

public class PackStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "Cairn-storetest-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly PackStore _store;

    public PackStoreTests() => _store = new PackStore(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("anego")]
    [InlineData("vanilla-qol")]
    [InlineData("my_pack_2")]
    [InlineData("A1")]
    public void Valid_ids_are_accepted(string id) => Assert.True(PackStore.IsValidId(id));

    [Theory]
    [InlineData("../evil")]
    [InlineData("../../etc/passwd")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("has space")]
    [InlineData("")]
    [InlineData(null)]
    public void Ids_that_could_escape_the_store_are_rejected(string? id)
    {
        Assert.False(PackStore.IsValidId(id));
        // PackDir is the only place an id becomes a path, so it must refuse too.
        Assert.Throws<ArgumentException>(() => _store.PackDir(id!));
    }

    [Fact]
    public void A_rejected_id_never_creates_anything_on_disk()
    {
        Assert.Throws<ArgumentException>(() => _store.PackDir("../../escaped"));
        Assert.False(Directory.Exists(Path.Combine(_root, "..", "..", "escaped")));
    }

    [Fact]
    public void Create_then_load_round_trips()
    {
        _store.Create("anego", "1.22.5", "Anego Server", "host:42420");

        var loaded = _store.Load("anego");
        Assert.Equal("anego", loaded.Id);
        Assert.Equal("Anego Server", loaded.Name);
        Assert.Equal("1.22.5", loaded.GameVersion);
        Assert.Equal("host:42420", loaded.Connect);
        Assert.True(Directory.Exists(_store.ModsDir("anego")));
    }

    [Fact]
    public void Create_refuses_a_duplicate()
    {
        _store.Create("dup", "1.22.5");
        Assert.Throws<InvalidOperationException>(() => _store.Create("dup", "1.22.5"));
    }

    [Fact]
    public void Create_refuses_an_unusable_game_version()
    {
        Assert.Throws<InvalidDataException>(() => _store.Create("bad", ">=1.22.0"));
        Assert.False(_store.Exists("bad"));
    }

    [Fact]
    public void An_empty_connect_is_stored_as_null_not_empty_string()
    {
        var manifest = _store.Create("sp", "1.22.5", "Single", connect: "   ");
        Assert.Null(manifest.Connect);
        Assert.Null(_store.Load("sp").Connect);
    }

    [Fact]
    public void Delete_removes_the_pack_and_its_mods()
    {
        _store.Create("gone", "1.22.5");
        File.WriteAllText(Path.Combine(_store.ModsDir("gone"), "something_1.0.0.zip"), "x");

        _store.Delete("gone");

        Assert.False(_store.Exists("gone"));
        Assert.False(Directory.Exists(_store.PackDir("gone")));
    }

    [Fact]
    public void ListIds_skips_directories_without_a_manifest()
    {
        _store.Create("real", "1.22.5");
        Directory.CreateDirectory(Path.Combine(_root, "not-a-pack"));

        Assert.Equal(["real"], _store.ListIds());
    }

    [Fact]
    public void DescribeIdProblem_explains_each_rejection()
    {
        Assert.NotNull(_store.DescribeIdProblem(""));
        Assert.NotNull(_store.DescribeIdProblem("../x"));

        _store.Create("taken", "1.22.5");
        Assert.Contains("already exists", _store.DescribeIdProblem("taken")!);

        Assert.Null(_store.DescribeIdProblem("brand-new"));
    }

    /// <summary>A document as a server serves one, with the fields it stamps on.</summary>
    private static PackBundle Published(string id = "theirs") => PackBundle.Parse($$"""
        {
          "formatVersion": 1,
          "pack": { "id": "{{id}}", "gameVersion": "1.22.5",
                    "mods": [ { "modid": "glassview" } ] },
          "publishedBy": "someone-else",
          "canonicalUrl": "https://cairns.gg/someone-else/{{id}}",
          "revision": 4
        }
        """);

    [Fact]
    public void Importing_a_published_pack_records_that_it_belongs_to_someone_else()
    {
        // The document URL, as a front-end fetched it. The link is recorded against this
        // rather than against the canonicalUrl inside the document — see below.
        var manifest = _store.Import(
            Published(), sourceUrl: "https://cairns.gg/someone-else/theirs.json");

        var link = _store.LoadLink(manifest.Id);

        // Without this the pack is indistinguishable from one you made, and Share offers
        // to publish somebody else's curation under your name.
        Assert.NotNull(link);
        Assert.Equal(PackRole.Follower, link!.Role);
        Assert.True(link.Following);
        Assert.Equal("https://cairns.gg/someone-else/theirs", link.Url);
        Assert.Equal(4, link.Revision);

        Assert.False(_store.ShareStateFor(manifest.Id).IsOffered);
    }

    [Fact]
    public void Importing_a_file_somebody_exported_leaves_it_yours()
    {
        // No canonical URL, so there is no owner and nowhere to check back with. A bundle
        // handed over as a file is a starting point, not somebody's published pack.
        var bundle = PackBundle.Parse("""
            {"formatVersion":1,
             "pack":{"id":"handed-over","gameVersion":"1.22.5","mods":[{"modid":"glassview"}]}}
            """);

        var manifest = _store.Import(bundle);

        Assert.Null(_store.LoadLink(manifest.Id));
        Assert.True(_store.ShareStateFor(manifest.Id).IsOffered);
    }

    [Fact]
    public void A_document_does_not_get_to_say_where_it_came_from()
    {
        // The document claims cairns.gg; it was served from somewhere else entirely.
        // Believing the claim would let any web page hand over a pack that reads as
        // somebody's published curation and, worse, point every future update check at a
        // host of its choosing — recorded once at import and never questioned again.
        var manifest = _store.Import(
            Published(), sourceUrl: "https://evil.example/someone-else/theirs.json");

        var link = _store.LoadLink(manifest.Id);

        Assert.NotNull(link);
        Assert.Equal("https://evil.example/someone-else/theirs", link!.Url);
    }

    [Fact]
    public void A_published_document_out_of_a_file_forks_unless_somebody_says_otherwise()
    {
        // Same document, no fetch behind it and nobody asked. The only address on offer is
        // the file's own say-so, and taking that unasked is what would let a file decide
        // where this machine checks back. So it becomes a pack of your own.
        var manifest = _store.Import(Published());

        Assert.Null(_store.LoadLink(manifest.Id));
        Assert.True(_store.ShareStateFor(manifest.Id).IsOffered);
    }

    [Fact]
    public void Following_a_file_is_allowed_once_somebody_chooses_it()
    {
        // The claim is reachable — but only through a choice, which is why the front-ends
        // show the address beside it. A claim somebody approved is not a claim believed.
        var manifest = _store.Import(Published(), intent: ImportIntent.Follow);
        var link = _store.LoadLink(manifest.Id);

        Assert.NotNull(link);
        Assert.Equal(PackRole.Follower, link!.Role);
        Assert.Equal("https://cairns.gg/someone-else/theirs", link.Url);
    }

    [Fact]
    public void Forking_a_fetched_pack_leaves_nobody_to_check_back_with()
    {
        // The other direction, and the thing that was impossible before: taking somebody's
        // published pack as the start of your own. No link and no merge base, so it is
        // yours to publish — which take-over was specced for and never delivered.
        var manifest = _store.Import(
            Published(),
            sourceUrl: "https://cairns.gg/someone-else/theirs.json",
            intent: ImportIntent.Fork);

        Assert.Null(_store.LoadLink(manifest.Id));
        Assert.Null(_store.LoadUpstream(manifest.Id));
        Assert.True(_store.ShareStateFor(manifest.Id).IsOffered);
    }

    [Fact]
    public void An_imported_lock_says_which_mod_and_not_where_it_lives()
    {
        // The whole of the payload-substitution hole in one fixture: a reputable modid and
        // version beside a URL and filename for something else. Both are on a host ModDB
        // genuinely serves from, so a host allowlist cannot tell them apart — anyone may
        // upload a mod, so anyone may put a file there.
        var bundle = PackBundle.Parse("""
            {"formatVersion":1,
             "pack":{"id":"theirs","gameVersion":"1.22.5","mods":[{"modid":"carryon"}]},
             "lock":{"gameVersion":"1.22.5","mods":[
               {"modid":"carryon","version":"2.6.1","filename":"payload.zip",
                "url":"https://moddbcdn.vintagestory.at/attacker/payload.zip",
                "releaseId":99,"fileId":98,"sha256":""}]},
             "canonicalUrl":"https://cairns.gg/someone-else/theirs","revision":1}
            """);

        var manifest = _store.Import(bundle, sourceUrl: "https://cairns.gg/someone-else/theirs");
        var locked = _store.LoadLock(manifest.Id)!.Mods.Single();

        // What the author may claim survives, because that is what reproduces their pack.
        Assert.Equal("carryon", locked.ModId);
        Assert.Equal("2.6.1", locked.Version);

        // What only ModDB may claim does not, so the next sync resolves carryon 2.6.1 for
        // itself and downloads whatever ModDB says that is.
        Assert.Equal("", locked.Url);
        Assert.Equal("", locked.FileName);
        Assert.Equal(0, locked.ReleaseId);
        Assert.Equal(0, locked.FileId);
    }

    [Fact]
    public void A_pack_you_published_stays_yours_to_republish()
    {
        var manifest = _store.Create("mine", "1.22.5");

        _store.SaveLink(manifest.Id, new PackLink
        {
            Role = PackRole.Author,
            Url = "https://cairns.gg/me/mine",
            Revision = 1,
            Published = new PublishRecord { Fingerprint = "sha256:whatever" },
        });

        Assert.True(_store.ShareStateFor(manifest.Id).IsOffered);
    }
}
