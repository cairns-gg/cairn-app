using System.Net;
using System.Text;
using Cairn.Core.Cairns;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// The client half of publishing. What matters here is not that HTTP works, but that a
/// refusal arrives as something worth reading and that polling stops for the right reasons.
/// </summary>
public class CairnsClientTests
{
    private sealed class Stub(Func<HttpRequestMessage, int, HttpResponseMessage> reply)
        : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null)
                Bodies.Add(await request.Content.ReadAsStringAsync(ct));

            return reply(request, ++Calls);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (CairnsClient Client, Stub Handler) Make(
        Func<HttpRequestMessage, int, HttpResponseMessage> reply)
    {
        var handler = new Stub(reply);
        return (new CairnsClient(new HttpClient(handler), "https://cairns.test"), handler);
    }

    private static readonly CairnsSession Session = new()
    {
        Server = "https://cairns.test", Token = "tok", Username = "dizzyd",
    };

    [Fact]
    public async Task Publishing_sends_the_document_unchanged()
    {
        var (client, handler) = Make((_, _) =>
            Json(HttpStatusCode.OK,
                """{"url":"https://cairns.test/dizzyd/anego","revision":1,"visibility":"unlisted"}"""));

        var document = """{"formatVersion":1,"pack":{"id":"anego"}}""";
        var result = await client.PublishAsync(Session, document, "anego", @public: false);

        // Byte for byte: this is what the share window showed and fingerprinted, and a
        // document rebuilt on the way out could differ from the one somebody agreed to.
        Assert.Equal(document, handler.Bodies.Single());
        Assert.Equal(1, result.Revision);
    }

    [Fact]
    public async Task A_refusal_arrives_with_the_reasons_the_server_gave()
    {
        var (client, _) = Make((_, _) =>
            Json(HttpStatusCode.BadRequest,
                """{"problems":["'unchisel' is in the pack but not in the lockfile."]}"""));

        var error = await Assert.ThrowsAsync<CairnsException>(
            () => client.PublishAsync(Session, "{}", "anego", false));

        // "the server said 400" is not actionable; the problems are, and they are usually
        // the whole answer.
        Assert.Contains("unchisel", error.Message);
    }

    [Fact]
    public async Task An_expired_token_says_so_rather_than_reporting_a_status_code()
    {
        var (client, _) = Make((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var error = await Assert.ThrowsAsync<CairnsException>(
            () => client.PublishAsync(Session, "{}", "anego", false));

        Assert.Contains("Not signed in", error.Message);
    }

    [Fact]
    public async Task Polling_waits_through_pending_and_stops_on_a_grant()
    {
        var (client, handler) = Make((request, call) =>
            request.RequestUri!.AbsolutePath.EndsWith("/token") && call < 2
                ? Json(HttpStatusCode.PreconditionRequired, """{"error":"authorization_pending"}""")
                : Json(HttpStatusCode.OK, """{"token":"granted","username":"dizzyd"}"""));

        var flow = new DeviceFlow("dev", "ACDE-FGHJ", "https://cairns.test/link", ExpiresIn: 30, Interval: 1);
        var session = await client.AwaitSignInAsync(flow);

        Assert.Equal("granted", session.Token);

        // 428 means keep waiting: one of those, then the grant, then /api/me.
        Assert.True(handler.Calls >= 3);
    }

    [Fact]
    public async Task Polling_gives_up_when_the_code_expires()
    {
        var (client, _) = Make((_, _) =>
            Json(HttpStatusCode.PreconditionRequired, """{"error":"authorization_pending"}"""));

        var flow = new DeviceFlow("dev", "ACDE-FGHJ", "https://cairns.test/link", ExpiresIn: 1, Interval: 1);

        var error = await Assert.ThrowsAsync<CairnsException>(() => client.AwaitSignInAsync(flow));
        Assert.Contains("expired", error.Message);
    }

    [Fact]
    public async Task A_tombstone_is_recognised_as_a_withdrawal()
    {
        var (client, handler) = Make((_, _) =>
            Json(HttpStatusCode.Gone, """{"withdrawn":true}"""));

        Assert.True(await client.IsWithdrawnAsync("dizzyd", "anego"));

        // Anonymous: the tombstone is public, and this is asked before anything is sent.
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task A_pack_still_being_served_is_not_a_withdrawal()
    {
        var (client, _) = Make((_, _) =>
            Json(HttpStatusCode.OK, """{"username":"dizzyd","slug":"anego","revision":3}"""));

        Assert.False(await client.IsWithdrawnAsync("dizzyd", "anego"));
    }

    [Fact]
    public async Task A_pack_that_never_existed_is_not_a_withdrawal()
    {
        // 404 and 410 are different answers on purpose — one is "no such pack", the other
        // "this pack, taken down" — and only the second one unblocks a republish.
        var (client, _) = Make((_, _) => Json(HttpStatusCode.NotFound, "{}"));

        Assert.False(await client.IsWithdrawnAsync("dizzyd", "anego"));
    }

    [Fact]
    public async Task An_unreachable_server_is_not_read_as_a_withdrawal()
    {
        var (client, _) = Make((_, _) => throw new HttpRequestException("no route to host"));

        // Not knowing is not knowing. Inventing a withdrawal would clear the local publish
        // record on nothing better than a flaky connection.
        Assert.False(await client.IsWithdrawnAsync("dizzyd", "anego"));
    }

    [Fact]
    public void The_server_can_be_pointed_somewhere_else_for_testing()
    {
        // Which is the only way to exercise any of this before the real one exists.
        Assert.Equal("https://cairns.test",
            new CairnsClient(new HttpClient(), "https://cairns.test/").Server);

        Assert.Equal(CairnsClient.DefaultServer, new CairnsClient(new HttpClient()).Server);
    }

    // ---- history ----

    [Fact]
    public async Task A_packs_history_is_asked_for_by_its_own_address_and_signed()
    {
        HttpRequestMessage? sent = null;
        var (client, _) = Make((request, _) =>
        {
            sent = request;
            return Json(HttpStatusCode.OK, """
                {"current":11,"archived":true,"revisions":[
                  {"revision":11,"publishedAt":"2026-10-17T14:02:00.0000000+00:00"},
                  {"revision":9,"publishedAt":"2026-10-01T09:00:00.0000000+00:00"}]}
                """);
        });

        // The canonical URL names a host the session is not signed in to. The path is what
        // identifies the pack; the server asked is the one the session belongs to.
        var history = await client.ListRevisionsAsync(Session, "https://cairns.gg/dizzyd/anego");

        Assert.Equal("https://cairns.test/api/packs/dizzyd/anego/revisions", sent!.RequestUri!.ToString());
        Assert.Equal("tok", sent.Headers.Authorization!.Parameter);

        Assert.True(history.Archived);
        Assert.Equal(11, history.Current);

        // A gap is the archive's to have, and is passed on as it came.
        Assert.Equal([11, 9], history.Revisions.Select(r => r.Revision));
    }

    [Fact]
    public async Task Somebody_elses_history_is_refused_by_name()
    {
        var (client, _) = Make((_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var error = await Assert.ThrowsAsync<CairnsException>(
            () => client.ListRevisionsAsync(Session, "https://cairns.gg/someone/anego"));

        Assert.Contains("dizzyd", error.Message);
        Assert.Contains("someone/anego", error.Message);
    }

    [Fact]
    public async Task A_revision_the_site_did_not_keep_says_so_rather_than_not_found()
    {
        var (client, _) = Make((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));

        var error = await Assert.ThrowsAsync<CairnsException>(
            () => client.FetchRevisionAsync(Session, "https://cairns.gg/dizzyd/anego", 4));

        Assert.Contains("did not keep revision 4", error.Message);
    }

    [Fact]
    public async Task An_unreachable_archive_arrives_with_the_servers_reason()
    {
        var (client, _) = Make((_, _) => Json(HttpStatusCode.ServiceUnavailable,
            """{"problems":["Pack history could not be read from storage. Try again later, and get in touch if it keeps happening."]}"""));

        var error = await Assert.ThrowsAsync<CairnsException>(
            () => client.ListRevisionsAsync(Session, "https://cairns.gg/dizzyd/anego"));

        Assert.Contains("could not be read from storage", error.Message);
    }

    [Fact]
    public async Task A_fetched_revision_is_the_document_the_site_stored()
    {
        HttpRequestMessage? sent = null;
        var (client, _) = Make((request, _) =>
        {
            sent = request;
            return Json(HttpStatusCode.OK, """
                {"formatVersion":1,"pack":{"id":"anego","gameVersion":"1.22.5","mods":[]},
                 "canonicalUrl":"https://cairns.gg/dizzyd/anego","revision":5}
                """);
        });

        var bundle = await client.FetchRevisionAsync(Session, "https://cairns.gg/dizzyd/anego", 5);

        Assert.Equal("https://cairns.test/api/packs/dizzyd/anego/revisions/5", sent!.RequestUri!.ToString());
        Assert.Equal(5, bundle.Revision);
        Assert.Equal("https://cairns.gg/dizzyd/anego", bundle.CanonicalUrl);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("https://cairns.gg/")]
    [InlineData("https://cairns.gg/dizzyd")]
    [InlineData("https://cairns.gg/dizzyd/anego/extra")]
    public async Task Only_a_packs_own_address_is_asked_about(string url)
    {
        var (client, handler) = Make((_, _) => new HttpResponseMessage(HttpStatusCode.OK));

        await Assert.ThrowsAsync<CairnsException>(() => client.ListRevisionsAsync(Session, url));
        Assert.Equal(0, handler.Calls);
    }
}
