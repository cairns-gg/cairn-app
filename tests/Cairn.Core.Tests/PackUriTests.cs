using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// The cairn:// links on a pack page.
///
/// Any web page anywhere can contain one of these, so the parser is the boundary: what it
/// accepts, the launcher will go and fetch. The tests that matter here are the refusals.
/// </summary>
public class PackUriTests
{
    [Theory]
    [InlineData("cairn://cairns.gg/dizzyd/anego", "https://cairns.gg/dizzyd/anego.json")]
    [InlineData("CAIRN://cairns.gg/dizzyd/anego", "https://cairns.gg/dizzyd/anego.json")]
    [InlineData("cairn://cairns.gg/dizzyd-8e79bl/my_pack.2", "https://cairns.gg/dizzyd-8e79bl/my_pack.2.json")]
    public void A_link_becomes_the_document_url(string link, string expected)
    {
        Assert.True(PackUri.TryGetDocumentUrl(link, out var url));
        Assert.Equal(expected, url);
    }

    [Theory]
    [InlineData("cairn://localhost:5080/dizzyd/anego", "http://localhost:5080/dizzyd/anego.json")]
    [InlineData("cairn://127.0.0.1:5080/dizzyd/anego", "http://127.0.0.1:5080/dizzyd/anego.json")]
    public void Loopback_resolves_over_http_so_a_local_server_can_be_tested(string link, string expected)
    {
        Assert.True(PackUri.TryGetDocumentUrl(link, out var url));
        Assert.Equal(expected, url);
    }

    [Fact]
    public void A_port_elsewhere_is_kept_but_stays_https()
    {
        Assert.True(PackUri.TryGetDocumentUrl("cairn://cairns.gg:8443/dizzyd/anego", out var url));
        Assert.Equal("https://cairns.gg:8443/dizzyd/anego.json", url);
    }

    [Theory]
    // Not our scheme, and http:// in particular must not be followed just because it parses.
    [InlineData("https://cairns.gg/dizzyd/anego")]
    [InlineData("http://cairns.gg/dizzyd/anego")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    // No host to fetch from.
    [InlineData("cairn:///dizzyd/anego")]
    [InlineData("cairn://")]
    // Not a pack address. Guessing what these meant is how a parser grows holes.
    [InlineData("cairn://cairns.gg/dizzyd")]
    [InlineData("cairn://cairns.gg/dizzyd/anego/extra")]
    [InlineData("cairn://cairns.gg/")]
    [InlineData("cairn://cairns.gg/dizzyd/anego?x=1")]
    // Traversal that eats a segment, leaving nothing that names a pack.
    [InlineData("cairn://cairns.gg/dizzyd/..")]
    [InlineData("not a url at all")]
    [InlineData("")]
    public void Anything_else_is_refused(string link) =>
        Assert.False(PackUri.TryGetDocumentUrl(link, out _));

    [Fact]
    public void Traversal_is_normalised_away_before_we_see_it_and_cannot_leave_the_host()
    {
        // Uri collapses the "..", so this arrives as two ordinary segments. Worth pinning
        // rather than asserting a refusal that does not happen: the reason it is harmless
        // is not that the string was rejected, it is that the result is still a path on
        // the host the link named. There is no ".." left to escape with.
        Assert.True(PackUri.TryGetDocumentUrl("cairn://cairns.gg/../etc/passwd", out var url));
        Assert.Equal("https://cairns.gg/etc/passwd.json", url);
    }

    [Fact]
    public void The_document_url_is_one_the_import_rule_would_also_accept()
    {
        // The two guards have to agree. A link that resolves to something import then
        // refuses is a dead button, and one that resolves past import is a hole.
        foreach (var link in new[]
                 {
                     "cairn://cairns.gg/dizzyd/anego",
                     "cairn://localhost:5080/dizzyd/anego",
                 })
        {
            Assert.True(PackUri.TryGetDocumentUrl(link, out var url));
            Assert.True(PackSources.IsRemote(url));
            Assert.False(PackSources.IsRewritableInFlight(url));
        }
    }
}
