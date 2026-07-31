using System.Diagnostics;

namespace Cairn.App;

/// <summary>Opens a link in whatever the machine considers its browser.</summary>
public static class Browser
{
    /// <summary>
    /// Best-effort: failing to open a page is a disappointment, not an error worth
    /// interrupting anyone over. Returns whether it managed to start something.
    /// </summary>
    public static bool Open(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        // Only ever http(s). These URLs are built from ModDB API responses, and
        // UseShellExecute would happily launch a file:// path or a registered handler.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        try
        {
            // UseShellExecute hands the URL to the OS, which is what picks the browser:
            // ShellExecute on Windows, "open" on macOS, xdg-open on Linux.
            using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true,
            });

            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception
                                      or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
