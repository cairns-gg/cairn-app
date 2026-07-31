using System.Diagnostics;
using System.IO;

namespace Cairn.App;

/// <summary>Hands a folder to the machine's file manager.</summary>
public static class Files
{
    /// <summary>
    /// Best-effort, like Browser.Open: failing to open a folder is a disappointment, not
    /// an error worth interrupting anyone over. Returns whether it started something.
    ///
    /// Directories only, and only ones that exist — UseShellExecute would otherwise happily
    /// run whatever an arbitrary path points at.
    /// </summary>
    public static bool OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;

        try
        {
            using var process = Process.Start(new ProcessStartInfo(Path.GetFullPath(path))
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
