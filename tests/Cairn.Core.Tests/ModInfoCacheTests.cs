using System.Net;
using System.Text;
using Cairn.Core.ModDb;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// A pack's manifest holds only mod ids, so drawing its rows with icons means asking
/// ModDB what each mod looks like. Kept on disk because that answer barely changes and
/// the alternative is one API call per mod on every launch.
/// </summary>
public class ModInfoCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-modinfo-" + Guid.NewGuid().ToString("n")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class Stub(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        private const string Body = """
        {"statuscode":"200","mod":{
          "modid":5784,"assetid":34157,"name":"Olla","urlalias":"olla",
          "logofile":"https://moddbcdn.vintagestory.at/olla_9b063fc6.png",
          "releases":[]
        }}
        """;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    status == HttpStatusCode.OK ? Body : """{"statuscode":"404"}""",
                    Encoding.UTF8, "application/json"),
            });
        }
    }

    private (ModInfoCache Cache, Stub Handler) Make(HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new Stub(status);
        return (new ModInfoCache(new ModDbClient(new HttpClient(handler)), _root), handler);
    }

    [Fact]
    public async Task It_reads_what_a_row_needs_to_draw_itself()
    {
        var (cache, _) = Make();

        var info = await cache.GetAsync("olla");

        Assert.NotNull(info);
        Assert.Equal("https://moddbcdn.vintagestory.at/olla_9b063fc6.png", info!.Logo);
        Assert.Equal(34157, info.AssetId);
        Assert.Equal("olla", info.UrlAlias);
    }

    [Fact]
    public async Task A_mod_is_looked_up_once_however_often_it_is_asked_for()
    {
        var (cache, handler) = Make();

        await cache.GetAsync("olla");
        await cache.GetAsync("olla");
        await cache.GetAsync("OLLA");   // mod ids are not case sensitive

        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task What_it_learned_survives_a_restart()
    {
        var (first, _) = Make();
        await first.GetAsync("olla");

        // A fresh cache over the same directory, as the next launch would build.
        var (second, handler) = Make();
        var info = await second.GetAsync("olla");

        Assert.NotNull(info);
        Assert.Equal(34157, info!.AssetId);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task A_mod_that_cannot_be_found_is_not_remembered_as_missing()
    {
        var (cache, handler) = Make(HttpStatusCode.NotFound);

        Assert.Null(await cache.GetAsync("nosuchmod"));

        // Caching the absence would make a temporary failure permanent.
        Assert.Null(await cache.GetAsync("nosuchmod"));
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task Peek_answers_only_from_what_is_already_known()
    {
        var (cache, handler) = Make();

        Assert.Null(cache.Peek("olla"));

        await cache.GetAsync("olla");
        Assert.NotNull(cache.Peek("olla"));
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_id_asks_nothing(string modId)
    {
        var (cache, handler) = Make();

        Assert.Null(await cache.GetAsync(modId));
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task A_corrupt_cache_file_is_started_over_rather_than_fatal()
    {
        Directory.CreateDirectory(_root);
        var (cache, _) = Make();
        await File.WriteAllTextAsync(cache.Path, "{ this is not json");

        // Reading it must not throw, and the lookup must still work.
        Assert.Null(cache.Peek("olla"));
        Assert.NotNull(await cache.GetAsync("olla"));
    }

    [Fact]
    public async Task Clearing_forgets_everything()
    {
        var (cache, handler) = Make();
        await cache.GetAsync("olla");

        cache.Clear();

        Assert.Null(cache.Peek("olla"));
        Assert.False(File.Exists(cache.Path));

        await cache.GetAsync("olla");
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task Concurrent_lookups_do_not_corrupt_the_file()
    {
        var (cache, _) = Make();

        // Rows load in parallel, so several first-time lookups overlap by design.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => cache.GetAsync($"mod{i}")));

        var (fresh, handler) = Make();
        foreach (var i in Enumerable.Range(0, 8)) Assert.NotNull(await fresh.GetAsync($"mod{i}"));

        Assert.Equal(0, handler.Requests);
    }
}
