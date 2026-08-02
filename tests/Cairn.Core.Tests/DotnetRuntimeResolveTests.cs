using System.Net;
using System.Text;
using Cairn.Core;
using Cairn.Core.Runtime;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Picking which file to download from the .NET release metadata.
///
/// Worth its own tests because getting it wrong is invisible on any machine that already
/// has .NET: the installer only runs when nothing is installed, so the one place this
/// breaks is a fresh machine — which is every machine that needs it.
/// </summary>
public class DotnetRuntimeResolveTests
{
    /// <summary>
    /// The shape Microsoft actually publishes, with the file order they actually use: the
    /// apphost pack is listed before the runtime for every rid.
    /// </summary>
    private sealed class Metadata : HttpMessageHandler
    {
        private const string Index = """
            {"releases-index":[
              {"channel-version":"10.0","releases.json":"https://example.test/10/releases.json"},
              {"channel-version":"8.0","releases.json":"https://example.test/8/releases.json"}]}
            """;

        private const string Releases = """
            {"releases":[{"runtime":{"version":"10.0.10","files":[
              {"name":"dotnet-apphost-pack-linux-x64.tar.gz","rid":"linux-x64",
               "url":"https://example.test/dotnet-apphost-pack-10.0.10-linux-x64.tar.gz","hash":"aa"},
              {"name":"dotnet-runtime-linux-x64.tar.gz","rid":"linux-x64",
               "url":"https://example.test/dotnet-runtime-10.0.10-linux-x64.tar.gz","hash":"bb"},
              {"name":"dotnet-apphost-pack-osx-arm64.tar.gz","rid":"osx-arm64",
               "url":"https://example.test/dotnet-apphost-pack-10.0.10-osx-arm64.tar.gz","hash":"cc"},
              {"name":"dotnet-runtime-osx-arm64.pkg","rid":"osx-arm64",
               "url":"https://example.test/dotnet-runtime-10.0.10-osx-arm64.pkg","hash":"dd"},
              {"name":"dotnet-runtime-osx-arm64.tar.gz","rid":"osx-arm64",
               "url":"https://example.test/dotnet-runtime-10.0.10-osx-arm64.tar.gz","hash":"ee"},
              {"name":"dotnet-apphost-pack-win-x64.zip","rid":"win-x64",
               "url":"https://example.test/dotnet-apphost-pack-10.0.10-win-x64.zip","hash":"ff"},
              {"name":"dotnet-runtime-win-x64.exe","rid":"win-x64",
               "url":"https://example.test/dotnet-runtime-10.0.10-win-x64.exe","hash":"gg"},
              {"name":"dotnet-runtime-win-x64.zip","rid":"win-x64",
               "url":"https://example.test/dotnet-runtime-10.0.10-win-x64.zip","hash":"hh"}]}}]}
            """;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    request.RequestUri!.AbsoluteUri.Contains("releases-index") ? Index : Releases,
                    Encoding.UTF8, "application/json"),
            });
    }

    private static DotnetRuntimeInstaller Installer() =>
        new(new HttpClient(new Metadata()),
            new RuntimeStore(Path.Combine(Path.GetTempPath(), "cairn-resolve-test")));

    [Theory]
    [InlineData("linux-x64", "dotnet-runtime-10.0.10-linux-x64.tar.gz")]
    [InlineData("osx-arm64", "dotnet-runtime-10.0.10-osx-arm64.tar.gz")]
    [InlineData("win-x64", "dotnet-runtime-10.0.10-win-x64.zip")]
    public async Task The_runtime_is_chosen_and_not_the_apphost_pack(string rid, string expected)
    {
        var release = await Installer().ResolveAsync(10, rid);

        // The apphost pack is listed first for every rid and unpacks perfectly well — it
        // is 5 MB of build-time templates with no `dotnet` in it, so taking the first
        // supported archive got as far as "extracted" and then reported no runtime found.
        Assert.EndsWith(expected, release.Url);
        Assert.DoesNotContain("apphost", release.Url);
        Assert.Equal("10.0.10", release.Version);
    }

    [Fact]
    public async Task An_installer_only_archive_is_passed_over_for_one_we_can_unpack()
    {
        // win-x64 lists .exe before .zip. The .exe is the real runtime, and nothing here
        // can unpack one.
        var release = await Installer().ResolveAsync(10, "win-x64");

        Assert.EndsWith(".zip", release.Url);
    }

    [Fact]
    public async Task A_rid_with_nothing_published_says_so()
    {
        var problem = await Assert.ThrowsAsync<DotnetRuntimeException>(
            () => Installer().ResolveAsync(10, "linux-riscv64"));

        Assert.Contains("linux-riscv64", problem.Message);
    }
}
