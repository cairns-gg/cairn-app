namespace Cairn.Core.Packs;

/// <summary>
/// The address a pack may launch into.
///
/// A pack's <c>connect</c> is handed to the game as the value of <c>--connect</c>, and it
/// arrives from a shared pack — so it is somebody else's string reaching this machine's
/// game command line. Nothing checked it. <c>ArgumentList</c> means there is no shell to
/// inject into, and every argument is passed as its own argv entry, so this is not command
/// injection; what it is instead is an argument the game's own parser reads.
///
/// The game parses with CommandLineParser 2.9, which treats a token beginning with
/// <c>--</c> as an option name rather than as a value. A connect of <c>--logPath=…</c>
/// therefore does not arrive as a strange server address: it arrives as a second option,
/// with <c>--connect</c> left wanting a value. What the game does with a partially parsed
/// argv was never established, and is recorded in this review as a blind spot.
///
/// It stops mattering here. Rather than answering "what does the game do with a hostile
/// argv", this refuses to produce one: a connect address is a host and an optional port, and
/// anything that is not gets no further. That is the smaller question and the one this
/// codebase can answer on its own.
///
/// <para>Deliberately permissive about what a host may be — <see cref="Uri.CheckHostName"/>
/// decides, so a DNS name, an IPv4 literal and a bracketed IPv6 literal are all fine, and a
/// LAN name somebody made up is too. The rule is not "is this a real server"; it is "is this
/// a server address rather than an instruction".</para>
/// </summary>
public static class ServerAddress
{
    /// <summary>
    /// Longer than any real one. A hostname is capped at 253 characters by DNS, and the
    /// port and brackets cannot add many more.
    /// </summary>
    public const int MaxLength = 280;

    /// <summary>
    /// Why this cannot be used as a pack's connect address, phrased to follow the field
    /// name, or null when there is nothing wrong with it.
    /// </summary>
    public static string? Problem(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;   // absent is fine; that is most packs

        if (address.Length > MaxLength)
            return Lang.Get("address-too-long", address.Length);

        if (address != address.Trim())
            return Lang.Get("address-whitespace-around");

        if (address.Any(char.IsWhiteSpace) || address.Any(char.IsControl))
            return Lang.Get("address-control-characters");

        // The reason this type exists. A value beginning with '-' is read by the game's
        // parser as another option rather than as this one's value.
        if (address.StartsWith('-'))
            return Lang.Get("address-starts-with-dash");

        var (host, port) = Split(address);

        if (port is not null && (!int.TryParse(port, out var number) || number is < 1 or > 65535))
            return Lang.Get("address-bad-port", port);

        if (string.IsNullOrEmpty(host)) return Lang.Get("address-no-host");

        return Uri.CheckHostName(host) == UriHostNameType.Unknown
            ? Lang.Get("address-bad-host", host)
            : null;
    }

    public static bool IsValid(string? address) => Problem(address) is null;

    /// <summary>
    /// Host and port, or host and null. Written out rather than leaning on
    /// <see cref="Uri"/>, which wants a scheme and would happily read "evil:80" as one.
    /// </summary>
    private static (string Host, string? Port) Split(string address)
    {
        // A bracketed IPv6 literal carries colons of its own, so the port is only whatever
        // follows the closing bracket. CheckHostName wants the brackets kept.
        if (address.StartsWith('['))
        {
            var close = address.IndexOf(']');
            if (close < 0) return (address, null);

            var rest = address[(close + 1)..];
            return (address[..(close + 1)], rest.StartsWith(':') ? rest[1..] : null);
        }

        var colon = address.LastIndexOf(':');

        // No colon, or more than one and no brackets — the latter is a bare IPv6 literal,
        // which has no port and must not be split at its last colon.
        if (colon < 0 || address.IndexOf(':') != colon) return (address, null);

        return (address[..colon], address[(colon + 1)..]);
    }
}
