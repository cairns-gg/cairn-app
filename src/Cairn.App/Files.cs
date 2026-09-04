using System.Diagnostics;
using System.IO;
using Cairn.Core;

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

    /// <summary>
    /// How to ask a platform's file manager to open a folder with one file picked out in
    /// it, or null where there is no way to ask.
    ///
    /// A parameter rather than the machine's own answer, so all three can be checked from
    /// one host — see <see cref="HostOs"/>. This one earns it twice over: the arguments are
    /// unlike each other, and Explorer's are unlike anything else in the world. Its switch
    /// is <c>/select,</c> with a comma and no space, and the path is part of the same
    /// argument; <c>/select &lt;path&gt;</c> opens Documents instead, silently, which on a
    /// machine none of us runs is a bug nobody would find.
    /// </summary>
    public static (string Exe, string[] Args)? RevealCommand(string path, HostOs os) => os switch
    {
        HostOs.Windows => ("explorer.exe", ["/select," + path]),
        HostOs.MacOs => ("open", ["-R", path]),

        // No fork to make: freedesktop has nothing for "select this file", and the file
        // managers that do have something disagree on the flag. The folder is where the
        // caller falls back to, which is where the file is.
        _ => null,
    };

    /// <summary>
    /// Opens the folder a file lives in, with the file itself picked out where the platform
    /// can do that.
    ///
    /// Getting there is the point and the selection is the courtesy, so a platform with no
    /// way to select — and any failure of the one that has — still lands in the right
    /// folder rather than nowhere. Returns whether it started something.
    /// </summary>
    public static bool Reveal(string? path, HostOs? os = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

        var full = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(full);

        if (RevealCommand(full, os ?? Host.This) is { } command)
        {
            try
            {
                // Not UseShellExecute: these are programs with arguments, and the argument
                // list is exactly what the shell verb has nowhere to put.
                var info = new ProcessStartInfo(command.Exe) { UseShellExecute = false };
                foreach (var arg in command.Args) info.ArgumentList.Add(arg);

                using var process = Process.Start(info);
                return true;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception
                                          or InvalidOperationException or PlatformNotSupportedException)
            {
                // Fall through to the folder. A file manager that would not start with a
                // selection is still worth trying without one.
            }
        }

        return OpenFolder(folder);
    }
}
