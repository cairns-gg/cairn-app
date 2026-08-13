using Cairn.Core.Runtime;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// What the .NET release index is allowed to talk this machine into downloading.
///
/// The same pair of defects the game installer had, in the sibling that fetches the
/// runtime the game then executes — a remote document choosing both where bytes land and
/// where they come from. They were fixed for GameInstaller and not for this file, which is
/// how a rule ends up applied to one of the two places that needed it.
/// </summary>
public class RuntimeDownloadTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-runtime-dl-" + Guid.NewGuid().ToString("n")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>Fails the test rather than the request: nothing here should reach the network.</summary>
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            throw new Xunit.Sdk.XunitException($"asked for {r.RequestUri} — it should have refused first");
    }

    private DotnetRuntimeInstaller Installer() =>
        new(new HttpClient(new NoNetwork()), new RuntimeStore(_root));

    private static DotnetRuntimeRelease Release(string url) =>
        new("10.0.0", "linux-x64", url, Sha512: new string('a', 128));

    [Theory]
    [InlineData("https://attacker.example/dotnet-runtime-10.0.0-linux-x64.tar.gz")]
    [InlineData("https://builds.dotnet.microsoft.com.attacker.example/dotnet-runtime.tar.gz")]
    [InlineData("https://builds.dotnet.microsoft.com@attacker.example/dotnet-runtime.tar.gz")]
    [InlineData("http://builds.dotnet.microsoft.com/dotnet-runtime-10.0.0-linux-x64.tar.gz")]
    public async Task A_runtime_from_anywhere_else_is_refused(string url)
    {
        var problem = await Assert.ThrowsAsync<DotnetRuntimeException>(
            () => Installer().InstallAsync(Release(url)));

        Assert.Contains("Refusing", problem.Message);
    }

    /// <summary>
    /// FileName is everything after the last '/' in the URL, so it cannot carry a forward
    /// slash — but it can carry backslashes, and on Windows Path.Combine honours those as
    /// a traversal.
    /// </summary>
    [Theory]
    [InlineData(@"https://builds.dotnet.microsoft.com/x/a\..\..\evil.exe")]
    [InlineData("https://builds.dotnet.microsoft.com/x/..")]
    [InlineData("https://builds.dotnet.microsoft.com/x/")]
    public async Task A_file_name_carrying_a_path_is_refused(string url)
    {
        var problem = await Assert.ThrowsAsync<DotnetRuntimeException>(
            () => Installer().InstallAsync(Release(url)));

        Assert.Contains("plain", problem.Message);
    }

    /// <summary>
    /// A missing hash is this file's own exception type rather than a bare
    /// InvalidOperationException, so cairn-server reports it as the runtime problem it is.
    /// </summary>
    [Fact]
    public async Task A_release_with_no_hash_is_refused()
    {
        var release = new DotnetRuntimeRelease(
            "10.0.0", "linux-x64",
            "https://builds.dotnet.microsoft.com/dotnet/Runtime/dotnet-runtime-linux-x64.tar.gz",
            Sha512: null);

        await Assert.ThrowsAsync<DotnetRuntimeException>(() => Installer().InstallAsync(release));
    }

    [Theory]
    [InlineData("https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.0/x.tar.gz", true)]
    [InlineData("https://download.visualstudio.microsoft.com/x.tar.gz", false)]
    [InlineData("https://download.microsoft.com/x.tar.gz", false)]
    [InlineData("", false)]
    public void The_host_list_is_the_one_channel_Cairn_can_ask_for(string url, bool allowed) =>
        Assert.Equal(allowed, DotnetRuntimeInstaller.IsKnownDownloadHost(url));
}
