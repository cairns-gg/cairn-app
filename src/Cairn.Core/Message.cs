namespace Cairn.Core;

/// <summary>
/// A sentence Core has decided on but has not written down: a catalog key and the values
/// that go in it.
///
/// This exists for the places where Core builds a sentence out of a template and a clause
/// decided somewhere else. <c>ModConfigChange.Describe</c> renders "left alone — {reason}",
/// and the reason comes from whichever check refused the file; <c>PackManifest.Validate</c>
/// renders "Pack 'connect' {problem}", and the problem comes from
/// <see cref="Packs.ServerAddress"/>. Passing prose between those two halves was fine while
/// there was one language, and translating only the outer half produces a German sentence
/// with an English clause inside it — which is worse than either language on its own.
///
/// So the inner half travels as a key too, and is resolved at the moment somebody reads it
/// rather than at the moment the decision was made. That also means a message crossing a
/// language change — sitting in the launch log while somebody switches language in
/// Preferences — is not stale, because nothing was rendered when it was recorded. It is only
/// prose when something asks.
///
/// The honest limitation: a sentence built from two catalog entries constrains word order in
/// a way one entry does not, and some languages will want the clause somewhere the template
/// cannot put it. The way out where it matters is a key per combination rather than a
/// composition — worth doing for the handful a translator complains about, and not worth
/// doing for fourteen combinations up front on the chance that they will.
/// </summary>
public sealed class Message
{
    private readonly object?[] _args;

    public Message(string key, params object?[] args)
    {
        Key = key;
        _args = args;
    }

    /// <summary>The catalog key, which is what a test should assert on rather than the words.</summary>
    public string Key { get; }

    /// <summary>The sentence, in whatever language is current now.</summary>
    public string Text => Lang.Get(Key, _args);

    /// <summary>
    /// So a message can be interpolated straight into a format argument — which is exactly
    /// how the composed cases use it, and means the composing side needs to know nothing
    /// about this type.
    /// </summary>
    public override string ToString() => Text;
}
