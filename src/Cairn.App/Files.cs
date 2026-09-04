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
    /// How to start a platform's file manager on one file.
    /// </summary>
    /// <param name="Args">
    /// The arguments as argv, for a file manager that takes ordinary ones. The form to
    /// prefer: a Unix path may contain a space, a quote or a backslash, and argv is the one
    /// shape that needs no escaping rules at all.
    /// </param>
    /// <param name="CommandLine">
    /// A command line to hand over verbatim instead, for the platform whose file manager
    /// cannot be addressed through argv. Exactly one of the two is set.
    /// </param>
    public sealed record RevealPlan(string Exe, string[]? Args = null, string? CommandLine = null);

    /// <summary>
    /// How to ask a platform's file manager to open a folder with one file picked out in
    /// it, or null where there is no way to ask.
    ///
    /// A parameter rather than the machine's own answer, so all three can be checked from
    /// one host — see <see cref="HostOs"/>. This one earns it twice over: the arguments are
    /// unlike each other, and Explorer's are unlike anything else in the world.
    /// </summary>
    public static RevealPlan? RevealCommand(string path, HostOs os) => os switch
    {
        // The switch is "/select," — a comma, no space — and the path belongs to that same
        // argument, with the quotes *inside* it.
        //
        // Which is why this is a command line and not argv. ProcessStartInfo.ArgumentList
        // quotes an argument that contains a space, wrapping the whole of it: a path under
        // C:\Users\Dave Smith came out as "/select,C:\Users\Dave Smith\...", with the quote
        // ahead of the switch. Explorer does not parse that — it drops the selection and
        // opens Documents. Measured on Windows rather than reasoned about, because a path
        // with no space is quoted by nobody and works either way, which is exactly what
        // keeps this hidden until it reaches somebody whose user name has one.
        //
        // Safe to quote by hand only because Windows forbids a quote in a filename; the
        // same trick on a Unix path would be a bug, hence argv below.
        HostOs.Windows => new RevealPlan("explorer.exe", CommandLine: $"/select,\"{path}\""),

        HostOs.MacOs => new RevealPlan("open", Args: ["-R", path]),

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

        if (RevealCommand(full, os ?? Host.This) is { } plan)
        {
            try
            {
                // Not UseShellExecute: these are programs with arguments, and the argument
                // list is exactly what the shell verb has nowhere to put.
                var info = new ProcessStartInfo(plan.Exe) { UseShellExecute = false };

                // Either, never both: touching one after the other throws
                // "Only one of Arguments or ArgumentList may be used", and this catch would
                // turn that into a silent fall back to the plain folder.
                if (plan.CommandLine is not null) info.Arguments = plan.CommandLine;
                else foreach (var arg in plan.Args ?? []) info.ArgumentList.Add(arg);

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
