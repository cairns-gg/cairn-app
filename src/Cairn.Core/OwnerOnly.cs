namespace Cairn.Core;

/// <summary>
/// Writing files that hold a credential.
///
/// Three files here carry one: <c>auth.json</c> is the cairns.gg token, <c>session.json</c>
/// is the Vintage Story login, and every pack's <c>clientsettings.json</c> receives that
/// same login at launch so one sign-in reaches all of them. Two of the three had no mode
/// set at all, so they landed at 0644 under an ordinary umask, inside a directory created
/// at 0755 — readable by any other account on the machine, indefinitely, on a platform
/// where home directories are world-traversable by default.
///
/// The mode is applied when the file is created rather than after it is written. That
/// distinction is the whole point: <c>File.WriteAllText</c> opens at 0666 masked by the
/// umask, so narrowing afterwards leaves a window in which the credential is already on
/// disk and readable, and a descriptor opened during that window keeps its access across
/// the change. For a token, that window is the entire protection.
///
/// All of it is a no-op on Windows, where these APIs do nothing and what keeps other
/// standard users out is the profile's own ACL.
/// </summary>
public static class OwnerOnly
{
    private const UnixFileMode FileMode600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode DirectoryMode700 =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Writes text to a file only its owner can read, creating it that way.
    ///
    /// The trailing <see cref="File.SetUnixFileMode(string, UnixFileMode)"/> is not
    /// redundant: a create mode applies only when the file is actually created, so a file
    /// left behind at 0644 by an older build would keep that mode for ever otherwise.
    /// </summary>
    public static void WriteText(string path, string contents)
    {
        var options = new FileStreamOptions
        {
            Mode = System.IO.FileMode.Create,
            Access = FileAccess.Write,
        };

        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = FileMode600;

        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(contents);
        }

        Tighten(path);
    }

    /// <summary>Narrows an existing file, for one written before this was applied.</summary>
    public static void Tighten(string path)
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            File.SetUnixFileMode(path, FileMode600);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or PlatformNotSupportedException)
        {
            // Best effort. A file whose mode cannot be narrowed is not a reason to refuse
            // to write it — the alternative is a launcher that will not sign in.
        }
    }

    /// <summary>
    /// Creates a directory only its owner can enter.
    ///
    /// Worth doing as well as narrowing the files: containment is what covers the things
    /// nobody thought to narrow, including whatever gets written under here next.
    /// </summary>
    public static void CreateDirectory(string path)
    {
        Directory.CreateDirectory(path);

        if (OperatingSystem.IsWindows()) return;

        try
        {
            File.SetUnixFileMode(path, DirectoryMode700);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or PlatformNotSupportedException)
        {
        }
    }
}
