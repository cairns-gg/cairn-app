using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cairn.Core.Packs;

namespace Cairn.Core.Cairns;

public sealed class CairnsException(string message) : Exception(message);

/// <summary>What starting a device flow gave us to show and to poll with.</summary>
public sealed record DeviceFlow(
    [property: JsonPropertyName("device_code")] string DeviceCode,
    [property: JsonPropertyName("user_code")] string UserCode,
    [property: JsonPropertyName("verification_uri")] string VerificationUri,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("interval")] int Interval);

/// <summary>Where a published pack ended up.</summary>
public sealed record PublishResult(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("visibility")] string Visibility);

/// <summary>
/// Talks to cairns.gg.
///
/// Deliberately small and deliberately not sharing types with the server: what is between
/// them is a wire format, and the two ship on their own schedules. The document that goes
/// up is produced by <see cref="PackStore.PublishedDocument"/>, which is the same bytes the
/// Share window fingerprinted — so what was shown is what is sent.
/// </summary>
public sealed class CairnsClient(HttpClient http, string? server = null)
{
    public const string DefaultServer = "https://cairns.gg";

    /// <summary>
    /// Overridable so the whole flow can be exercised against a server running on this
    /// machine, which is the only way to test it before the real one exists.
    /// </summary>
    public string Server { get; } =
        (server
         ?? Environment.GetEnvironmentVariable("CAIRNS_SERVER")
         ?? DefaultServer).TrimEnd('/');

    // ---- signing in ----

    public async Task<DeviceFlow> StartSignInAsync(CancellationToken ct = default)
    {
        var response = await http.PostAsync($"{Server}/api/auth/device", EmptyJson(), ct)
            .ConfigureAwait(false);

        await ThrowIfFailed(response, "start signing in").ConfigureAwait(false);

        return await response.Content.ReadFromJsonAsync<DeviceFlow>(ct).ConfigureAwait(false)
               ?? throw new CairnsException("The server did not say how to sign in.");
    }

    /// <summary>
    /// Waits for the code to be approved in a browser, then returns the token.
    ///
    /// Polls at the interval the server asked for rather than one of our choosing: polling
    /// costs the client nothing and the server something, and the server is the one that
    /// knows how much of it it wants.
    /// </summary>
    public async Task<CairnsSession> AwaitSignInAsync(
        DeviceFlow flow, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        // Both of these come from the server, and both decide how long this machine waits.
        // expires_in is an int, so a hostile or broken one can name roughly sixty-eight
        // years — and the CLI's sign-in is a foreground loop with a person watching it, so
        // that is a hang rather than a long wait. Clamped to something longer than any real
        // device flow and shorter than a lost afternoon; the interval likewise, since a
        // server asking to be polled once an hour is asking for the same thing slowly.
        var window = TimeSpan.FromSeconds(Math.Clamp(flow.ExpiresIn, 1, MaxSignInSeconds));
        var deadline = DateTimeOffset.UtcNow + window;
        var interval = TimeSpan.FromSeconds(Math.Clamp(flow.Interval, 1, MaxPollSeconds));

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var response = await http.PostAsJsonAsync(
                $"{Server}/api/auth/device/token", new { token = flow.DeviceCode }, ct)
                .ConfigureAwait(false);

            // 428 is "keep waiting"; anything else is an answer, good or bad.
            //
            // The wait is after the ask rather than before it. Delaying first meant
            // somebody who approved in the browser before this loop got going still sat
            // through a full interval of "waiting for the browser…" with nothing left to
            // wait for — and it let a server choose how long the very first request took
            // by naming a large interval.
            if (response.StatusCode == HttpStatusCode.PreconditionRequired)
            {
                progress?.Report("waiting for the browser…");
                await Task.Delay(interval, ct).ConfigureAwait(false);
                continue;
            }

            await ThrowIfFailed(response, "sign in").ConfigureAwait(false);

            var granted = await response.Content.ReadFromJsonAsync<TokenResponse>(ct)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(granted?.Token))
                throw new CairnsException("The server approved the sign-in but sent no token.");

            var session = new CairnsSession { Server = Server, Token = granted.Token };
            session.Username = await WhoAmIAsync(session, ct).ConfigureAwait(false) ?? "";

            return session;
        }

        throw new CairnsException("The sign-in code expired before it was approved.");
    }

    public async Task<string?> WhoAmIAsync(CairnsSession session, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Server}/api/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);

        var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        var me = await response.Content.ReadFromJsonAsync<MeResponse>(ct).ConfigureAwait(false);
        return me?.Username;
    }

    // ---- publishing ----

    /// <summary>
    /// Sends a document, exactly as given. It is not rebuilt here: the Share window showed
    /// this and fingerprinted it, and a document assembled a second time is a document that
    /// can differ from the one somebody agreed to.
    /// </summary>
    public async Task<PublishResult> PublishAsync(
        CairnsSession session, string document, string slug, bool @public,
        CancellationToken ct = default)
    {
        var url = $"{Server}/api/packs?slug={Uri.EscapeDataString(slug)}"
                  + $"&visibility={(@public ? "public" : "unlisted")}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(document, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);

        var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        await ThrowIfFailed(response, "publish").ConfigureAwait(false);

        return await response.Content.ReadFromJsonAsync<PublishResult>(ct).ConfigureAwait(false)
               ?? throw new CairnsException("The server accepted the pack but said nothing about it.");
    }

    /// <summary>
    /// Whether the site is serving this pack, or a tombstone where it was.
    ///
    /// Asked on the one path where the answer changes what happens. Publishing refuses a
    /// revision identical to its predecessor, which is right while the pack is up and
    /// exactly wrong once it is not: republishing an unchanged document is how an author
    /// brings a withdrawn pack back. A withdrawal made on the site never reaches this
    /// machine, so that refusal can rest on a record describing a pack that stopped being
    /// served — and it has to be checked against the server before it blocks anybody.
    ///
    /// Anonymous, because the tombstone is public. Unlisted packs answer here too: being
    /// unlisted is being absent from browse, not from its own address.
    /// </summary>
    public async Task<bool> IsWithdrawnAsync(
        string username, string slug, CancellationToken ct = default)
    {
        try
        {
            using var response = await http
                .GetAsync($"{Server}/api/packs/{username}/{slug}", ct).ConfigureAwait(false);

            return response.StatusCode == HttpStatusCode.Gone;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // Not knowing is not the same as knowing it is live, but inventing a
            // withdrawal is worse than leaving the refusal standing — and a publish over
            // this same connection is about to fail and say so anyway.
            return false;
        }
    }

    public async Task WithdrawAsync(
        CairnsSession session, string username, string slug, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete, $"{Server}/api/packs/{username}/{slug}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);

        var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        await ThrowIfFailed(response, "withdraw the pack").ConfigureAwait(false);
    }

    // ---- plumbing ----

    private static StringContent EmptyJson() => new("{}", Encoding.UTF8, "application/json");

    /// <summary>
    /// Turns a failure into something worth reading. The server reports refusals as a list
    /// of problems, and repeating them beats "the server said 400" — they are the reason,
    /// and they are usually actionable.
    /// </summary>
    /// <summary>
    /// The most of a server-supplied message worth repeating, and the only characters of it
    /// worth printing.
    ///
    /// These strings end up on a terminal by way of the CLI writing them to stderr, and a
    /// terminal treats some bytes as instructions rather than text: an escape sequence can
    /// repaint the line, hide what came before, or set the window title. So a server that
    /// answers a failed publish with the right bytes gets to write on somebody's screen
    /// whatever it likes, in a place they are reading precisely because something went
    /// wrong. Length matters for the same reason — an uncapped body scrolls the actual
    /// error out of view.
    ///
    /// Tabs and newlines are kept, because problem lists are printed as lists.
    /// </summary>
    private static string Printable(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var clean = new System.Text.StringBuilder(Math.Min(text.Length, MaxServerMessage));

        foreach (var ch in text)
        {
            if (clean.Length >= MaxServerMessage) break;

            // Control characters other than the two that mean layout, and the Unicode
            // separators a terminal may also act on.
            // The two that mean layout are kept; anything a terminal might act on rather
            // than draw is dropped. By category rather than by listing characters — that
            // covers ESC and the C1 range, the line and paragraph separators, and the
            // invisible formatting characters that can reorder a rendered line. Naming
            // them as literals would also put invisible characters in this file.
            if (ch is '\n' or '\t')
            {
                clean.Append(ch);
                continue;
            }

            if (CharUnicodeInfo.GetUnicodeCategory(ch) is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator) continue;

            clean.Append(ch);
        }

        return clean.Length == MaxServerMessage ? clean.Append('…').ToString() : clean.ToString();
    }

    private const int MaxServerMessage = 500;

    /// <summary>
    /// The longest a sign-in may be left waiting, whatever the server says. Fifteen minutes
    /// is far more than a device flow needs and far less than a stuck program.
    /// </summary>
    private const int MaxSignInSeconds = 15 * 60;

    /// <summary>The slowest poll a server may ask for before it is simply waiting.</summary>
    private const int MaxPollSeconds = 30;

    private static async Task ThrowIfFailed(HttpResponseMessage response, string doing)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new CairnsException($"Not signed in — could not {doing}.");

        try
        {
            var problems = JsonSerializer.Deserialize<ProblemResponse>(body);

            if (problems?.Problems is { Length: > 0 })
                throw new CairnsException(
                    $"Could not {doing}:\n  "
                    + string.Join("\n  ", problems.Problems.Select(Printable)));

            if (!string.IsNullOrWhiteSpace(problems?.Error))
                throw new CairnsException($"Could not {doing}: {Printable(problems.Error)}");
        }
        catch (JsonException)
        {
            // Not the shape we expected; fall through to the status line.
        }

        throw new CairnsException($"Could not {doing}: the server answered {(int)response.StatusCode}.");
    }

    private sealed record TokenResponse([property: JsonPropertyName("token")] string Token);
    private sealed record MeResponse([property: JsonPropertyName("username")] string Username);

    private sealed record ProblemResponse(
        [property: JsonPropertyName("problems")] string[]? Problems,
        [property: JsonPropertyName("error")] string? Error);
}
