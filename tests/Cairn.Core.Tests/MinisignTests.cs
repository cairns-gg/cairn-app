using System.Text;
using Cairn.Core.Updates;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Checking a minisign signature, against vectors produced by minisign 0.12 itself rather
/// than by anything here. A verifier tested only against its own idea of the format proves
/// that it agrees with itself.
///
/// The keys below are throwaways generated for these tests and exist nowhere else. Only the
/// public halves are recorded; the private halves were never in the repository.
/// </summary>
public class MinisignTests
{
    private static readonly byte[] Content = Encoding.UTF8.GetBytes("""{"version":"9.9.9","files":[]}""");

    private const string PublicKey = "RWRAbJ1gHdDEh9xDOLFum0islHiQrxMrXefIFoeDUB2GgqUNY4bHmPXr";

    private const string Signature = """
        untrusted comment: signature from minisign secret key
        RURAbJ1gHdDEh1kwMDfGV69HgYfIYB2lqm3sydW8rcHT+CkZskcZT5Emy+MOlR0oWpqfADCSKqL5KsPVXkCdTALA24Qxm57HGgc=
        trusted comment: cairn test
        xf98tIzMdZmH5bEiIenTMUsIMI49kKIvGpsCZS1ywMsMlxclFuIEXNr26a+n9gDSqSNecaug+yjucpuxrA3RAA==
        """;

    /// <summary>A different key, signing the same bytes. Well-formed, and not ours.</summary>
    private const string OtherPublicKey = "RWQDiHgg9aatPFKkqUvPYNvMyNAevHIYjOOTWaN65OATfn8zQawEfQCZ";

    private const string OtherSignature = """
        untrusted comment: signature from minisign secret key
        RUQDiHgg9aatPH1Ppl8ZbLEweeAsLVys6IyGr6t3akkF52Cw46b7pvFpMsAa8yp2/6hBecgF/PTrHgXYxK+d5FNZrVfq7aE+xQw=
        trusted comment: other key
        +nWxVNcjmtdry9F4dJwKBwL1ydsxAl5gXaXZaFRjru3En71CngWoxkXryAJVt4BOMx+k83VMfGTZBaKUuiWjCw==
        """;

    [Fact]
    public void A_real_signature_over_the_real_content_verifies()
    {
        Assert.Null(Minisign.Problem(Content, Signature, PublicKey));
        Assert.True(Minisign.Verify(Content, Signature, PublicKey));
    }

    /// <summary>
    /// The whole point. minisign writes prehashed (`ED`) signatures by default, with no
    /// flag asked for — a verifier that only understood the unhashed form would pass every
    /// hand-made test and reject every signature the release job actually produces.
    /// </summary>
    [Fact]
    public void The_vector_is_the_prehashed_form_minisign_writes_by_default()
    {
        var header = Convert.FromBase64String(Signature.Split('\n')[1].Trim());
        Assert.Equal("ED", Encoding.ASCII.GetString(header, 0, 2));
    }

    [Fact]
    public void Content_that_changed_by_one_byte_does_not_verify()
    {
        var tampered = (byte[])Content.Clone();
        tampered[^2] ^= 0xFF;

        Assert.NotNull(Minisign.Problem(tampered, Signature, PublicKey));
    }

    [Fact]
    public void Content_with_something_appended_does_not_verify()
    {
        var extended = Content.Concat(Encoding.UTF8.GetBytes(" ")).ToArray();

        Assert.NotNull(Minisign.Problem(extended, Signature, PublicKey));
    }

    /// <summary>
    /// A perfectly valid signature made by somebody else. This is the case a check that
    /// only asked "does this verify" would accept, and it is the one that matters: an
    /// attacker can always make a good signature with a key they own.
    /// </summary>
    [Fact]
    public void A_valid_signature_from_another_key_is_refused()
    {
        // Genuinely valid on its own terms, so the test is not passing by accident.
        Assert.Null(Minisign.Problem(Content, OtherSignature, OtherPublicKey));

        Assert.NotNull(Minisign.Problem(Content, OtherSignature, PublicKey));
    }

    /// <summary>
    /// The trusted comment is covered by the second signature, which is the only reason it
    /// is called trusted. Editing it has to fail.
    /// </summary>
    [Fact]
    public void An_edited_trusted_comment_does_not_verify()
    {
        var edited = Signature.Replace("trusted comment: cairn test",
                                       "trusted comment: cairn 99.9.9");

        Assert.NotNull(Minisign.Problem(Content, edited, PublicKey));
    }

    /// <summary>
    /// The untrusted comment is exactly that, and minisign does not cover it either. Worth
    /// pinning so nobody later reads it for anything.
    /// </summary>
    [Fact]
    public void The_untrusted_comment_is_not_covered_and_does_not_matter()
    {
        var edited = Signature.Replace("untrusted comment: signature from minisign secret key",
                                       "untrusted comment: anything at all");

        Assert.Null(Minisign.Problem(Content, edited, PublicKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a signature")]
    [InlineData("untrusted comment: x\nnotbase64!!\ntrusted comment: y\nalsonot!!")]
    public void Rubbish_is_refused_rather_than_throwing(string signature) =>
        Assert.NotNull(Minisign.Problem(Content, signature, PublicKey));

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("RWRAbJ1gHdDEh9xDOLFum0islHiQrxMrXefIFoeDUB2GgqUNY4bHmPX")]  // truncated
    public void An_unusable_public_key_verifies_nothing(string key) =>
        Assert.NotNull(Minisign.Problem(Content, Signature, key));

    /// <summary>
    /// A .pub file is a comment line and then the key. Somebody pasting either the file or
    /// just the line should get the same answer.
    /// </summary>
    [Fact]
    public void The_key_may_be_a_pub_file_or_just_the_line_out_of_one()
    {
        var file = "untrusted comment: minisign public key 87C4D01D609D6C40\n" + PublicKey + "\n";

        Assert.Null(Minisign.Problem(Content, Signature, file));
    }

    [Fact]
    public void Nothing_to_check_is_not_quietly_fine() =>
        Assert.NotNull(Minisign.Problem([], Signature, PublicKey));
}
