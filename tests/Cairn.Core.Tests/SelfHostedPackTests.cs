using System.Net;
using System.Text;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// A pack its author hosts themselves — exported from Cairn and put on a static host —
/// followed from that address (cairns-gg/cairn-app#4).
///
/// It used to import with no link at all, because following needed cairns.gg's stamp, so
/// the only way to a newer version was to delete the pack and import it again. With no
/// revisions to compare, whether it has changed is asked of the document itself.
/// </summary>
public class SelfHostedPackTests : IDisposable
{
    private const string Address = "https://raw.githubusercontent.com/someone/seraph/main/pack.json";

    private readonly string _root = Directory.CreateTempSubdirectory("cairn-selfhosted-").FullName;
    private readonly PackStore _store;
    private readonly Served _served = new();

    public SelfHostedPackTests() => _store = new PackStore(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>Whatever the author last pushed, at one address.</summary>
    private sealed class Served : HttpMessageHandler
    {
        public string Text { get; set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(r.RequestUri!.ToString() == Address
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Text, Encoding.UTF8) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    /// <summary>An export, as Cairn writes one: no canonical URL, no revision.</summary>
    private static string Export(params string[] mods) => PackBundle.Serialize(new PackManifest
    {
        Id = "seraph",
        Name = "Seraph Horizons",
        GameVersion = "1.22.7",
        Mods = [.. mods.Select(m => new PackMod { ModId = m })],
    });

    private PackManifest ImportFromAddress()
    {
        var bundle = PackBundle.Parse(_served.Text);
        Assert.False(bundle.IsPublished);
        return _store.Import(bundle, sourceUrl: Address);
    }

    private Task<PackUpdateAvailable?> Check() =>
        PackUpdateCheck.CheckAsync(_store.LoadLink("seraph"), new HttpClient(_served));

    [Fact]
    public void Fetched_from_an_address_it_is_followed_from_that_address()
    {
        _served.Text = Export("carryon");
        ImportFromAddress();

        var link = _store.LoadLink("seraph");
        Assert.NotNull(link);
        Assert.Equal(PackRole.Follower, link.Role);
        Assert.True(link.Following);
        Assert.Equal(Address, PackUpdateCheck.DocumentUrl(link.Url));
        Assert.NotNull(link.ContentFingerprint);
        Assert.True(PackUpdateCheck.CanCheck(link));
    }

    /// <summary>
    /// A pack file named as Cairn names them, hosted as it is (cairns-gg/cairn-app#2). Asked
    /// for with .json on the end, it was not found, and the pack could never be checked.
    /// </summary>
    [Fact]
    public async Task A_cairn_file_at_an_address_is_followed_at_that_address()
    {
        const string cairnFile = "https://raw.githubusercontent.com/someone/seraph/main/seraph.cairn";

        var bundle = PackBundle.Parse(Export("carryon"));
        _store.Import(bundle, sourceUrl: cairnFile);

        var link = _store.LoadLink("seraph")!;
        Assert.Equal(cairnFile, PackUpdateCheck.DocumentUrl(link.Url));

        var http = new HttpClient(new Fixed(Export("carryon", "heavyweight")));
        Assert.NotNull(await PackUpdateCheck.FetchAsync(link, http));
    }

    [Fact]
    public void A_pack_file_is_named_cairn_and_the_old_name_still_imports()
    {
        Assert.Equal("seraph.cairn", PackBundle.FileNameFor("seraph"));

        // An export made before the rename: the name was never what import read.
        var old = Path.Combine(_root, "seraph.cairn.json");
        File.WriteAllText(old, Export("carryon"));

        Assert.Equal("seraph", _store.Import(PackBundle.Parse(File.ReadAllText(old))).Id);
    }

    [Fact]
    public void Out_of_a_file_it_has_no_address_and_is_not_followed()
    {
        // No fetch, no published origin: nothing that could be checked back with.
        var bundle = PackBundle.Parse(Export("carryon"));

        Assert.Null(PackStore.FollowAddress(bundle, fetchedFrom: null));

        _store.Import(bundle);
        Assert.Null(_store.LoadLink("seraph"));
    }

    [Fact]
    public async Task Unchanged_is_not_news()
    {
        _served.Text = Export("carryon");
        ImportFromAddress();

        Assert.Null(await Check());
    }

    [Fact]
    public async Task Reformatted_is_not_news()
    {
        // A host, or a hand, that only rewrites the layout has not changed the pack.
        _served.Text = Export("carryon");
        ImportFromAddress();

        _served.Text = _served.Text.Replace("\n", "\n  ").Replace("  \"formatVersion\"", "\"formatVersion\"");
        Assert.NotEqual(Export("carryon"), _served.Text);

        Assert.Null(await Check());
    }

    [Fact]
    public async Task A_change_the_author_pushed_is_news_until_it_is_taken()
    {
        _served.Text = Export("carryon");
        ImportFromAddress();

        _served.Text = Export("carryon", "heavyweight");
        var found = await Check();

        Assert.NotNull(found);
        Assert.True(found.Changed);
        Assert.Equal(Lang.Get("packupdate-check-changed"), found.Describe());

        var plan = PackUpdatePlan.Between(
            _store.Load("seraph"), found.Bundle.Pack!, _store.LoadUpstream("seraph"),
            0, 0, _store.LoadLocalState("seraph"));
        _store.ApplyUpdate("seraph", plan, found.Bundle);

        Assert.Contains(_store.Load("seraph").Mods, m => m.ModId == "heavyweight");
        Assert.Null(await Check());
    }

    [Fact]
    public async Task This_copys_own_edits_are_not_mistaken_for_the_authors()
    {
        // Compared with what was last taken, not with what this copy holds now.
        _served.Text = Export("carryon");
        ImportFromAddress();

        var mine = _store.Load("seraph");
        mine.Mods.Add(new PackMod { ModId = "mine" });
        _store.Save(mine);

        Assert.Null(await Check());
    }

    [Fact]
    public async Task A_pack_followed_from_cairns_gg_still_refuses_a_document_that_lost_its_url()
    {
        // Content-following is opted into by the link, so a published pack whose address
        // starts serving an unpublished document is still not this pack any more.
        var link = new PackLink
        {
            Role = PackRole.Follower, Following = true, Url = "https://cairns.gg/dizzyd/anego", Revision = 3,
        };

        var http = new HttpClient(new Fixed(Export("carryon")));

        Assert.Null(await PackUpdateCheck.FetchAsync(link, http));
    }

    private sealed class Fixed(string text) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) });
    }
}
