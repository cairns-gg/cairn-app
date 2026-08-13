using Cairn.Core.Cairns;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// What a server is allowed to write on somebody's terminal.
///
/// cairns.gg's error bodies are repeated verbatim because they are usually the actionable
/// part, and the CLI writes them to stderr. A terminal treats some bytes as instructions
/// rather than text, so a hostile or compromised server answering a failed publish could
/// repaint the line, hide what came before, or set the window title - in the place somebody
/// is reading precisely because something went wrong.
///
/// Every control character here is written as an escape rather than as a literal. Putting
/// the real bytes in a source file makes the file itself unreadable in exactly the way the
/// code exists to prevent.
/// </summary>
public class ServerMessageTests
{
    private const char Escape = '\u001b';
    private const char RightToLeftOverride = '\u202e';

    /// <summary>
    /// Reaches the sanitiser through the public behaviour that uses it rather than by
    /// poking at a private method: what matters is what lands on the screen.
    /// </summary>
    private static async Task<string> MessageFor(string body)
    {
        var client = new CairnsClient(new HttpClient(new StubHandler(body)), "https://cairns.test");

        var thrown = await Assert.ThrowsAsync<CairnsException>(
            () => client.PublishAsync(new CairnsSession { Token = "t" }, "{}", "slug", true));

        return thrown.Message;
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
            {
                Content = new StringContent(body),
            });
    }

    [Fact]
    public async Task An_escape_sequence_does_not_reach_the_terminal()
    {
        // Clear-screen, then a window-title sequence: the two shapes that do the most with
        // the least.
        var message = await MessageFor(
            "{\"error\":\"\\u001b[2J\\u001b]0;owned\\u0007gone\"}");

        Assert.DoesNotContain(Escape, message);
        Assert.DoesNotContain('\u0007', message);

        // The readable part survives, because the message is still the useful bit.
        Assert.Contains("gone", message);
    }

    [Fact]
    public async Task Carriage_returns_and_backspaces_cannot_rewrite_the_line()
    {
        var message = await MessageFor(
            "{\"error\":\"harmless\\r\\b\\b\\b\\bDELETED\"}");

        Assert.DoesNotContain('\r', message);
        Assert.DoesNotContain('\b', message);
    }

    /// <summary>
    /// The invisible formatting characters that reorder a rendered line without changing
    /// its bytes - the reason this filters by category rather than by a list.
    /// </summary>
    [Fact]
    public async Task Direction_overrides_are_dropped_too()
    {
        var message = await MessageFor("{\"error\":\"safe\\u202egnorw\"}");

        Assert.DoesNotContain(RightToLeftOverride, message);
    }

    /// <summary>Layout is kept: problem lists are printed as lists.</summary>
    [Fact]
    public async Task Newlines_and_tabs_survive()
    {
        var message = await MessageFor("{\"problems\":[\"first\\n\\tsecond\"]}");

        Assert.Contains("first", message);
        Assert.Contains("second", message);
        Assert.Contains('\n', message);
    }

    /// <summary>
    /// An uncapped body scrolls the real error out of view, which is its own way of hiding
    /// something.
    /// </summary>
    [Fact]
    public async Task A_very_long_message_is_cut_short()
    {
        var message = await MessageFor($$"""{"error":"{{new string('x', 50_000)}}"}""");

        Assert.True(message.Length < 1_000, $"message was {message.Length} characters");
    }
}
