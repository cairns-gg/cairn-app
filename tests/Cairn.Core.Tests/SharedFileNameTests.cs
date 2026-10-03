using System.Net;
using System.Text;
using Cairn.Core.ModDb;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Two mods that would install under the same file name each get one of their own.
///
/// A direct link names its file after the URL's last segment, so two different mods at
/// …/alpha/release.zip and …/beta/release.zip were both written to Mods/release.zip. The
/// second replaced the first, the lock kept two entries with two hashes for one file, and
/// sync reported success.
/// </summary>
public class SharedFileNameTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cairn-shared-name-").FullName;

    private string ModsDir => Path.Combine(_root, "Mods");
    private string LockPath => Path.Combine(_root, "pack.lock.json");

    public SharedFileNameTests() => Directory.CreateDirectory(ModsDir);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>Serves a different mod at each address, and counts what is downloaded.</summary>
    private sealed class Serving(Dictionary<string, string> moddb, Dictionary<string, byte[]> files)
        : HttpMessageHandler
    {
        public int Downloads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var url = r.RequestUri!.ToString();

            if (url.Contains("/api/mod/") && moddb.TryGetValue(url[(url.LastIndexOf('/') + 1)..], out var json))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                });

            if (files.TryGetValue(url, out var bytes))
            {
                Downloads++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static byte[] Mod(string id) => FailedInstallTests.Zip($$"""{"modid":"{{id}}","version":"1.0.0"}""");

    private static string ModDbEntry(string id, string file, string url) => $$$"""
        {"statuscode":"200","mod":{"modid":1,"assetid":2,"name":"{{{id}}}","urlalias":"{{{id}}}","side":"both",
         "releases":[{"releaseid":1,"fileid":1,"modidstr":"{{{id}}}","modversion":"1.0.0",
           "filename":"{{{file}}}","mainfile":"{{{url}}}","tags":["1.22.5"]}]}}
        """;

    private Task<SyncReport> Sync(Serving http, params PackMod[] mods) =>
        new PackSyncer(new ModDbClient(new HttpClient(http)), new HttpClient(http)).SyncAsync(
            new PackManifest { Id = "demo", GameVersion = "1.22.5", Mods = [.. mods] },
            ModsDir, LockPath);

    /// <summary>Every lock entry's file is there, and holds the bytes the entry says.</summary>
    private async Task AssertEachModHasItsOwnFile()
    {
        var locked = PackLock.Load(LockPath)!.Mods;

        Assert.Equal(locked.Count, locked.Select(m => m.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var entry in locked)
        {
            var path = Path.Combine(ModsDir, entry.FileName);
            Assert.True(File.Exists(path), $"{entry.ModId} has no {entry.FileName}");
            Assert.Equal(entry.Sha256, await ModUrl.Sha256Async(path, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Two_links_ending_in_the_same_name_install_two_files()
    {
        var http = new Serving([], new()
        {
            ["https://files.example/alpha/release.zip"] = Mod("alpha"),
            ["https://files.example/beta/release.zip"] = Mod("beta"),
        });

        var mods = new[]
        {
            new PackMod { ModId = "alpha", Url = "https://files.example/alpha/release.zip" },
            new PackMod { ModId = "beta", Url = "https://files.example/beta/release.zip" },
        };

        Assert.False((await Sync(http, mods)).Failed);
        Assert.Equal(2, Directory.EnumerateFiles(ModsDir).Count());
        await AssertEachModHasItsOwnFile();

        // And the names hold: the next sync finds both where it left them.
        var downloads = http.Downloads;
        Assert.False((await Sync(http, mods)).Failed);
        Assert.Equal(downloads, http.Downloads);
        await AssertEachModHasItsOwnFile();
    }

    /// <summary>
    /// A pack this already happened to: one release.zip holding beta, and a lock that names
    /// it for both. The next sync puts alpha back and gives beta a name of its own.
    /// </summary>
    [Fact]
    public async Task A_pack_that_lost_a_mod_this_way_gets_it_back()
    {
        var alpha = Mod("alpha");
        var beta = Mod("beta");

        File.WriteAllBytes(Path.Combine(ModsDir, "release.zip"), beta);
        new PackLock
        {
            GameVersion = "1.22.5",
            Mods =
            [
                new LockedMod
                {
                    ModId = "alpha", Version = "1.0.0", FileName = "release.zip", FromUrl = true,
                    Url = "https://files.example/alpha/release.zip", Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(alpha)),
                },
                new LockedMod
                {
                    ModId = "beta", Version = "1.0.0", FileName = "release.zip", FromUrl = true,
                    Url = "https://files.example/beta/release.zip", Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(beta)),
                },
            ],
        }.Save(LockPath);

        var http = new Serving([], new()
        {
            ["https://files.example/alpha/release.zip"] = alpha,
            ["https://files.example/beta/release.zip"] = beta,
        });

        var report = await Sync(http,
            new PackMod { ModId = "alpha", Url = "https://files.example/alpha/release.zip" },
            new PackMod { ModId = "beta", Url = "https://files.example/beta/release.zip" });

        Assert.False(report.Failed);
        Assert.Equal(2, Directory.EnumerateFiles(ModsDir).Count());
        await AssertEachModHasItsOwnFile();
    }

    [Fact]
    public async Task Two_moddb_releases_with_the_same_filename_install_two_files()
    {
        var http = new Serving(new()
        {
            ["alpha"] = ModDbEntry("alpha", "release.zip", "https://moddbcdn.vintagestory.at/a/release.zip"),
            ["beta"] = ModDbEntry("beta", "release.zip", "https://moddbcdn.vintagestory.at/b/release.zip"),
        }, new()
        {
            ["https://moddbcdn.vintagestory.at/a/release.zip"] = Mod("alpha"),
            ["https://moddbcdn.vintagestory.at/b/release.zip"] = Mod("beta"),
        });

        Assert.False((await Sync(http, new PackMod { ModId = "alpha" }, new PackMod { ModId = "beta" })).Failed);
        Assert.Equal(2, Directory.EnumerateFiles(ModsDir).Count());
        await AssertEachModHasItsOwnFile();
    }
}
