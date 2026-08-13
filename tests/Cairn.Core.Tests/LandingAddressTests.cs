using System.Net;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Which address a pack is recorded as having come from, when a redirect means there are
/// two candidates.
///
/// HttpClient follows redirects without saying so, and https to another https host is
/// allowed — only a downgrade to http is refused. Every front-end records the address it
/// fetched from as the pack's origin and shows it to somebody deciding whether to trust
/// the thing, so "asked" and "answered" being different matters.
/// </summary>
public class LandingAddressTests
{
    /// <summary>
    /// HttpClient sets RequestMessage on the response to the *final* request, which is what
    /// makes this readable at all. Simulated here rather than by running a redirect, since
    /// the property under test is which field is read.
    /// </summary>
    private static HttpResponseMessage Answered(string finalUrl) =>
        new(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUrl),
        };

    [Fact]
    public void The_address_that_answered_is_the_one_recorded()
    {
        var response = Answered("https://elsewhere.example/u/p.json");

        Assert.Equal(
            "https://elsewhere.example/u/p.json",
            PackSources.LandingAddress(response, "https://cairns.gg/u/p.json"));
    }

    [Fact]
    public void With_no_redirect_it_is_simply_the_address_asked_for()
    {
        var response = Answered("https://cairns.gg/u/p.json");

        Assert.Equal(
            "https://cairns.gg/u/p.json",
            PackSources.LandingAddress(response, "https://cairns.gg/u/p.json"));
    }

    /// <summary>
    /// A response that cannot say falls back to what was asked — no worse than the
    /// behaviour this replaced, and the shape a stubbed handler in a test produces.
    /// </summary>
    [Fact]
    public void A_response_that_says_nothing_falls_back_rather_than_throwing()
    {
        var bare = new HttpResponseMessage(HttpStatusCode.OK);

        Assert.Equal(
            "https://cairns.gg/u/p.json",
            PackSources.LandingAddress(bare, "https://cairns.gg/u/p.json"));
    }
}
