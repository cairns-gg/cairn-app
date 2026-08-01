using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// The rule that decides whether a pack document is allowed to be fetched at all.
///
/// Worth testing directly rather than through import: a pack names the mods, their
/// download URLs and their hashes, so anyone who can rewrite one in flight chooses what
/// gets installed — and writes hashes that match. A hole here is not a bug in importing,
/// it is arbitrary code on somebody's machine.
/// </summary>
public class PackSourceTests
{
    [Theory]
    [InlineData("http://cairns.gg/dizzyd/anego.json")]
    [InlineData("http://192.168.1.10:5080/pack.json")]     // the LAN is still a network
    [InlineData("HTTP://CAIRNS.GG/pack.json")]             // and casing is not a bypass
    public void Plain_http_across_a_network_is_refused(string source) =>
        Assert.True(PackSources.IsRewritableInFlight(source));

    [Theory]
    [InlineData("http://localhost:5080/dizzyd/anego.json")]
    [InlineData("http://127.0.0.1:5080/pack.json")]
    [InlineData("http://[::1]:5080/pack.json")]
    public void Loopback_is_allowed_because_nothing_can_sit_on_it(string source)
    {
        // These packets never leave the machine, so the reason for the rule does not
        // apply — and importing what you just published to a local server needs it.
        Assert.False(PackSources.IsRewritableInFlight(source));
        Assert.True(PackSources.IsRemote(source));
    }

    [Theory]
    [InlineData("https://cairns.gg/dizzyd/anego.json")]
    [InlineData("https://localhost:5080/pack.json")]
    public void Https_is_always_fine(string source)
    {
        Assert.False(PackSources.IsRewritableInFlight(source));
        Assert.True(PackSources.IsRemote(source));
    }

    [Theory]
    [InlineData("/Users/dizzyd/anego.json")]
    [InlineData("anego.json")]
    [InlineData("""{"formatVersion":1}""")]
    public void Anything_that_is_not_a_url_is_neither_fetched_nor_refused(string source)
    {
        // Local paths and pasted JSON never touch the network, so the transport rule has
        // nothing to say about them.
        Assert.False(PackSources.IsRemote(source));
        Assert.False(PackSources.IsRewritableInFlight(source));
    }

    [Fact]
    public void A_hostname_that_merely_starts_with_localhost_is_not_loopback()
    {
        // localhost.evil.com resolves to whatever its owner likes. This is the case a
        // string comparison gets wrong, which is why the check parses the URL instead.
        Assert.True(PackSources.IsRewritableInFlight("http://localhost.evil.com/pack.json"));
    }
}
