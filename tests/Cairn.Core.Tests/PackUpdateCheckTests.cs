using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// How often a followed pack's author gets asked.
///
/// The cost of getting this wrong is paid by somebody else's server. Before the interval,
/// selecting a pack asked — so clicking between two followed packs asked on every click,
/// and reopening the launcher asked again, for ever.
/// </summary>
public class PackUpdateCheckTests
{
    private static PackLink Following(string url = "https://cairns.gg/dizzyd/anego") => new()
    {
        Role = PackRole.Follower,
        Following = true,
        Url = url,
        Revision = 1,
    };

    [Fact]
    public void A_pack_that_follows_nobody_is_never_asked_about()
    {
        // Most packs are your own. They have no author to ask and must cost no request.
        Assert.False(PackUpdateCheck.CanCheck(null));

        Assert.False(PackUpdateCheck.CanCheck(new PackLink
        {
            Role = PackRole.Author, Url = "https://cairns.gg/dizzyd/anego",
        }));

        // Taken over: the URL is still there, the following is not.
        Assert.False(PackUpdateCheck.CanCheck(new PackLink
        {
            Role = PackRole.Follower, Following = false, Url = "https://cairns.gg/dizzyd/anego",
        }));
    }

    [Fact]
    public void A_pack_fetched_over_plain_http_is_not_asked_about()
    {
        // The same rule import applies: a pack decides which mods get installed, so it
        // must not arrive over a connection anyone on the path can rewrite.
        Assert.False(PackUpdateCheck.CanCheck(Following("http://packs.example.com/anego")));

        // Loopback has no such path, which is what lets a server on this machine be used.
        Assert.True(PackUpdateCheck.CanCheck(Following("http://127.0.0.1:8811/pack.json")));
    }

    /// <summary>
    /// A pack's canonical URL is where a person reads about it, and that address serves
    /// HTML. Fetching it directly got a web page every time, failed to parse, and was
    /// reported as the author being unreachable — for every pack, however healthy the
    /// server. The bundle is the same address with .json on the end.
    /// </summary>
    [Theory]
    [InlineData("https://cairns.gg/dizzyd/anego", "https://cairns.gg/dizzyd/anego.json")]
    [InlineData("https://cairns.gg/dizzyd/anego/", "https://cairns.gg/dizzyd/anego.json")]
    public void The_document_is_the_page_address_with_json_on_it(string url, string expected) =>
        Assert.Equal(expected, PackUpdateCheck.DocumentUrl(url));

    [Theory]
    [InlineData("http://127.0.0.1:8811/pack.json")]
    [InlineData("https://example.com/packs/anego.JSON")]
    public void A_url_that_is_already_a_document_is_left_alone(string url) =>
        // A file on a static host or a dev server on loopback serves the document itself,
        // and appending again would ask for pack.json.json.
        Assert.Equal(url.TrimEnd('/'), PackUpdateCheck.DocumentUrl(url));

    [Fact]
    public void A_pack_never_asked_about_is_due_immediately()
    {
        Assert.True(PackUpdateCheck.IsDue(null));
        Assert.True(PackUpdateCheck.IsDue(new PackLocalState()));
    }

    [Fact]
    public void Asking_again_waits_out_the_interval()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new PackLocalState();
        state.RecordCheck(now);

        // Re-selecting the pack, twice, a minute apart: the thing that used to be a
        // request per click.
        Assert.False(PackUpdateCheck.IsDue(state, now));
        Assert.False(PackUpdateCheck.IsDue(state, now.AddMinutes(1)));
        Assert.False(PackUpdateCheck.IsDue(state, now.Add(PackUpdateCheck.CheckInterval).AddMinutes(-1)));

        Assert.True(PackUpdateCheck.IsDue(state, now.Add(PackUpdateCheck.CheckInterval)));
    }

    [Fact]
    public void The_interval_survives_the_launcher_being_closed()
    {
        // On disk rather than in memory, because reopening five times in an afternoon
        // asked five more times.
        var dir = Path.Combine(Path.GetTempPath(), "cairn-check-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);

        try
        {
            var path = Path.Combine(dir, "local.json");
            var now = DateTimeOffset.UtcNow;

            var state = new PackLocalState();
            state.RecordCheck(now);
            state.Save(path);

            Assert.False(PackUpdateCheck.IsDue(PackLocalState.Load(path), now.AddMinutes(5)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Being on the author's latest revision is not the same as matching it.
    ///
    /// A copy that has been edited has diverged whether or not anybody published since, and
    /// the way back to it must not depend on an author happening to release — which is what
    /// gating the whole reconcile behind "is there an update" quietly did.
    /// </summary>
    [Fact]
    public async Task The_authors_pack_can_be_fetched_when_there_is_no_update()
    {
        var bundle = new PackBundle
        {
            Pack = new PackManifest { Id = "anego", GameVersion = "1.22.5" },
            CanonicalUrl = "https://cairns.gg/dizzyd/anego",
            Revision = 3,
        };

        var http = new HttpClient(new Serves(bundle));
        var link = Following();
        link.Revision = 3;      // already on the latest

        // The check says no, correctly: nothing newer has been published.
        Assert.Null(await PackUpdateCheck.CheckAsync(link, http));

        // The fetch says yes, also correctly: their pack is right there, and comparing a
        // diverged copy against it is a different question from "is there an update".
        var fetched = await PackUpdateCheck.FetchAsync(link, http);
        Assert.NotNull(fetched);
        Assert.Equal(3, fetched.Revision);
    }

    /// <summary>Serves one bundle, whatever is asked for.</summary>
    private sealed class Serves(PackBundle bundle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage r, CancellationToken ct)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(bundle);

            return Task.FromResult(new System.Net.Http.HttpResponseMessage(
                System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public void Recording_a_check_leaves_the_declines_alone()
    {
        // One file, two unrelated things in it. A check must not disturb an answer somebody
        // gave, and an answer must not reset the interval.
        var state = new PackLocalState();
        state.Decline("heavyweight");
        state.RecordCheck(DateTimeOffset.UtcNow);

        Assert.True(state.HasDeclined("heavyweight"));
        Assert.NotNull(state.LastChecked);
    }
}
