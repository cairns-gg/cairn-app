#if HAS_GAME
using System.Reflection;
using System.Text.Json.Nodes;
using Cairn.Core.Hotkeys;
using Xunit;
using RealCombination = Vintagestory.API.Client.KeyCombination;
using RealKeys = Vintagestory.API.Client.GlKeys;

namespace Cairn.Core.Tests;

/// <summary>
/// Holds what Cairn writes into keyMapping to the type the game reads it into. Compiled only
/// when VINTAGE_STORY points at an install, like <see cref="GameVersionConformanceTests"/>.
///
/// What replaced a round trip through Cairn's own reader, which could only show that Cairn
/// agreed with itself: a misspelt field reads back as faithfully as it was written, and the
/// game would have loaded it as no binding at all. If one of these fails after a game update,
/// the game changed the shape — follow it in KeyBinding and GlKeys, and do not "fix" the test.
/// </summary>
public class KeyCombinationConformanceTests
{
    private static readonly FieldInfo[] Fields =
        typeof(RealCombination).GetFields(BindingFlags.Public | BindingFlags.Instance);

    [Fact]
    public void Every_property_written_is_a_field_of_the_games_type_and_of_its_type()
    {
        var json = KeyBinding.Parse("Ctrl+K,M")!.ToJson();

        foreach (var (name, value) in json)
        {
            // Exact casing: the settings file is the game's, and other code reads it too.
            var field = Fields.SingleOrDefault(f => f.Name == name);
            Assert.True(field is not null, $"KeyCombination has no field {name}");

            var expected = Nullable.GetUnderlyingType(field!.FieldType) ?? field.FieldType;
            var written = (value as JsonValue)!.GetValueKind() switch
            {
                System.Text.Json.JsonValueKind.Number => typeof(int),
                System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => typeof(bool),
                var other => throw new InvalidOperationException($"{name} written as {other}"),
            };

            Assert.Equal(expected, written);
        }
    }

    [Fact]
    public void Every_field_of_the_games_type_is_written()
    {
        // One left out deserialises to its default, which for a modifier is silently "not held".
        var written = KeyBinding.Parse("Ctrl+K")!.ToJson().Select(p => p.Key).ToHashSet();

        Assert.All(Fields, f => Assert.Contains(f.Name, written));
    }

    [Fact]
    public void Every_key_name_has_the_games_code()
    {
        var mismatches = GlKeys.All
            .Where(name => GlKeys.TryParse(name, out _))
            .Select(name => (name, ours: Code(name), theirs: Enum.TryParse<RealKeys>(name, out var k) ? (int?)k : null))
            .Where(x => x.ours != x.theirs)
            .Select(x => $"  {x.name,-16} ours={x.ours} game={x.theirs}")
            .ToList();

        Assert.True(mismatches.Count == 0, "Key codes diverged from the game:\n" + string.Join("\n", mismatches));

        static int Code(string name) => GlKeys.TryParse(name, out var code) ? code : -1;
    }
}
#endif
