using System.Net;
using System.Text;
using Cairn.Core.ModDb;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Where a freshly-resolved mod is allowed to be downloaded from.
///
/// The host check used to sit only on the branch that reused a URL out of the lockfile,
/// which left the resolve path — the branch that one deliberately falls back to — taking
/// whatever ModDB's JSON named, over any scheme, from any host. Clearing locations out of
/// imported locks made resolve the path every shared pack takes, so the guarded branch was
/// carrying the least attacker-influenced input and the unguarded one the most.
///
/// These pin the check to the download rather than to a branch. A mod that fails it fails
/// on its own — the rest of the pack still syncs, because a stale host list must not stop
/// somebody playing every pack they have.
/// </summary>
public class ResolvedDownloadTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-resolved-" + Guid.NewGuid().ToString("n")[..8]);

    private string ModsDir => Path.Combine(_root, "pack", "Mods");
    private string LockPath => Path.Combine(_root, "pack", "pack.lock.json");

    public ResolvedDownloadTests() => Directory.CreateDirectory(ModsDir);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private const string Cdn = "https://moddbcdn.vintagestory.at";

    /// <summary>
    /// ModDB's API with the one field under test — <c>mainfile</c> — under the caller's
    /// control, which is what a compromised or redirected API response looks like.
    /// </summary>
    private sealed class Stub(string mainFileFor2) : HttpMessageHandler
    {
        public List<string> Downloaded { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var url = r.RequestUri!.ToString();

            if (url.Contains("/api/mod/"))
            {
                var id = url[(url.LastIndexOf('/') + 1)..];
                var main = id == "beta" ? mainFileFor2 : $"{Cdn}/{id}_1.0.0.zip";
                var body = $$"""
                {"statuscode":"200","mod":{
                  "modid":1,"assetid":2,"name":"{{id}}","urlalias":"{{id}}","side":"client",
                  "releases":[
                    {"releaseid":1,"fileid":1,"modidstr":"{{id}}","modversion":"1.0.0",
                     "filename":"{{id}}_1.0.0.zip",
                     "mainfile":"{{main}}","tags":["1.22.5"]}
                  ]
                }
                }
                """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }

            Downloaded.Add(url);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("a mod zip")),
            });
        }
    }

    private (PackSyncer Syncer, Stub Handler) Make(string mainFile)
    {
        var handler = new Stub(mainFile);
        var http = new HttpClient(handler);
        return (new PackSyncer(new ModDbClient(http), http), handler);
    }

    /// <summary>Two mods, so a refusal of one can be told from a failure of the run.</summary>
    private static PackManifest Pack() => new()
    {
        Id = "anego",
        GameVersion = "1.22.5",
        Mods = [new PackMod { ModId = "alpha" }, new PackMod { ModId = "beta" }],
    };

    [Theory]
    [InlineData("https://attacker.example/payload.zip")]
    [InlineData("https://moddbcdn.vintagestory.at.attacker.example/payload.zip")]
    [InlineData("https://moddbcdn.vintagestory.at@attacker.example/payload.zip")]
    public async Task A_resolved_download_from_an_unknown_host_is_refused(string mainFile)
    {
        var (syncer, handler) = Make(mainFile);
        var report = await syncer.SyncAsync(Pack(), ModsDir, LockPath);

        Assert.DoesNotContain(handler.Downloaded, u => u.Contains("attacker.example"));
        Assert.Contains(report.Steps, s => s.Action == SyncAction.Failed && s.ModId == "beta");
        Assert.False(File.Exists(Path.Combine(ModsDir, "beta_1.0.0.zip")));
    }

    /// <summary>
    /// The threat model names a network attacker on plaintext transport, and this was the
    /// one sink in the sync path that accepted an http:// URL.
    /// </summary>
    [Fact]
    public async Task A_resolved_download_over_plain_http_is_refused()
    {
        var (syncer, handler) = Make("http://moddbcdn.vintagestory.at/beta_1.0.0.zip");
        var report = await syncer.SyncAsync(Pack(), ModsDir, LockPath);

        Assert.DoesNotContain(handler.Downloaded, u => u.StartsWith("http://"));
        Assert.Contains(report.Steps, s => s.Action == SyncAction.Failed && s.ModId == "beta");
    }

    /// <summary>
    /// The refusal is per-mod. A pack is not made unplayable because one of its mods
    /// resolved to somewhere unfamiliar — which is as likely to mean the host list has
    /// gone stale as it is to mean an attack.
    /// </summary>
    [Fact]
    public async Task The_rest_of_the_pack_still_installs()
    {
        var (syncer, _) = Make("https://attacker.example/payload.zip");
        await syncer.SyncAsync(Pack(), ModsDir, LockPath);

        Assert.True(File.Exists(Path.Combine(ModsDir, "alpha_1.0.0.zip")));
    }

    /// <summary>The ordinary case still works, or none of the above means anything.</summary>
    [Fact]
    public async Task A_download_from_the_CDN_is_installed()
    {
        var (syncer, _) = Make($"{Cdn}/beta_1.0.0.zip");
        var report = await syncer.SyncAsync(Pack(), ModsDir, LockPath);

        Assert.True(File.Exists(Path.Combine(ModsDir, "beta_1.0.0.zip")));
        Assert.DoesNotContain(report.Steps, s => s.Action == SyncAction.Failed);
    }

    /// <summary>
    /// download.php is allowlisted precisely because it redirects to the CDN, so a resolve
    /// naming it must still be installable.
    /// </summary>
    [Fact]
    public async Task The_redirecting_download_endpoint_is_still_allowed()
    {
        var (syncer, _) = Make("https://mods.vintagestory.at/download.php?fileid=1");
        var report = await syncer.SyncAsync(Pack(), ModsDir, LockPath);

        Assert.DoesNotContain(report.Steps, s => s.Action == SyncAction.Failed);
    }
}
