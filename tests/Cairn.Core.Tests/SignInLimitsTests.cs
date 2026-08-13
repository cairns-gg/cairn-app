using Cairn.Core.Cairns;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// How long a sign-in will wait when the server decides.
///
/// The device flow's expires_in and interval both come from the server, and both bound a
/// loop the CLI runs in the foreground with somebody watching it. expires_in is an int, so
/// a hostile or simply broken one names roughly sixty-eight years — which is not a long
/// wait, it is a program that has stopped.
/// </summary>
public class SignInLimitsTests
{
    /// <summary>Never answers the poll, so the only thing that ends this is the deadline.</summary>
    private sealed class NeverApproves : HttpMessageHandler
    {
        public int Polls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage r, CancellationToken ct)
        {
            Polls++;
            return Task.FromResult(new HttpResponseMessage(
                System.Net.HttpStatusCode.PreconditionRequired));
        }
    }

    [Fact]
    public async Task An_absurd_expiry_does_not_become_an_absurd_wait()
    {
        var handler = new NeverApproves();
        var client = new CairnsClient(new HttpClient(handler), "https://cairns.test");

        // int.MaxValue seconds is about sixty-eight years.
        var flow = new DeviceFlow("device", "CODE", "https://cairns.test/device",
            ExpiresIn: int.MaxValue, Interval: 1);

        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Cancelled rather than expired is the honest outcome for a two-second budget — the
        // point is that the deadline it computed is a real DateTimeOffset rather than one
        // that overflowed or landed beyond the end of time.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.AwaitSignInAsync(flow, ct: cancel.Token));

        Assert.True(handler.Polls > 0, "it should have polled at least once");
    }

    /// <summary>
    /// A server asking to be polled once a fortnight is asking for the same hang more
    /// slowly, so the interval is clamped as well as the deadline.
    /// </summary>
    [Fact]
    public async Task An_absurd_interval_does_not_stall_the_first_poll()
    {
        var handler = new NeverApproves();
        var client = new CairnsClient(new HttpClient(handler), "https://cairns.test");

        var flow = new DeviceFlow("device", "CODE", "https://cairns.test/device",
            ExpiresIn: 600, Interval: int.MaxValue);

        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.AwaitSignInAsync(flow, ct: cancel.Token));

        // The first ask happens before any waiting, so an absurd interval delays the
        // second poll and not the whole flow. Exactly one, because the clamped wait is
        // longer than this test's patience — which is the assertion: it asked, then waited.
        Assert.Equal(1, handler.Polls);
    }

    [Fact]
    public async Task A_cancelled_sign_in_stops_promptly()
    {
        var client = new CairnsClient(new HttpClient(new NeverApproves()), "https://cairns.test");
        var flow = new DeviceFlow("device", "CODE", "https://cairns.test/device", 600, 1);

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var started = DateTimeOffset.UtcNow;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.AwaitSignInAsync(flow, ct: cancel.Token));

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(10));
    }
}
