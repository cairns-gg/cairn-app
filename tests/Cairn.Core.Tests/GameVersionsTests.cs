using Xunit;

namespace Cairn.Core.Tests;

public class GameVersionsTests
{
    [Theory]
    [InlineData("1.22.5", new[] { 1, 22, 5, 3 })]
    [InlineData("1.22", new[] { 1, 22, 0, 3 })]           // ".0" appended
    [InlineData("1.22.0-rc.1", new[] { 1, 22, 0, 2, 1 })] // rc ranks 2
    [InlineData("1.22.0-pre.3", new[] { 1, 22, 0, 1, 3 })]// pre ranks 1
    [InlineData("1.22.0-dev.1", new[] { 1, 22, 0, 0, 1 })]// anything else ranks 0
    [InlineData(">=1.22.0", new[] { 0, 22, 0, 3 })]       // ">=1" silently becomes 0
    [InlineData("garbage", new[] { 0, 3 })]
    public void SplitVersionString_reproduces_game_quirks(string version, int[] expected)
        => Assert.Equal(expected, GameVersions.SplitVersionString(version));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void SplitVersionString_rejects_empty(string? version)
        => Assert.Throws<ArgumentNullException>(() => GameVersions.SplitVersionString(version!));

    [Theory]
    [InlineData("1.22.5", "1.22.0", true)]
    [InlineData("1.21.5", "1.22.0", false)]
    [InlineData("1.22.0", "1.22.0", true)]
    [InlineData("1.22.5", "1.22.6", false)]
    public void IsAtLeastVersion_implements_dependency_minimums(string game, string dep, bool expected)
        => Assert.Equal(expected, GameVersions.IsAtLeastVersion(game, dep));

    [Fact]
    public void A_malformed_dependency_is_satisfied_by_everything()
    {
        // This is the trap: ">=1.22.0" becomes 0.22.0, so it matches far older games.
        Assert.True(GameVersions.IsAtLeastVersion("1.19.0", ">=1.22.0"));
        Assert.True(GameVersions.IsAtLeastVersion("1.19.0", "garbage"));

        // ...which is exactly why the manifest layer refuses them up front.
        Assert.False(GameVersions.IsPlausibleVersion(">=1.22.0"));
        Assert.False(GameVersions.IsPlausibleVersion("garbage"));
    }

    [Fact]
    public void Version_ordering_is_numeric_not_lexical()
    {
        Assert.True(GameVersions.IsNewerVersionThan("1.10.0", "1.9.0"));
        Assert.True(GameVersions.IsLowerVersionThan("1.9.0", "1.10.0"));
    }

    [Theory]
    [InlineData("1.22.5", true)]
    [InlineData("1.22", true)]
    [InlineData("1.22.0-rc.1", true)]
    [InlineData("1.22.0-pre.5", true)]
    [InlineData(">=1.22.0", false)]
    [InlineData("^1.22", false)]
    [InlineData("1.22.x", false)]
    [InlineData("v1.22.5", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPlausibleVersion_gates_what_a_manifest_may_contain(string? version, bool expected)
        => Assert.Equal(expected, GameVersions.IsPlausibleVersion(version));

    [Fact]
    public void IsSameMajorMinor_ignores_revision()
    {
        Assert.True(GameVersions.IsSameMajorMinor("1.22.0", "1.22.5"));
        Assert.False(GameVersions.IsSameMajorMinor("1.21.5", "1.22.5"));
    }
}
