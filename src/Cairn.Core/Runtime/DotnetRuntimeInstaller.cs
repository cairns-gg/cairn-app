using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cairn.Core.Runtime;

/// <summary>A downloadable .NET runtime build.</summary>
public sealed record DotnetRuntimeRelease(string Version, string Rid, string Url, string? Sha512)
{
    public string FileName => Url[(Url.LastIndexOf('/') + 1)..];
}

public sealed class DotnetRuntimeException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Where Cairn keeps .NET runtimes it downloaded, under ~/.cairn/runtimes.
///
/// This exists because each game version pins a different .NET major — 1.21 needs .NET 8,
/// 1.22 needs .NET 10 — and the game bundles no runtime. Rather than asking the user to
/// install several system-wide SDKs, Cairn can keep private copies and point the game at
/// the right one via DOTNET_ROOT.
/// </summary>
public sealed class RuntimeStore
{
    private readonly string _root;

    public RuntimeStore(string? root = null) => _root = root ?? CairnPaths.RuntimesRoot;

    public string Root => _root;

    public string InstallDir(string version, string rid)
    {
        if (!IsValidComponent(version) || !IsValidComponent(rid))
            // Not translated: a guard on Cairn's own invariants, so this sentence only
            // appears when Cairn has a bug and its audience is whoever reads the report.
            throw new ArgumentException($"'{version}-{rid}' is not a usable runtime directory name.");

        return Path.Combine(_root, $"{version}-{rid}");
    }

    private static bool IsValidComponent(string? s) =>
        !string.IsNullOrWhiteSpace(s)
        && s.Length <= 40
        && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
        && s is not ("." or "..");

    public IEnumerable<DotnetRuntime> ListInstalled()
    {
        if (!Directory.Exists(_root)) yield break;

        foreach (var dir in Directory.EnumerateDirectories(_root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var runtime = DotnetRuntimeLocator.Inspect(dir);
            if (runtime is not null) yield return runtime;
        }
    }

    /// <summary>A managed runtime able to host the given framework on the given architecture.</summary>
    public DotnetRuntime? Find(Version required, ExecutableArch arch) =>
        ListInstalled().FirstOrDefault(r =>
            r.Satisfies(required) && (r.Arch == arch || r.Arch == ExecutableArch.Unknown));

    /// <summary>Root to hand a game as DOTNET_ROOT, or null when nothing managed fits.</summary>
    public string? RootFor(GameInstall install) =>
        Find(install.RequiredFramework, install.Architecture)?.Root;

    public void Remove(string version, string rid)
    {
        var dir = InstallDir(version, rid);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// Removes a runtime this store listed, by the directory it was found in — the same
    /// reasoning as GameStore.Remove(GameInstall): the directory is what was inspected,
    /// so rebuilding a path from its reported version can miss.
    /// </summary>
    public void Remove(DotnetRuntime runtime)
    {
        var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtime.Root));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_root));

        // Only ever inside the store, and never the store itself: this deletes recursively.
        if (!dir.StartsWith(root + Path.DirectorySeparatorChar, PathComparison) || dir == root)
            // Not translated: a guard on Cairn's own invariants, so this sentence only
            // appears when Cairn has a bug and its audience is whoever reads the report.
            throw new InvalidOperationException($"'{runtime.Root}' is not a managed runtime.");

        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
}

/// <summary>
/// Resolves and installs .NET runtimes from Microsoft's public release metadata.
/// Unauthenticated, and the metadata publishes a SHA512 per file.
/// </summary>
public sealed class DotnetRuntimeInstaller(HttpClient http, RuntimeStore store)
{
    private const string ReleasesIndexUrl =
        "https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json";

    /// <summary>
    /// Where Microsoft publishes runtime archives.
    ///
    /// One host, from reading every channel the release index lists: 5,234 of the 5,258
    /// runtime file URLs across all fourteen channels are builds.dotnet.microsoft.com, and
    /// every one of the remaining 24 belongs to .NET 1.0 through 5.0 on the two hosts
    /// Microsoft published from before the move — download.visualstudio.microsoft.com and
    /// download.microsoft.com. Those are deliberately not listed: the major asked for here
    /// comes from the game's own runtimeconfig, Vintage Story needs .NET 8 or 10, and there
    /// is no path by which Cairn requests a channel old enough to reach them. Adding them
    /// would widen the list to hosts serving Microsoft's entire download estate in exchange
    /// for versions this cannot ask for.
    ///
    /// Same shape and same staleness risk as the lists in
    /// <see cref="Games.GameCatalog"/> and <see cref="ModDb.ModDbUrls"/>: if Microsoft
    /// moves again, this fails closed with a printable reason, which is the right
    /// direction for a list bounding where an executable runtime may come from.
    /// </summary>
    private static readonly string[] DownloadHosts = ["builds.dotnet.microsoft.com"];

    /// <summary>Whether a release-index URL points somewhere Microsoft serves runtimes from.</summary>
    public static bool IsKnownDownloadHost(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && DownloadHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>The host worth naming in a refusal, or a description when there is none.</summary>
    private static string Origin(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Scheme == Uri.UriSchemeHttps ? uri.Host : $"{uri.Host} over {uri.Scheme}"
            : "an address that is not a URL";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// RID for the architecture the game needs. The published clients are x64 on every
    /// platform, so this is normally the x64 rid for the current OS.
    /// </summary>
    public static string RidFor(ExecutableArch arch)
    {
        var cpu = arch switch
        {
            ExecutableArch.Arm64 => "arm64",
            ExecutableArch.X86 => "x86",
            _ => "x64",
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return $"osx-{cpu}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return $"win-{cpu}";
        return $"linux-{cpu}";
    }

    /// <summary>The published release list for one major, newest first.</summary>
    private async Task<ChannelReleases> LoadChannelAsync(int major, CancellationToken ct)
    {
        var index = await http.GetFromJsonAsync<ReleasesIndex>(ReleasesIndexUrl, Json, ct)
                        .ConfigureAwait(false)
                    ?? throw new DotnetRuntimeException(Lang.Get("dotnet-no-index"));

        var channel = index.Entries.FirstOrDefault(e =>
            Version.TryParse(e.ChannelVersion, out var v) && v.Major == major);

        if (channel?.ReleasesJson is null)
            throw new DotnetRuntimeException(Lang.Get("dotnet-no-channel", major));

        return await http.GetFromJsonAsync<ChannelReleases>(channel.ReleasesJson, Json, ct)
                   .ConfigureAwait(false)
               ?? throw new DotnetRuntimeException(Lang.Get("dotnet-channel-unreadable", major));
    }

    /// <summary>
    /// Latest SDK of the given major, for the given rid.
    ///
    /// Needed because building Optimum means running <c>dotnet build</c>, and Cairn's
    /// private .NET is a runtime — it can host the game but cannot compile anything. Rather
    /// than making a compiler toolchain a prerequisite the user must go and satisfy, this
    /// fetches one the same way the runtime is fetched.
    /// </summary>
    public async Task<DotnetRuntimeRelease> ResolveSdkAsync(
        int major, string rid, CancellationToken ct = default)
    {
        var releases = await LoadChannelAsync(major, ct).ConfigureAwait(false);

        var sdk = releases.Releases.FirstOrDefault()?.Sdk
                  ?? throw new DotnetRuntimeException(Lang.Get("dotnet-no-sdk", major));

        // Named for the same reason the runtime asset is named below: an rid lists several
        // archives and the first is not the one wanted.
        var file = sdk.Files.FirstOrDefault(f =>
            string.Equals(f.Rid, rid, StringComparison.OrdinalIgnoreCase)
            && ArchiveExtractor.IsSupported(f.Name)
            && f.Name.StartsWith("dotnet-sdk", StringComparison.OrdinalIgnoreCase));

        if (file?.Url is null)
            throw new DotnetRuntimeException(Lang.Get("dotnet-no-sdk-archive", sdk.Version, rid));

        return new DotnetRuntimeRelease(sdk.Version, rid, file.Url, file.Hash);
    }

    /// <summary>Latest patch of the given major, for the given rid.</summary>
    public async Task<DotnetRuntimeRelease> ResolveAsync(
        int major, string rid, CancellationToken ct = default)
    {
        var releases = await LoadChannelAsync(major, ct).ConfigureAwait(false);

        // releases[0] is the newest; its runtime is what latest-runtime refers to.
        var runtime = releases.Releases.FirstOrDefault()?.Runtime
                      ?? throw new DotnetRuntimeException(Lang.Get("dotnet-no-runtime", major));

        // Named, not merely first. Every rid lists an apphost pack alongside the runtime,
        // and it comes first:
        //
        //   dotnet-apphost-pack-linux-x64.tar.gz    5 MB of build-time templates
        //   dotnet-runtime-linux-x64.tar.gz         the actual runtime
        //
        // Taking the first supported archive downloaded and unpacked the apphost pack
        // quite happily, then failed at the end with nothing that looks like a runtime in
        // it. On every platform — but only on a machine with no .NET already installed,
        // which is why the one this was written on never showed it.
        var file = runtime.Files.FirstOrDefault(f =>
            string.Equals(f.Rid, rid, StringComparison.OrdinalIgnoreCase)
            && ArchiveExtractor.IsSupported(f.Name)
            && f.Name.StartsWith("dotnet-runtime", StringComparison.OrdinalIgnoreCase));

        if (file?.Url is null)
            throw new DotnetRuntimeException(Lang.Get("dotnet-no-runtime-archive", runtime.Version, rid));

        return new DotnetRuntimeRelease(runtime.Version, rid, file.Url, file.Hash);
    }

    public async Task<DotnetRuntime> InstallAsync(
        DotnetRuntimeRelease release,
        IProgress<InstallProgressReport>? progress = null,
        CancellationToken ct = default)
    {
        var target = store.InstallDir(release.Version, release.Rid);
        if (DotnetRuntimeLocator.Inspect(target) is { } existing) return existing;

        // Both of these guard the same thing the game installer guards, for the same
        // reason: this is a remote document choosing where bytes land and where they come
        // from, and what gets unpacked here is the runtime that goes on to execute the
        // game. They were written for GameInstaller and not for this file, which is how a
        // rule ends up applied to one of the two places that needed it.
        //
        // FileName is derived by taking everything after the last '/' in the URL, so it
        // cannot carry a forward slash — but it can carry backslashes, and on Windows
        // "a\..\..\evil.exe" is a directory traversal that Path.Combine will honour.
        if (!BareFileName.IsBare(release.FileName))
            throw new DotnetRuntimeException(Lang.Get("dotnet-bad-filename", release.FileName));

        if (!IsKnownDownloadHost(release.Url))
            throw new DotnetRuntimeException(Lang.Get("dotnet-bad-origin", Origin(release.Url), release.FileName));

        // Asked before the download rather than after it. Refusing a hashless release is
        // not new, but it used to happen once ~80 MB had already been fetched — a check
        // whose answer never depended on the bytes, placed after the expensive step that
        // a crafted index entry would have wanted anyway. Microsoft's release index
        // carries a hash for every file, so an entry without one is not a case to
        // tolerate; the verification itself still runs after the download, because that
        // one genuinely needs the bytes.
        if (release.Sha512 is not { Length: > 0 })
            throw new DotnetRuntimeException(Lang.Get("dotnet-no-hash", release.FileName));

        Directory.CreateDirectory(store.Root);
        var archive = Path.Combine(store.Root, release.FileName + ".partial");
        var staging = target + ".staging";

        try
        {
            await DownloadAsync(release.Url, archive, progress, ct).ConfigureAwait(false);

            progress?.Report(new InstallProgressReport("verifying", 0, 0));
            await VerifyAsync(archive, release.Sha512, ct).ConfigureAwait(false);

            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);

            progress?.Report(new InstallProgressReport("extracting", 0, 0));
            await ArchiveExtractor.ExtractAsync(archive, staging, ct).ConfigureAwait(false);

            ArchiveExtractor.EnsureExecutable(Path.Combine(staging, "dotnet"));
            ArchiveExtractor.EnsureExecutable(Path.Combine(staging, "dotnet.exe"));

            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(staging, target);
        }
        catch
        {
            if (Directory.Exists(staging)) TryDelete(staging);
            throw;
        }
        finally
        {
            if (File.Exists(archive)) TryDeleteFile(archive);
        }

        return DotnetRuntimeLocator.Inspect(target)
               ?? throw new DotnetRuntimeException(Lang.Get("dotnet-no-framework", release.Version, target));
    }

    private async Task DownloadAsync(
        string url, string destination, IProgress<InstallProgressReport>? progress, CancellationToken ct)
    {
        using var response = await http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var sink = File.Create(destination);

        var buffer = new byte[1 << 20];
        long done = 0, lastReport = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;

            await sink.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;

            if (done - lastReport >= 2 << 20)
            {
                lastReport = done;
                progress?.Report(new InstallProgressReport("downloading", done, total));
            }
        }

        progress?.Report(new InstallProgressReport("downloading", done, total));
    }

    private static async Task VerifyAsync(string path, string expected, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA512.HashDataAsync(fs, ct).ConfigureAwait(false);
        var actual = Convert.ToHexStringLower(hash);

        if (!string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new DotnetRuntimeException(Lang.Get("dotnet-corrupt"));
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    private static void TryDeleteFile(string file)
    {
        try { File.Delete(file); } catch (IOException) { }
    }

    // ---- release metadata shapes ----

    private sealed class ReleasesIndex
    {
        [JsonPropertyName("releases-index")] public List<IndexEntry> Entries { get; set; } = [];
    }

    private sealed class IndexEntry
    {
        [JsonPropertyName("channel-version")] public string ChannelVersion { get; set; } = "";
        [JsonPropertyName("releases.json")] public string? ReleasesJson { get; set; }
    }

    private sealed class ChannelReleases
    {
        [JsonPropertyName("releases")] public List<ReleaseEntry> Releases { get; set; } = [];
    }

    private sealed class ReleaseEntry
    {
        [JsonPropertyName("runtime")] public RuntimeEntry? Runtime { get; set; }

        // Same shape as the runtime entry — a version and a file list.
        [JsonPropertyName("sdk")] public RuntimeEntry? Sdk { get; set; }
    }

    private sealed class RuntimeEntry
    {
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("files")] public List<RuntimeFile> Files { get; set; } = [];
    }

    private sealed class RuntimeFile
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("rid")] public string? Rid { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("hash")] public string? Hash { get; set; }
    }
}

/// <summary>Simple phase/bytes progress, shared by the runtime installer.</summary>
public sealed record InstallProgressReport(string Phase, long Done, long Total)
{
    public double? Fraction => Total > 0 ? (double)Done / Total : null;
}
