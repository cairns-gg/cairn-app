namespace Cairn.Core;

/// <summary>
/// Whether a string is a filename and nothing else.
///
/// The rule three separate subsystems need before combining a remote string with a
/// directory: mods (<see cref="Packs.ModFileName"/>), the game catalogue
/// (<see cref="Games.GameCatalog"/>) and the .NET runtime index
/// (<see cref="Runtime.DotnetRuntimeInstaller"/>). All three take a name out of somebody
/// else's JSON and build a path with it, and each one that gets this wrong is an
/// arbitrary-path write of bytes that are about to be unpacked or executed.
///
/// It lives here, on its own, because it was previously written once inside
/// <see cref="Packs.PackSyncer"/> and then promoted to <see cref="Packs.ModFileName"/> —
/// where it was correct, and where the two subsystems that also needed it could not
/// reasonably reach for something called "ModFileName". A rule kept next to one of its
/// callers is a rule the others reimplement or forget. The kinds of file each subsystem
/// accepts differ and stay with that subsystem; "is a bare name at all" does not differ
/// and belongs here.
/// </summary>
public static class BareFileName
{
    /// <summary>
    /// Whether this is a filename and nothing else — no directory part, nothing rooted,
    /// and nothing that means somewhere other than where it reads.
    ///
    /// Rejects rather than sanitises. A name that tries to escape is reported to whoever
    /// can act on it instead of quietly becoming a different name, which is the difference
    /// between "this release cannot be installed and here is why" and a file appearing
    /// somewhere nobody looked.
    /// </summary>
    public static bool IsBare(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        // GetFileName strips any directory part; if that changed the string, the original
        // was carrying one. Also catches "..", rooted paths and both separators.
        if (Path.GetFileName(name) != name || name is "." or "..") return false;
        if (name.AsSpan().IndexOfAny('/', '\\') >= 0) return false;
        if (Path.IsPathRooted(name)) return false;

        // Windows reads "mod.zip:hidden" as an alternate data stream, which File.Create
        // will happily write and which neither a sweep nor a directory listing shows. The
        // colon survives GetFileName unchanged, so it has to be named on its own.
        if (name.Contains(':')) return false;

        // "COM1.zip" and "NUL.dll" are the serial port and the bit bucket, not files:
        // Win32 resolves a reserved device name whatever extension follows it, so
        // File.Create opens the device and the write goes nowhere recoverable. Checked on
        // every platform rather than under OperatingSystem.IsWindows, because a lock or a
        // manifest written on one machine is read on another and a name that is a hazard
        // anywhere should be refused everywhere — a rule that changes by host is a rule
        // that disagrees with itself about the same document.
        var dot = name.IndexOf('.');

        return !IsReservedDeviceName(dot >= 0 ? name.AsSpan(0, dot) : name.AsSpan());
    }

    /// <summary>
    /// The MS-DOS device names Win32 still resolves ahead of the filesystem. Trailing
    /// spaces are stripped by the same path parser, so "COM1 .zip" reaches the device too.
    /// </summary>
    private static readonly string[] DeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    private static bool IsReservedDeviceName(ReadOnlySpan<char> stem)
    {
        stem = stem.TrimEnd(' ');

        if (stem.Length is < 3 or > 4) return false;

        foreach (var reserved in DeviceNames)
            if (stem.Equals(reserved, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }
}
