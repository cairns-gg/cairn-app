using System.Net;
using System.Text;
using Cairn.Core.ModDb;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// What ModDB says about a mod is remembered for a few minutes, because the launcher asks
/// the same questions of the same endpoint several times over in the course of one journey:
/// a version-change preview resolves every mod, publishing sweeps them for existence, and
/// the sync in between resolves them all again. A seventy-mod pack spent close to three
/// hundred requests on that, against an API that publishes no rate limit and whose
/// bandwidth somebody else pays for.
///
/// The rule that keeps it honest is that a resolve looking for the *newest* release never
/// reads it — see the fresh parameter.
/// </summary>
public class ModDbDocumentCacheTests
{
    /// <param name="found">
    /// Whether the mod is published. ModDB answers "no such mod" with HTTP 200 and a body
    /// carrying no <c>mod</c> — a transport 404 is a different thing, and reaches callers
    /// as an exception rather than as an absence.
    /// </param>
    private sealed class Stub(bool found = true) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        /// <summary>
        /// The releases ModDB is currently serving. A test moves this to stand for an
        /// author publishing while Cairn is holding a document.
        /// </summary>
        public List<string> Versions { get; } = ["1.0.0"];

        private string Body()
        {
            var releases = string.Join(",", Versions.Select((v, i) => Release(v, i + 1)));

            return "{\"statuscode\":\"200\",\"mod\":{\"modid\":5784,\"assetid\":34157,"
                   + "\"name\":\"Olla\",\"urlalias\":\"olla\",\"side\":\"client\","
                   + "\"releases\":[" + releases + "]}}";
        }

        private static string Release(string version, int id) =>
            $"{{\"releaseid\":{id},\"fileid\":{id},\"modidstr\":\"olla\","
            + $"\"modversion\":\"{version}\",\"filename\":\"olla_{version}.zip\","
            + $"\"mainfile\":\"https://moddbcdn.vintagestory.at/olla_{version}.zip\","
            + "\"tags\":[\"1.22.5\"]}";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    found ? Body() : """{"statuscode":"404"}""",
                    Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>A clock the test moves by hand, so nothing has to sleep for ten minutes.</summary>
    private sealed class Clock
    {
        public DateTimeOffset Now = new(2026, 8, 22, 18, 26, 0, TimeSpan.Zero);
        public Func<DateTimeOffset> Read => () => Now;
    }

    [Fact]
    public async Task Asking_twice_asks_ModDB_once()
    {
        var handler = new Stub();
        var moddb = new ModDbClient(new HttpClient(handler));

        Assert.True(await moddb.ExistsAsync("olla"));
        Assert.True(await moddb.ExistsAsync("olla"));
        Assert.True(await moddb.ExistsAsync("olla"));

        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task The_id_is_matched_however_it_is_cased()
    {
        var handler = new Stub();
        var moddb = new ModDbClient(new HttpClient(handler));

        Assert.True(await moddb.ExistsAsync("olla"));
        Assert.True(await moddb.ExistsAsync("Olla"));

        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task An_answer_stops_standing_once_it_is_old()
    {
        var clock = new Clock();
        var handler = new Stub();
        var moddb = new ModDbClient(new HttpClient(handler), clock.Read);

        Assert.True(await moddb.ExistsAsync("olla"));

        clock.Now += ModDbClient.DocumentLifetime - TimeSpan.FromSeconds(1);
        Assert.True(await moddb.ExistsAsync("olla"));
        Assert.Equal(1, handler.Requests);

        clock.Now += TimeSpan.FromSeconds(2);
        Assert.True(await moddb.ExistsAsync("olla"));
        Assert.Equal(2, handler.Requests);
    }

    /// <summary>
    /// A laptop resumed or a clock corrected backwards would otherwise hold an answer open
    /// for as long as the skew lasts, because the arithmetic says no time has passed.
    /// </summary>
    [Fact]
    public async Task A_clock_that_goes_backwards_does_not_freeze_an_answer()
    {
        var clock = new Clock();
        var handler = new Stub();
        var moddb = new ModDbClient(new HttpClient(handler), clock.Read);

        Assert.True(await moddb.ExistsAsync("olla"));

        clock.Now -= TimeSpan.FromHours(3);

        Assert.True(await moddb.ExistsAsync("olla"));
        Assert.Equal(2, handler.Requests);
    }

    /// <summary>
    /// The saving that matters. Publishing syncs when the lock does not cover the manifest,
    /// and the sync resolves every mod it touches — which is the same request the existence
    /// sweep afterwards would make.
    /// </summary>
    [Fact]
    public async Task Fetching_a_mod_answers_the_existence_question_for_free()
    {
        var handler = new Stub();
        var moddb = new ModDbClient(new HttpClient(handler));

        await moddb.GetModAsync("olla");
        Assert.Equal(1, handler.Requests);

        Assert.True(await moddb.ExistsAsync("olla"));
        Assert.Equal(1, handler.Requests);
    }

    /// <summary>
    /// Absence is the answer that goes stale in the direction that hurts: an author
    /// publishes the mod their pack names, and would be told for another ten minutes that
    /// recipients cannot install it.
    /// </summary>
    [Fact]
    public async Task A_mod_that_was_not_found_is_asked_about_again()
    {
        var handler = new Stub(found: false);
        var moddb = new ModDbClient(new HttpClient(handler));

        Assert.False(await moddb.ExistsAsync("nosuchmod"));
        Assert.False(await moddb.ExistsAsync("nosuchmod"));

        Assert.Equal(2, handler.Requests);
    }

    /// <summary>
    /// The pair the document cache exists for. GameVersionChange.PreviewAsync resolves
    /// every mod against the target and promises to be the same call the sync will make;
    /// they now make it from the same document instead of fetching it twice.
    /// </summary>
    [Fact]
    public async Task A_preview_and_the_sync_it_predicts_share_one_document()
    {
        var handler = new Stub();
        var moddb = new ModDbClient(new HttpClient(handler));

        var previewed = await moddb.ResolveAsync("olla", "1.22.5");
        var installed = await moddb.ResolveAsync("olla", "1.22.5");

        Assert.Equal(1, handler.Requests);
        Assert.Equal(previewed!.ModVersion, installed!.ModVersion);
    }

    /// <summary>
    /// And they agree even when the answer moves underneath them, which is the part a
    /// saved request does not buy on its own: an author publishing between the preview and
    /// the sync used to make the sync install something the preview never showed.
    /// </summary>
    [Fact]
    public async Task A_release_published_in_between_does_not_change_what_the_sync_installs()
    {
        var handler = new Stub();
        var moddb = new ModDbClient(new HttpClient(handler));

        var previewed = await moddb.ResolveAsync("olla", "1.22.5");
        Assert.Equal("1.0.0", previewed!.ModVersion);

        handler.Versions.Add("2.0.0");

        var installed = await moddb.ResolveAsync("olla", "1.22.5");
        Assert.Equal("1.0.0", installed!.ModVersion);
    }

    /// <summary>
    /// The rule that keeps the whole thing honest. Somebody pressing Update asked for the
    /// newest; handing them the release that was newest ten minutes ago would be written
    /// into the lock and then reported back as up to date.
    /// </summary>
    [Fact]
    public async Task Asking_for_the_newest_never_reads_a_remembered_document()
    {
        var handler = new Stub();
        var moddb = new ModDbClient(new HttpClient(handler));

        Assert.Equal("1.0.0", (await moddb.ResolveAsync("olla", "1.22.5"))!.ModVersion);

        handler.Versions.Add("2.0.0");

        var updated = await moddb.ResolveAsync("olla", "1.22.5", fresh: true);

        Assert.Equal("2.0.0", updated!.ModVersion);
        Assert.Equal(2, handler.Requests);
    }

    /// <summary>
    /// A pin naming a release that landed while Cairn was holding the document would
    /// otherwise be refused as a release that does not exist — wrong, and nothing the
    /// person who wrote the pin could act on.
    /// </summary>
    [Fact]
    public async Task A_pin_a_remembered_document_cannot_meet_is_asked_about_again()
    {
        var handler = new Stub();
        var moddb = new ModDbClient(new HttpClient(handler));

        await moddb.ResolveAsync("olla", "1.22.5");
        Assert.Equal(1, handler.Requests);

        handler.Versions.Add("2.0.0");

        var pinned = await moddb.ResolveAsync("olla", "1.22.5", "2.0.0");

        Assert.Equal("2.0.0", pinned!.ModVersion);
        Assert.Equal(2, handler.Requests);
    }

    /// <summary>
    /// But a pin that is genuinely not there still costs one request rather than two: the
    /// retry is for a document that might be behind, not for every refusal.
    /// </summary>
    [Fact]
    public async Task A_pin_that_does_not_exist_is_refused_without_a_second_request()
    {
        var handler = new Stub();
        var moddb = new ModDbClient(new HttpClient(handler));

        await Assert.ThrowsAsync<ModDbException>(
            () => moddb.ResolveAsync("olla", "1.22.5", "9.9.9"));

        Assert.Equal(1, handler.Requests);
    }
}
