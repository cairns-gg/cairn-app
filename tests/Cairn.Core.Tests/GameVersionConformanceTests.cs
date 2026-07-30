#if HAS_GAME
using Xunit;
using Real = Vintagestory.API.Config.GameVersion;

namespace Cairn.Core.Tests;

/// <summary>
/// Holds our dependency-free port honest by running it against the game's own
/// implementation over <see cref="VersionCorpus"/>. Compiled only when VINTAGE_STORY
/// points at an install; a clean checkout skips the whole class.
///
/// If one of these fails after a game update, the game changed its version semantics
/// and GameVersions must be updated to match — do not "fix" the test.
/// </summary>
public class GameVersionConformanceTests
{
    [Fact]
    public void SplitVersionString_matches_the_game_for_every_corpus_entry()
    {
        var mismatches = new List<string>();

        foreach (var v in VersionCorpus.Versions)
        {
            var ours = Try(() => GameVersions.SplitVersionString(v));
            var theirs = Try(() => Real.SplitVersionString(v));

            if (ours != theirs)
                mismatches.Add($"  {v,-14} ours={ours}  game={theirs}");
        }

        Assert.True(mismatches.Count == 0,
            "SplitVersionString diverged from the game:\n" + string.Join("\n", mismatches));
    }

    [Fact]
    public void IsAtLeastVersion_matches_the_game_for_every_pair()
        => AssertPairsMatch(
            nameof(GameVersions.IsAtLeastVersion),
            (a, b) => GameVersions.IsAtLeastVersion(a, b),
            (a, b) => Real.IsAtLeastVersion(a, b));

    [Fact]
    public void IsNewerVersionThan_matches_the_game_for_every_pair()
        => AssertPairsMatch(
            nameof(GameVersions.IsNewerVersionThan),
            (a, b) => GameVersions.IsNewerVersionThan(a, b),
            (a, b) => Real.IsNewerVersionThan(a, b));

    [Fact]
    public void IsLowerVersionThan_matches_the_game_for_every_pair()
        => AssertPairsMatch(
            nameof(GameVersions.IsLowerVersionThan),
            (a, b) => GameVersions.IsLowerVersionThan(a, b),
            (a, b) => Real.IsLowerVersionThan(a, b));

    private static void AssertPairsMatch(
        string name, Func<string, string, bool> ours, Func<string, string, bool> theirs)
    {
        var mismatches = new List<string>();

        foreach (var (a, b) in VersionCorpus.Pairs())
        {
            var o = Try(() => ours(a, b));
            var t = Try(() => theirs(a, b));

            if (o != t) mismatches.Add($"  {name}(\"{a}\", \"{b}\") ours={o} game={t}");
        }

        Assert.True(mismatches.Count == 0,
            $"{name} diverged from the game in {mismatches.Count} case(s):\n"
            + string.Join("\n", mismatches.Take(25)));
    }

    /// <summary>
    /// Compares thrown exception types as well as values — the game throws
    /// ArgumentNullException on empty input and our port must do the same.
    /// </summary>
    private static string Try<T>(Func<T> f)
    {
        try
        {
            var value = f();
            return value is int[] a ? "[" + string.Join(",", a) + "]" : value?.ToString() ?? "null";
        }
        catch (Exception e)
        {
            return "throws:" + e.GetType().Name;
        }
    }
}
#endif
