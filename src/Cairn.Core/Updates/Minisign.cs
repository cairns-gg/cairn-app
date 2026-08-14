using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Cairn.Core.Updates;

/// <summary>
/// Checking a minisign signature.
///
/// The release manifest names both what to download and the SHA-256 to check it against,
/// so on its own it authenticates nothing — whoever can rewrite one rewrites the other, and
/// everything that produces it runs in one job holding one credential. A signature made
/// with a key that never enters that job is the only part of the answer that does not come
/// from the same place as the question.
///
/// Verification only. Cairn never signs anything, so there is no private-key handling here
/// and no way to add one by accident.
///
/// <para><b>Format</b>, read off minisign 0.12 rather than from the spec, because the one
/// detail that matters is not what the documentation leads you to expect. A signature file
/// is four lines:</para>
/// <code>
/// untrusted comment: ...
/// base64( algorithm[2] || key id[8] || signature[64] )
/// trusted comment: ...
/// base64( global signature[64] )
/// </code>
/// <para>The algorithm is <c>Ed</c> for a signature over the file itself and <c>ED</c> for
/// one over its BLAKE2b-512 hash. **Current minisign produces <c>ED</c> by default**, with
/// no flag asked for — writing a verifier that only understood <c>Ed</c> would pass its own
/// tests against hand-made vectors and reject every real signature. Both are accepted here.
/// The global signature covers the first signature followed by the trusted comment, which
/// is what stops that comment being edited after the fact.</para>
///
/// <para>The untrusted comment is exactly what it says and is not covered by anything. It
/// is never read here.</para>
/// </summary>
public static class Minisign
{
    /// <summary>Ed25519, in both the key and the unhashed signature form.</summary>
    private const string Legacy = "Ed";

    /// <summary>Ed25519 over BLAKE2b-512 of the content. What minisign writes by default.</summary>
    private const string Prehashed = "ED";

    /// <summary>
    /// Why this signature does not vouch for this content, or null when it does.
    ///
    /// Every failure is one message shape on purpose: a verifier that says which part
    /// failed is a verifier that helps somebody adjust a forgery until it stops
    /// complaining. The distinction worth drawing is between "this is not signed" and
    /// "this signature is wrong", and that one is drawn by the caller, which knows whether
    /// it found a signature at all.
    /// </summary>
    public static string? Problem(byte[] content, string signature, string publicKey)
    {
        if (content is null || content.Length == 0) return Lang.Get("sig-nothing-to-check");

        if (!TryReadPublicKey(publicKey, out var keyId, out var key))
            return Lang.Get("sig-key-unreadable");

        var lines = (signature ?? "").Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToList();

        // Four lines, and the two comments are not optional in anything minisign writes.
        if (lines.Count < 4) return Lang.Get("sig-not-minisign");

        if (!TryDecode(lines[1], 10 + 64, out var first)) return Lang.Get("sig-unreadable");
        if (!TryDecode(lines[3], 64, out var global)) return Lang.Get("sig-unreadable");

        var algorithm = System.Text.Encoding.ASCII.GetString(first, 0, 2);
        if (algorithm is not (Legacy or Prehashed)) return Lang.Get("sig-unknown-kind");

        // Bound to the key Cairn was built with, not merely to a well-formed signature.
        if (!first.AsSpan(2, 8).SequenceEqual(keyId))
            return Lang.Get("sig-wrong-key");

        var sig = first[10..];

        var message = algorithm == Prehashed ? Blake2b512(content) : content;
        if (!Ed25519Verify(key, message, sig)) return Lang.Get("sig-mismatch");

        // The trusted comment is signed too, and this is the signature that says so. Read
        // from the line rather than reconstructed, since the prefix is part of the format
        // and not part of what is signed.
        const string prefix = "trusted comment: ";
        if (!lines[2].StartsWith(prefix, StringComparison.Ordinal))
            return Lang.Get("sig-not-minisign");

        var comment = System.Text.Encoding.UTF8.GetBytes(lines[2][prefix.Length..]);
        var trusted = new byte[sig.Length + comment.Length];
        sig.CopyTo(trusted, 0);
        comment.CopyTo(trusted, sig.Length);

        return Ed25519Verify(key, trusted, global)
            ? null
            : Lang.Get("sig-mismatch");
    }

    /// <summary>Whether this signature vouches for this content.</summary>
    public static bool Verify(byte[] content, string signature, string publicKey) =>
        Problem(content, signature, publicKey) is null;

    /// <summary>
    /// Accepts either a minisign <c>.pub</c> file or just the base64 line out of one, which
    /// is what <c>minisign -G</c> prints and what somebody is most likely to paste.
    /// </summary>
    private static bool TryReadPublicKey(string? text, out byte[] keyId, out byte[] key)
    {
        keyId = [];
        key = [];

        if (string.IsNullOrWhiteSpace(text)) return false;

        // The last non-comment line: a .pub file is a comment then the key, and a pasted
        // key is the key alone.
        var line = text.Split('\n')
            .Select(l => l.Trim())
            .LastOrDefault(l => l.Length > 0 && !l.StartsWith("untrusted comment:", StringComparison.Ordinal));

        if (line is null || !TryDecode(line, 10 + 32, out var raw)) return false;
        if (System.Text.Encoding.ASCII.GetString(raw, 0, 2) != Legacy) return false;

        keyId = raw[2..10];
        key = raw[10..];
        return true;
    }

    private static bool TryDecode(string line, int expected, out byte[] value)
    {
        value = [];
        Span<byte> buffer = new byte[expected + 16];

        if (!Convert.TryFromBase64String(line.Trim(), buffer, out var written)) return false;
        if (written != expected) return false;

        value = buffer[..written].ToArray();
        return true;
    }

    private static byte[] Blake2b512(byte[] content)
    {
        var digest = new Blake2bDigest(512);
        digest.BlockUpdate(content, 0, content.Length);

        var hash = new byte[64];
        digest.DoFinal(hash, 0);
        return hash;
    }

    private static bool Ed25519Verify(byte[] key, byte[] message, byte[] signature)
    {
        try
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(key, 0));
            verifier.BlockUpdate(message, 0, message.Length);
            return verifier.VerifySignature(signature);
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            // A malformed key or signature is a failed check rather than a crash — this
            // runs behind a window somebody is trying to play a game from.
            return false;
        }
    }
}
