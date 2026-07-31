using System.Net;
using System.Text;
using Cairn.Core.ModDb;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Searching restricted to a pack's game version.
///
/// The rule is the pack's major.minor, not its exact patch, because that is what Cairn
/// accepts when it actually installs a release. The engine ships patch releases that
/// rarely break mods, so most are marked for x.y.0 and never re-tagged: measured against
/// the live API, "olla" filtered to exactly 1.22.5 returned 49 mods where the full 1.22.x
/// range returned 248 — and Cairn installs all 248. A stricter filter would hide four
/// fifths of what works.
/// </summary>
public class ModSearchFilterTests
{
    /// <summary>Answers the game-version list and records the search URLs it is asked for.</summary>
    private sealed class Stub : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        private const string Versions = """
        {"statuscode":"200","gameversions":[
          {"tagid":-281569466384383,"name":"1.22.5"},
          {"tagid":-281569466449919,"name":"1.22.4"},
          {"tagid":-281569466056703,"name":"1.22.0"},
          {"tagid":-281565171417087,"name":"1.21.5"},
          {"tagid":-281560876449791,"name":"1.20.0"},
          {"tagid":-281492156858370,"name":"1.4.4-dev.2"}
        ]}
        """;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);

            var body = url.Contains("/gameversions") ? Versions : """{"statuscode":"200","mods":[]}""";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (ModDbClient Client, Stub Handler) Make()
    {
        var handler = new Stub();
        return (new ModDbClient(new HttpClient(handler)), handler);
    }

    [Fact]
    public async Task The_whole_minor_is_accepted_not_just_the_exact_patch()
    {
        var (client, _) = Make();

        var tags = await client.GameVersionTagsForMinorAsync("1.22.5");

        // 1.22.5, 1.22.4 and 1.22.0 — a mod marked only for 1.22.0 installs fine on 1.22.5.
        Assert.Equal(3, tags.Count);
        Assert.Contains(-281569466056703, tags);
    }

    [Fact]
    public async Task Other_minors_are_excluded()
    {
        var (client, _) = Make();
        var tags = await client.GameVersionTagsForMinorAsync("1.22.5");

        Assert.DoesNotContain(-281565171417087, tags);   // 1.21.5
        Assert.DoesNotContain(-281560876449791, tags);   // 1.20.0
    }

    [Fact]
    public async Task A_filtered_search_sends_every_tag_in_the_minor()
    {
        var (client, handler) = Make();

        await client.SearchAsync("olla", gameVersion: "1.22.5");

        var search = handler.Requests.Single(r => r.Contains("/mods?"));
        Assert.Contains("text=olla", search);

        // A repeated array parameter, one tag per version in the minor. Counted by name
        // because .NET may percent-encode the brackets — verified against the live API,
        // both spellings filter identically.
        Assert.Equal(3, search.Split("gameversions").Length - 1);
    }

    [Fact]
    public async Task Ranked_search_asks_twice_so_it_can_tell_which_results_are_usable()
    {
        var (client, handler) = Make();

        await client.SearchRankedAsync("olla", "1.22.5");

        // One filtered and one unfiltered: the difference between them is exactly the set
        // with no usable release, which no per-result field reports. Two requests beats
        // one lookup per result.
        var searches = handler.Requests.Where(r => r.Contains("/mods?")).ToList();
        Assert.Equal(2, searches.Count);
        Assert.Single(searches.Where(r => r.Contains("gameversions")));
        Assert.Single(searches.Where(r => !r.Contains("gameversions")));
    }

    [Fact]
    public async Task Without_a_game_version_it_asks_once_and_calls_everything_usable()
    {
        var (client, handler) = Make();

        var results = await client.SearchRankedAsync("olla");

        Assert.Single(handler.Requests.Where(r => r.Contains("/mods?")));
        Assert.All(results, r => Assert.True(r.Compatible));
    }

    [Fact]
    public async Task An_unfiltered_search_asks_for_no_versions_at_all()
    {
        var (client, handler) = Make();

        await client.SearchAsync("olla");

        var search = handler.Requests.Single(r => r.Contains("/mods?"));
        Assert.DoesNotContain("gameversions", search);

        // And it does not even fetch the version list it would not use.
        Assert.DoesNotContain(handler.Requests, r => r.Contains("/gameversions"));
    }

    [Fact]
    public async Task The_version_list_is_fetched_once_and_reused()
    {
        var (client, handler) = Make();

        await client.SearchAsync("a", "1.22.5");
        await client.SearchAsync("b", "1.22.5");
        await client.SearchAsync("c", "1.22.0");

        // It changes only when the game ships a release; re-fetching per search is waste.
        Assert.Single(handler.Requests.Where(r => r.Contains("/gameversions")));
    }

    [Fact]
    public async Task A_version_ModDB_has_never_heard_of_falls_back_to_searching_everything()
    {
        var (client, handler) = Make();

        await client.SearchAsync("olla", gameVersion: "9.99.9");

        // Better than sending nothing and hoping, or refusing to search at all.
        var search = handler.Requests.Single(r => r.Contains("/mods?"));
        Assert.DoesNotContain("gameversions", search);
    }

    [Fact]
    public void Compatibility_of_a_named_mod_matches_what_resolving_would_do()
    {
        var mod = new ModDbMod
        {
            Name = "Olla",
            Releases =
            [
                new ModDbRelease { ModVersion = "1.1.0", ModIdStr = "olla", Tags = ["1.22.0"] },
            ],
        };

        // Same-minor counts, which is the point.
        Assert.True(ModDbClient.HasReleaseFor(mod, "1.22.5"));
        Assert.True(ModDbClient.HasReleaseFor(mod, "1.22.0"));

        Assert.False(ModDbClient.HasReleaseFor(mod, "1.21.5"));
        Assert.False(ModDbClient.HasReleaseFor(mod, "1.23.0"));
    }

    [Fact]
    public void A_mod_with_no_releases_is_compatible_with_nothing()
        => Assert.False(ModDbClient.HasReleaseFor(new ModDbMod { Name = "Empty" }, "1.22.5"));
}
