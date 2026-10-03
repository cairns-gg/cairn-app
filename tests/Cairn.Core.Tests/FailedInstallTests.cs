using System.IO.Compression;
using System.Net;
using System.Text;
using Cairn.Core.ModDb;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// An install that fails keeps what was installed before it.
///
/// Every failure after a resolve used to drop the mod's lock entry. A checksum refusal was
/// forgotten by the next sync, which then took the bytes it had refused; and an update whose
/// download failed handed the working copy, and the library it pulled in, to the sweep.
/// </summary>
public class FailedInstallTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cairn-failed-install-").FullName;

    private string ModsDir => Path.Combine(_root, "Mods");
    private string LockPath => Path.Combine(_root, "pack.lock.json");

    public FailedInstallTests() => Directory.CreateDirectory(ModsDir);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>
    /// ModDB with two mods: olla, which requires lib. Its CDN can go down, or start serving
    /// different bytes under the same names — a release rebuilt behind its version number.
    /// </summary>
    private sealed class Stub : HttpMessageHandler
    {
        public string Newest { get; set; } = "1.0.0";
        public bool CdnDown { get; set; }
        public bool Rebuilt { get; set; }

        /// <summary>What a direct link serves; null serves an error page with a 200.</summary>
        public byte[]? AtLink { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var url = r.RequestUri!.ToString();

            if (url.Contains("/api/mod/"))
            {
                var id = url[(url.LastIndexOf('/') + 1)..];
                var versions = id == "olla" ? new[] { Newest, "1.0.0" }.Distinct() : ["1.0.0"];

                var releases = string.Join(",", versions.Select((v, i) => $$"""
                    {"releaseid":{{i + 1}},"fileid":{{i + 1}},"modidstr":"{{id}}","modversion":"{{v}}",
                     "filename":"{{id}}_{{v}}.zip",
                     "mainfile":"https://moddbcdn.vintagestory.at/{{id}}_{{v}}.zip",
                     "tags":["1.22.5"]}
                    """));

                return Json($$$"""
                    {"statuscode":"200","mod":{"modid":1,"assetid":2,"name":"{{{id}}}",
                     "urlalias":"{{{id}}}","side":"both","releases":[{{{releases}}}]}}
                    """);
            }

            if (url.StartsWith("https://files.example/"))
                return Bytes(AtLink ?? Encoding.UTF8.GetBytes("<html>gone</html>"));

            if (CdnDown) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

            var file = url[(url.LastIndexOf('/') + 1)..];
            var modId = file[..file.IndexOf('_')];
            var deps = modId == "olla" ? """, "dependencies": {"lib": "*"}""" : "";

            return Bytes(Zip($"{{\"modid\":\"{modId}\",\"version\":\"{file}\"{deps}}}",
                comment: Rebuilt ? "rebuilt" : null));
        }

        private static Task<HttpResponseMessage> Json(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });

        private static Task<HttpResponseMessage> Bytes(byte[] bytes) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }

    /// <summary>Timestamped so one URL serves one set of bytes, as PackSyncPinningTests explains.</summary>
    public static byte[] Zip(string modInfo, string? comment = null)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (comment is not null) zip.Comment = comment;

            var entry = zip.CreateEntry("modinfo.json");
            entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(modInfo);
        }

        return buffer.ToArray();
    }

    private readonly Stub _stub = new();

    private Task<SyncReport> Sync(PackManifest pack, params string[] update)
    {
        var http = new HttpClient(_stub);
        return new PackSyncer(new ModDbClient(http), http).SyncAsync(
            pack, ModsDir, LockPath, allowUpdates: update.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private static PackManifest Olla() => new()
    {
        Id = "demo",
        GameVersion = "1.22.5",
        Mods = [new PackMod { ModId = "olla" }],
    };

    private LockedMod? Locked(string modId) =>
        PackLock.Load(LockPath)?.Mods.SingleOrDefault(m => m.ModId == modId);

    [Fact]
    public async Task A_checksum_refusal_holds_on_the_next_sync()
    {
        Assert.False((await Sync(Olla())).Failed);
        var original = Locked("olla")!.Sha256;

        // The release is rebuilt behind its version number, and the copy here goes missing.
        _stub.Rebuilt = true;
        File.Delete(Path.Combine(ModsDir, "olla_1.0.0.zip"));

        var first = await Sync(Olla());
        var second = await Sync(Olla());

        // Refused both times — the second used to find no entry, take the rebuilt bytes and
        // record them as though the author had published them.
        Assert.True(first.Failed);
        Assert.True(second.Failed);
        Assert.Equal(original, Locked("olla")!.Sha256);
        Assert.False(File.Exists(Path.Combine(ModsDir, "olla_1.0.0.zip")));

        // Nothing could be read from a zip that is not here, so the lock's own record of what
        // olla required is what kept lib.
        Assert.NotNull(Locked("lib"));
        Assert.True(File.Exists(Path.Combine(ModsDir, "lib_1.0.0.zip")));
    }

    [Fact]
    public async Task An_update_whose_download_fails_leaves_the_working_copy_and_its_library()
    {
        Assert.False((await Sync(Olla())).Failed);

        _stub.Newest = "2.0.0";
        _stub.CdnDown = true;

        var update = await Sync(Olla(), "olla");

        Assert.True(update.Failed);
        Assert.Equal("1.0.0", Locked("olla")!.Version);
        Assert.True(File.Exists(Path.Combine(ModsDir, "olla_1.0.0.zip")));
        Assert.NotNull(Locked("lib"));
        Assert.True(File.Exists(Path.Combine(ModsDir, "lib_1.0.0.zip")));

        // And an ordinary sync once the CDN is back installs what the lock says, which is
        // still the version that worked — not the update that did not happen.
        _stub.CdnDown = false;
        var after = await Sync(Olla());

        Assert.False(after.Failed);
        Assert.Equal("1.0.0", Locked("olla")!.Version);
    }

    [Fact]
    public async Task Updating_a_linked_mod_to_an_error_page_keeps_the_working_mod()
    {
        // An update lands under the same name as the file it replaces. Moved into place
        // before it was checked, the page overwrote the mod and was then deleted as not a
        // mod — taking the only working copy with it.
        var good = Zip("""{"modid":"tweaks","version":"1.0.0"}""");
        _stub.AtLink = good;

        var pack = new PackManifest
        {
            Id = "demo",
            GameVersion = "1.22.5",
            Mods = [new PackMod { ModId = "tweaks", Url = "https://files.example/tweaks.zip" }],
        };

        Assert.False((await Sync(pack)).Failed);
        var installed = Directory.EnumerateFiles(ModsDir).Single();

        _stub.AtLink = null;
        var update = await Sync(pack, "tweaks");

        Assert.True(update.Failed);
        Assert.Equal(good, File.ReadAllBytes(installed));
        Assert.Equal(["tweaks.zip"], Directory.EnumerateFiles(ModsDir).Select(Path.GetFileName));
        Assert.NotNull(Locked("tweaks"));
    }

    [Fact]
    public async Task A_linked_mod_whose_download_fails_keeps_its_entry()
    {
        _stub.AtLink = Zip("""{"modid":"tweaks","version":"1.0.0"}""");

        var pack = new PackManifest
        {
            Id = "demo",
            GameVersion = "1.22.5",
            Mods = [new PackMod { ModId = "tweaks", Url = "https://files.example/tweaks.zip" }],
        };

        Assert.False((await Sync(pack)).Failed);
        var sha = Locked("tweaks")!.Sha256;

        // Gone from disk, then unreachable. Dropping the entry would let the next sync take
        // whatever the link serves next with nothing to compare it against.
        File.Delete(Path.Combine(ModsDir, "tweaks.zip"));
        var unreachable = new HttpClient(new Unreachable());

        var report = await new PackSyncer(new ModDbClient(unreachable), unreachable)
            .SyncAsync(pack, ModsDir, LockPath);

        Assert.True(report.Failed);
        Assert.Equal(sha, Locked("tweaks")!.Sha256);
    }

    private sealed class Unreachable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            throw new HttpRequestException("no route to host");
    }
}
