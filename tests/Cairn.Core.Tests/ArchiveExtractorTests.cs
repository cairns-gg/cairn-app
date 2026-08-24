using System.Formats.Tar;
using System.IO.Compression;
using Cairn.Core;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Unpacking the archives Cairn downloads.
///
/// The case that matters is the one nothing here used to cover: every installer downloads
/// to <c>&lt;name&gt;.partial</c> and extracts *that*, so the name reaching the extractor
/// never ends in ".zip". Deciding the format by suffix therefore sent zips down the tarball
/// path — invisible on macOS and Linux, where every artifact is a tarball and the wrong
/// branch is the right one, and fatal on Windows, where it meant no private .NET runtime
/// could ever be installed.
/// </summary>
public class ArchiveExtractorTests : IDisposable
{
    private readonly string _root;

    public ArchiveExtractorTests()
    {
        // Beside the test assembly rather than in the temp directory, which on macOS is
        // reached through /var -> /private/var. TarFile compares the destination's real
        // path against the path it was handed, and when a symlink makes those differ it
        // fails inside its own directory-escape check — a fixture problem that reads as a
        // malicious archive. Nothing Cairn extracts into is behind a symlink.
        _root = Path.Combine(
            AppContext.BaseDirectory, "archive-tests", Guid.NewGuid().ToString("n")[..8]);

        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>A zip holding one file, written under whatever name is asked for.</summary>
    private string Zip(string name)
    {
        var content = Path.Combine(_root, "src");
        Directory.CreateDirectory(content);
        File.WriteAllText(Path.Combine(content, "dotnet.exe"), "binary");

        var path = Path.Combine(_root, name);
        ZipFile.CreateFromDirectory(content, path);
        Directory.Delete(content, recursive: true);

        return path;
    }

    /// <summary>The same, as a gzipped tar — what every non-Windows artifact is.</summary>
    private string TarGz(string name)
    {
        var content = Path.Combine(_root, "src");
        Directory.CreateDirectory(content);
        File.WriteAllText(Path.Combine(content, "dotnet"), "binary");

        var path = Path.Combine(_root, name);

        using (var fs = File.Create(path))
        using (var gz = new GZipStream(fs, CompressionMode.Compress))
            TarFile.CreateFromDirectory(content, gz, includeBaseDirectory: false);

        Directory.Delete(content, recursive: true);
        return path;
    }

    [Fact]
    public async Task A_zip_downloaded_as_partial_still_unpacks_as_a_zip()
    {
        // The regression. This is exactly what DotnetRuntimeInstaller hands over: the
        // release's own filename with ".partial" on the end, which is not ".zip".
        var archive = Zip("dotnet-sdk-10.0.400-win-x64.zip.partial");
        var into = Path.Combine(_root, "out");

        await ArchiveExtractor.ExtractAsync(archive, into);

        Assert.True(File.Exists(Path.Combine(into, "dotnet.exe")));
    }

    [Fact]
    public async Task A_tarball_downloaded_as_partial_still_unpacks()
    {
        // The branch that used to be reached by falling through rather than by being
        // chosen — right by luck on two platforms out of three, and worth pinning now that
        // it is chosen deliberately.
        var archive = TarGz("dotnet-runtime-10.0.10-osx-arm64.tar.gz.partial");
        var into = Path.Combine(_root, "out");

        await ArchiveExtractor.ExtractAsync(archive, into);

        Assert.True(File.Exists(Path.Combine(into, "dotnet")));
    }

    [Fact]
    public async Task Both_still_unpack_under_their_real_names()
    {
        var zip = Path.Combine(_root, "zipout");
        var tar = Path.Combine(_root, "tarout");

        await ArchiveExtractor.ExtractAsync(Zip("sdk.zip"), zip);
        await ArchiveExtractor.ExtractAsync(TarGz("runtime.tar.gz"), tar);

        Assert.True(File.Exists(Path.Combine(zip, "dotnet.exe")));
        Assert.True(File.Exists(Path.Combine(tar, "dotnet")));
    }

    [Fact]
    public async Task Something_that_is_neither_says_so()
    {
        // A truncated or redirected download is the real case. It used to reach GZipStream
        // and come back as "the archive entry was compressed using an unsupported
        // compression method", which sounds like a problem with the archive's contents
        // rather than with having got the wrong bytes.
        var path = Path.Combine(_root, "dotnet-sdk-win-x64.zip.partial");
        File.WriteAllText(path, "<html>404</html>");

        var thrown = await Assert.ThrowsAsync<InvalidDataException>(
            () => ArchiveExtractor.ExtractAsync(path, Path.Combine(_root, "out")));

        Assert.Contains("dotnet-sdk-win-x64.zip.partial", thrown.Message);
    }

    [Fact]
    public async Task A_file_too_short_to_identify_says_so_rather_than_throwing_something_else()
    {
        var path = Path.Combine(_root, "empty.zip.partial");
        File.WriteAllBytes(path, [0x50, 0x4B]);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ArchiveExtractor.ExtractAsync(path, Path.Combine(_root, "out")));
    }

    [Fact]
    public void What_can_be_downloaded_is_still_asked_of_the_published_name()
    {
        // IsSupported answers a different question — "is this artifact one Cairn can
        // handle" — and is asked of names out of a release index, which are real ones.
        Assert.True(ArchiveExtractor.IsSupported("dotnet-sdk-10.0.400-win-x64.zip"));
        Assert.True(ArchiveExtractor.IsSupported("dotnet-runtime-10.0.10-linux-x64.tar.gz"));
        Assert.False(ArchiveExtractor.IsSupported("dotnet-sdk-10.0.400-win-x64.exe"));
    }
}
