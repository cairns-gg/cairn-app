using System.Net.Sockets;
using System.Text;

namespace Cairn.Server;

/// <summary>
/// The way a console command reaches a server nobody has a terminal on.
///
/// The server reads its console from stdin — which is why the shipped init script wraps it
/// in screen — and a service started by systemd has no stdin worth the name. So the running
/// process listens on a Unix socket beside its pack and writes whatever arrives to the
/// server's stdin, and "cairn-server command" is a client of that socket. No screen, no
/// second supervisor, and journald still gets the output because the server's stdout is
/// never redirected.
///
/// A Unix socket rather than a FIFO because it answers the question that matters: connect
/// fails when nothing is listening, so "the server is not running" is a result rather than
/// a write that blocks or vanishes. It is created with owner-only permissions — anything
/// that can write here can run any server command, including ones that grant privileges.
/// </summary>
public static class ServerConsole
{
    /// <summary>Accepts connections and hands each line to <paramref name="onCommand"/>.</summary>
    public static async Task ListenAsync(
        string socketPath, Func<string, Task> onCommand, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);

        // A socket left behind by a process that did not shut down cleanly. Removing it is
        // safe here only because a second server for the same pack is refused before this
        // point — otherwise this is how two servers end up sharing one world directory.
        if (File.Exists(socketPath)) File.Delete(socketPath);

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(4);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var client = await listener.AcceptAsync(ct).ConfigureAwait(false);
                using var stream = new NetworkStream(client, ownsSocket: false);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        await onCommand(line).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down; the finally below is the whole cleanup.
        }
        finally
        {
            try { if (File.Exists(socketPath)) File.Delete(socketPath); }
            catch (IOException) { }
        }
    }

    /// <summary>Sends one command, or reports that nothing is listening.</summary>
    public static async Task<bool> SendAsync(string socketPath, string command, CancellationToken ct = default)
    {
        if (!File.Exists(socketPath)) return false;

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            // The file outlives the process that made it, so its existence proves nothing.
            return false;
        }

        var bytes = Encoding.UTF8.GetBytes(command.TrimEnd('\n') + "\n");
        await socket.SendAsync(bytes, SocketFlags.None, ct).ConfigureAwait(false);
        return true;
    }
}
