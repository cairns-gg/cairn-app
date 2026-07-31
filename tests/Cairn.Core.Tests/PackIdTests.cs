using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// A pack id is a directory name and it travels inside shared bundles, so it has to stay
/// in a narrow ASCII alphabet. That is the machine's constraint, not something worth
/// asking a person about — these pin the derivation that removes the question.
/// </summary>
public class PackIdTests
{
    [Theory]
    [InlineData("Anego Server", "anego-server")]
    [InlineData("Vanilla + QoL", "vanilla-qol")]
    [InlineData("  Leading and trailing  ", "leading-and-trailing")]
    [InlineData("Lots    of     space", "lots-of-space")]
    [InlineData("Punctuation!!! Everywhere???", "punctuation-everywhere")]
    [InlineData("under_scores_too", "under-scores-too")]
    [InlineData("1.22 Kitchen Sink", "1-22-kitchen-sink")]
    [InlineData("---leading dashes---", "leading-dashes")]
    public void Names_become_readable_slugs(string name, string expected)
        => Assert.Equal(expected, PackId.From(name));

    [Theory]
    [InlineData("Café", "cafe")]
    [InlineData("Anégo", "anego")]
    [InlineData("Ærø Ølberg", "aero-olberg")]
    [InlineData("Straße", "strasse")]
    [InlineData("Łódź", "lodz")]
    [InlineData("Škoda Ñandú", "skoda-nandu")]
    public void Accents_fold_to_the_letters_they_stand_for(string name, string expected)
    {
        // Not via Normalize(FormD): these projects set InvariantGlobalization, where
        // normalisation is a silent no-op and "Café" would quietly become "caf".
        Assert.Equal(expected, PackId.From(name));
    }

    [Fact]
    public void A_name_already_decomposed_folds_the_same_way()
    {
        // "e" followed by a combining acute, rather than the single character "é". Both
        // spellings of the same name must not produce two different packs.
        Assert.Equal(PackId.From("Café"), PackId.From("Café"));
    }

    [Theory]
    [InlineData("日本語")]
    [InlineData("!!!")]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData(null)]
    public void A_name_with_nothing_usable_yields_nothing(string? name)
        => Assert.Equal("", PackId.From(name));

    [Fact]
    public void But_the_fallback_always_gives_something_to_save_under()
        => Assert.Equal(PackId.Fallback, PackId.FromOrFallback("日本語"));

    [Fact]
    public void Slugging_is_idempotent()
    {
        // Relied on by the CLI: `cairn init anego` must still produce exactly "anego",
        // so callers can pass a name or an id without deciding which they have.
        foreach (var name in new[] { "Anego Server", "anego", "a-b-c", "Café", "1.22 Sink" })
            Assert.Equal(PackId.From(name), PackId.From(PackId.From(name)));
    }

    [Fact]
    public void Path_separators_cannot_survive()
    {
        // The traversal never reaches the filesystem: '/', '\' and '.' are not in the
        // alphabet, so there is nothing to reject later.
        var slug = PackId.From("../../../etc/evil");

        Assert.Equal("etc-evil", slug);
        Assert.True(PackStore.IsValidId(slug));
    }

    [Fact]
    public void Anything_it_produces_is_a_valid_id()
    {
        foreach (var name in new[]
                 {
                     "Anego Server", "Café", "../../etc", "!!!", "a".PadRight(200, 'b'),
                     "Straße", "under_score", "1.22", "  ", "日本語 mixed With Latin",
                 })
        {
            var slug = PackId.FromOrFallback(name);
            Assert.True(PackStore.IsValidId(slug), $"'{name}' produced '{slug}'");
        }
    }

    [Fact]
    public void A_very_long_name_is_truncated_without_a_trailing_dash()
    {
        var slug = PackId.From(string.Join(' ', Enumerable.Repeat("word", 40)));

        Assert.True(slug.Length <= PackId.MaxLength);
        Assert.False(slug.EndsWith('-'));
        Assert.True(PackStore.IsValidId(slug));
    }

    [Fact]
    public void The_fold_table_stays_aligned()
    {
        // Both halves are string literals; editing one without the other would shift
        // every mapping after the edit and fail silently.
        var accented = typeof(PackId)
            .GetField("Accented", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetRawConstantValue() as string;
        var folded = typeof(PackId)
            .GetField("Folded", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetRawConstantValue() as string;

        Assert.Equal(accented!.Length, folded!.Length);
        Assert.All(folded, c => Assert.True(char.IsAsciiLetter(c)));
    }

    // ---- uniqueness ----

    [Fact]
    public void An_id_already_in_use_gets_a_number()
    {
        var taken = new HashSet<string> { "anego-server" };
        Assert.Equal("anego-server-2", PackId.MakeUnique("anego-server", taken.Contains));
    }

    [Fact]
    public void It_keeps_counting_past_the_first_collision()
    {
        var taken = new HashSet<string> { "pack", "pack-2", "pack-3" };
        Assert.Equal("pack-4", PackId.MakeUnique("pack", taken.Contains));
    }

    [Fact]
    public void A_free_id_is_left_alone()
        => Assert.Equal("anego", PackId.MakeUnique("anego", _ => false));

    [Fact]
    public void A_maximum_length_id_still_fits_once_numbered()
    {
        var full = new string('a', PackId.MaxLength);
        var unique = PackId.MakeUnique(full, id => id == full);

        Assert.True(unique.Length <= PackId.MaxLength, $"'{unique}' is {unique.Length} chars");
        Assert.True(PackStore.IsValidId(unique));
    }
}
