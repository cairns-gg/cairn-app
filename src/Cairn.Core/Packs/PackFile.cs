namespace Cairn.Core.Packs;

/// <summary>
/// A <c>.cairn</c> file handed to Cairn by the operating system — double-clicked in a file
/// manager, or opened from a download (cairns-gg/cairn-app#2).
///
/// The extension says what the file is to somebody who was sent one; this is the other half,
/// which makes opening it do something. <see cref="PackLinkHandler"/> tells Windows and Linux
/// that the type is Cairn's, and the macOS bundle declares it in Info.plist.
/// </summary>
public static class PackFile
{
    /// <summary>The type a Linux desktop knows the files by.</summary>
    public const string MimeType = "application/x-cairn-pack";

    /// <summary>The Windows registry's name for the type the extension points at.</summary>
    public const string WindowsProgId = "Cairn.Pack";

    /// <summary>
    /// The local path an argument names, when it is an existing pack file; otherwise null.
    ///
    /// A plain path from Windows and from most Linux launchers, and a <c>file://</c> URL from
    /// the ones that take a desktop entry's <c>%u</c> literally — the spec allows either, and
    /// the same entry also answers <c>cairn://</c> links, which are not files at all.
    /// Required to exist, so an argument that merely ends in .cairn is not taken for one.
    /// </summary>
    public static string? PathFrom(string argument)
    {
        if (string.IsNullOrWhiteSpace(argument)) return null;

        string path;
        if (argument.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(argument, UriKind.Absolute, out var uri) || !uri.IsFile) return null;
            path = uri.LocalPath;
        }
        else path = argument;

        if (!path.EndsWith(PackBundle.FileExtension, StringComparison.OrdinalIgnoreCase)) return null;

        return File.Exists(path) ? Path.GetFullPath(path) : null;
    }

    /// <summary>The first argument that is a pack file, if any.</summary>
    public static string? FromArguments(IEnumerable<string> args) =>
        args.Select(PathFrom).FirstOrDefault(p => p is not null);
}
