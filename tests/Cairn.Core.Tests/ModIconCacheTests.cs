using System.Net;
using Cairn.Core.ModDb;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Icons are decoration, so the cache's job is as much about never getting in the way as
/// about saving downloads: a CDN hiccup must leave searching working.
/// </summary>
public class ModIconCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-icons-" + Guid.NewGuid().ToString("n")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>Serves canned responses and counts what was actually requested.</summary>
    private sealed class Stub(HttpStatusCode status, byte[] body) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            Urls.Add(request.RequestUri!.ToString());

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(body),
            });
        }
    }

    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4];

    private (ModIconCache Cache, Stub Handler) Make(
        HttpStatusCode status = HttpStatusCode.OK, byte[]? body = null)
    {
        var handler = new Stub(status, body ?? Png);
        return (new ModIconCache(new HttpClient(handler), _root), handler);
    }

    private const string Url = "https://moddbcdn.vintagestory.at/olla_9b063fc6.png";

    [Fact]
    public async Task An_icon_is_downloaded_once_and_then_read_from_disk()
    {
        var (cache, handler) = Make();

        var first = await cache.GetAsync(Url);
        var second = await cache.GetAsync(Url);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(Png, await File.ReadAllBytesAsync(first!));

        // The whole point: a repeated search must not re-fetch what is already here.
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task A_cached_icon_is_served_without_any_network_at_all()
    {
        var (warm, _) = Make();
        await warm.GetAsync(Url);

        // A handler that would throw if touched, standing in for being offline.
        var offline = new ModIconCache(new HttpClient(new Stub(HttpStatusCode.InternalServerError, [])), _root);

        Assert.True(offline.IsCached(Url));
        Assert.NotNull(await offline.GetAsync(Url));
    }

    [Fact]
    public async Task Different_urls_do_not_collide()
    {
        var (cache, _) = Make();

        var a = await cache.GetAsync("https://cdn.example/one.png");
        var b = await cache.GetAsync("https://cdn.example/two.png");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void The_path_is_stable_across_runs()
    {
        // Otherwise every launch would start with an empty cache.
        var (one, _) = Make();
        var (two, _) = Make();

        Assert.Equal(one.PathFor(Url), two.PathFor(Url));
    }

    [Fact]
    public void A_hostile_url_cannot_write_outside_the_cache()
    {
        // These URLs arrive from a remote API, so the filename is hashed rather than taken
        // from the path.
        var (cache, _) = Make();
        var path = cache.PathFor("https://cdn.example/../../../../etc/passwd");

        Assert.Equal(Path.GetFullPath(_root), Path.GetDirectoryName(Path.GetFullPath(path)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task No_url_is_not_an_error(string? url)
    {
        var (cache, handler) = Make();

        Assert.Null(await cache.GetAsync(url));
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task A_failed_fetch_yields_no_icon_rather_than_throwing()
    {
        var (cache, _) = Make(HttpStatusCode.NotFound);

        Assert.Null(await cache.GetAsync(Url));

        // And nothing is left behind to be served as if it were real next time.
        Assert.False(cache.IsCached(Url));
    }

    [Fact]
    public async Task An_unreachable_host_yields_no_icon()
    {
        var cache = new ModIconCache(new HttpClient(new ThrowingHandler()), _root);
        Assert.Null(await cache.GetAsync(Url));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => throw new HttpRequestException("no route to host");
    }

    [Fact]
    public async Task Something_far_too_large_to_be_an_icon_is_refused()
    {
        var (cache, _) = Make(body: new byte[ModIconCache.MaxBytes + 1]);

        Assert.Null(await cache.GetAsync(Url));
        Assert.False(cache.IsCached(Url));
    }

    [Fact]
    public async Task An_empty_response_is_refused()
    {
        var (cache, _) = Make(body: []);
        Assert.Null(await cache.GetAsync(Url));
    }

    [Fact]
    public async Task The_cache_reports_its_size_and_can_be_emptied()
    {
        var (cache, _) = Make();
        await cache.GetAsync(Url);

        Assert.Equal(Png.Length, cache.Size());

        cache.Clear();
        Assert.Equal(0, cache.Size());
        Assert.False(cache.IsCached(Url));
    }

    [Fact]
    public void Clearing_a_cache_that_was_never_used_is_harmless()
    {
        var (cache, _) = Make();
        cache.Clear();
        Assert.Equal(0, cache.Size());
    }
}
