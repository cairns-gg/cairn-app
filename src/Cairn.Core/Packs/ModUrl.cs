using System.Security.Cryptography;
using System.Text.Json;
using Cairn.Core.ModDb;

namespace Cairn.Core.Packs;

/// <summary>What ModDB has to say about a file fetched from one of its download links.</summary>
public enum ModDbListing
{
    /// <summary>Not a ModDB link; there was nothing to ask.</summary>
    NotModDb,

    /// <summary>
    /// The mod is listed, and this file is one of its releases. The link was only ever a
    /// way of naming a release ModDB already serves.
    /// </summary>
    Listed,

    /// <summary>
    /// The file is on ModDB but the mod is not listed — uploaded and not yet published,
    /// which is the case the link form exists for. The API answers 404, and the link is
    /// the only way anybody gets the file until that changes.
    /// </summary>
    Unlisted,

    /// <summary>ModDB could not be reached, so nothing is known either way.</summary>
    Unknown,
}

/// <summary>
/// What a mod's own zip said about itself when fetched from its URL, and what was fetched.
/// </summary>
/// <param name="Problem">
/// Why this cannot be added as a mod, or null when it can. A file that is not a zip, or a
/// zip with no <c>modinfo.json</c>, or one that declares no mod id: each is a different
/// thing to tell the person who pasted the address, and none of them is a mod.
/// </param>
/// <param name="Side">Which side the zip says it runs on, as written, or null when it did not say.</param>
/// <param name="FileId">ModDB's id for the file, when the address was a ModDB download link. Zero otherwise.</param>
/// <param name="Listing">Whether ModDB lists this mod, for a file fetched from there.</param>
/// <param name="ListedVersion">
/// The version ModDB gives the release this file belongs to, when <see cref="Listing"/> is
/// <see cref="ModDbListing.Listed"/>. ModDB's spelling rather than the zip's, because it is
/// ModDB's that a pin has to match.
/// </param>
public sealed record ModUrlInspection(
    string? ModId, string? Name, string? Version, string Sha256, string? Problem, string? Side = null,
    int FileId = 0, ModDbListing Listing = ModDbListing.NotModDb, string? ListedVersion = null)
{
    public bool IsMod => Problem is null && !string.IsNullOrWhiteSpace(ModId);

    public bool IsListed => Listing == ModDbListing.Listed && !string.IsNullOrWhiteSpace(ListedVersion);

    /// <summary>"Anego Tweaks 1.2.0", or whatever survives.</summary>
    public string Describe()
    {
        var name = string.IsNullOrWhiteSpace(Name) ? ModId ?? "" : Name;
        return string.IsNullOrWhiteSpace(Version) ? name : $"{name} {Version}";
    }

    /// <summary>
    /// The manifest entry this file should become.
    ///
    /// A release ModDB already lists is added as an ordinary ModDB mod pinned to that
    /// release, not as an address: the address was only how somebody found it, and an
    /// entry ModDB resolves is one every copy of the pack reproduces, updates and
    /// publishes the same way as the rest. The pin is there because a specific file was
    /// asked for; unpinning it is one click. Everything else is fetched from where it was
    /// found.
    /// </summary>
    public PackMod ToManifestEntry(string url) => IsListed
        ? new PackMod { ModId = ModId!, Version = ListedVersion }
        : new PackMod { ModId = ModId!, Url = url };
}

/// <summary>
/// The rules for a mod fetched from an address the pack names rather than from ModDB.
///
/// Its own type, like <see cref="ModFileName"/> and for the same reason: the manifest
/// validates an address, the syncer fetches it, both front-ends inspect one before adding
/// it, and a rule kept next to one of those callers is a rule the others reimplement. See
/// <see cref="PackMod.Url"/> for why the address is in the manifest at all.
/// </summary>
public static class ModUrl
{
    /// <summary>
    /// Why this is not an address to fetch a mod from, phrased to finish "its address …",
    /// or null when there is nothing wrong with it.
    ///
    /// The transport rule is <see cref="PackSources"/>' and not a stricter one of its own:
    /// https, or plain http to loopback, where the packets never leave the machine. Mods are
    /// code, and a file fetched in the clear across a network is a file anybody on the path
    /// could have replaced — the hash beside it is no defence on the first sync, which is
    /// the one that writes the hash. Nothing narrows the host. That is the point of the
    /// feature: the mod is not on ModDB, and whoever put it somewhere else is the one to ask
    /// whether that somewhere is trusted, on the screen where the pack is imported.
    /// </summary>
    public static string? Problem(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !PackSources.IsRemote(url))
            return Lang.Get("url-unusable");

        if (PackSources.IsRewritableInFlight(url))
            return Lang.Get("url-not-https", new Uri(url).Scheme);

        return null;
    }

    /// <summary>Whether this looks like an address at all, for a box that takes either an address or a name.</summary>
    public static bool LooksLikeUrl(string? text) =>
        !string.IsNullOrWhiteSpace(text) && PackSources.IsRemote(text.Trim());

    /// <summary>The host, for a row that says where a mod comes from without quoting the whole address.</summary>
    public static string Host(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url ?? "";

    /// <summary>
    /// The name the fetched file gets inside the pack's <c>Mods</c> directory, or null when
    /// no usable one can be made.
    ///
    /// The address's own last segment when it is a plain mod filename — <c>anego_1.2.0.zip</c>
    /// reads better in a directory than anything invented — and otherwise a name built from
    /// the mod id, because plenty of private hosting ends in <c>?id=5</c> or a bare share
    /// token. Either way it goes through <see cref="ModFileName"/>: this is a string out of
    /// somebody else's manifest about to be combined with a directory, and that rule exists
    /// for exactly this shape of input.
    /// </summary>
    public static string? FileNameFor(string url, string modId)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var last = Uri.UnescapeDataString(uri.Segments.LastOrDefault() ?? "");
            if (last.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && ModFileName.Problem(last) is null)
                return last;
        }

        return ModFileName.Safe($"{modId}.zip");
    }

    /// <summary>
    /// Fetches the file at <paramref name="url"/> into a temporary file and reads what it
    /// says about itself, so a front-end can name the mod before writing anything into
    /// the manifest. The mod id comes from the zip and from nowhere else: asking somebody
    /// to type the id of a mod they wrote is asking them to get it wrong once.
    ///
    /// The bytes are discarded. The sync fetches them again into the pack, which costs a
    /// second download of something small and keeps <c>Mods/</c> written by one thing.
    /// </summary>
    /// <param name="moddb">
    /// Asked whether the mod is listed, when the address is one of ModDB's own download
    /// links. Null skips the question, and the file is treated as any other address.
    /// </param>
    /// <exception cref="HttpRequestException">The address could not be fetched.</exception>
    public static async Task<ModUrlInspection> InspectAsync(
        HttpClient http, string url, CancellationToken ct = default, ModDbClient? moddb = null)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"cairn-modurl-{Guid.NewGuid():n}.zip");
        ModUrlInspection found;
        try
        {
            await DownloadAsync(http, url, tmp, ct).ConfigureAwait(false);
            found = Inspect(tmp, await Sha256Async(tmp, ct).ConfigureAwait(false));
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }

        if (!found.IsMod || !ModDbUrls.TryParseDownload(url, out var fileId, out _)) return found;

        found = found with { FileId = fileId };
        if (moddb is null) return found;

        var (listing, version) = await ListingAsync(moddb, found.ModId!, fileId, ct).ConfigureAwait(false);
        return found with { Listing = listing, ListedVersion = version };
    }

    /// <summary>
    /// Whether ModDB lists a mod, and under what version it serves a given file.
    ///
    /// A 404 is the answer and not a failure: it is what the API says about a mod that has
    /// been uploaded and not yet published, which is the whole reason a ModDB link ends up
    /// pasted into a pack. Not reaching ModDB at all is different, and says nothing.
    /// </summary>
    public static async Task<(ModDbListing Listing, string? Version)> ListingAsync(
        ModDbClient moddb, string modId, int fileId, CancellationToken ct = default)
    {
        ModDbMod mod;
        try
        {
            mod = await moddb.GetModAsync(modId, ct).ConfigureAwait(false);
        }
        catch (ModDbException)
        {
            return (ModDbListing.Unlisted, null);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException)
        {
            return (ModDbListing.Unknown, null);
        }

        // Listed, but this file is not one of its releases: a draft release of a mod that
        // is otherwise public, which the link still serves. The address is the only way to
        // that file, so it is treated as unlisted — what matters is where the bytes are.
        var release = mod.Releases.FirstOrDefault(r => r.FileId == fileId);

        return release is null || string.IsNullOrWhiteSpace(release.ModVersion)
            ? (ModDbListing.Unlisted, null)
            : (ModDbListing.Listed, release.ModVersion);
    }

    /// <summary>
    /// What a fetched file is, judged from its own <c>modinfo.json</c>.
    ///
    /// Stricter than the same read on a ModDB download, where a zip that will not open is
    /// warned about and installed anyway because ModDB has already said what it is. Nothing
    /// has said anything about this file: the manifest names an address, and the zip is
    /// the only witness to what the address serves. A file that cannot testify is refused.
    /// </summary>
    public static ModUrlInspection Inspect(string path, string sha256)
    {
        var info = ModDependencies.Describe(path);

        if (info.Problem is not null)
            return new ModUrlInspection(null, null, null, sha256, info.Problem);

        if (string.IsNullOrWhiteSpace(info.ModId))
            return new ModUrlInspection(null, info.Name, info.Version, sha256,
                Lang.Get("modurl-no-modid"));

        return new ModUrlInspection(
            info.ModId.Trim(), info.Name, info.Version, sha256, null,
            string.IsNullOrWhiteSpace(info.Side) ? null : info.Side.Trim().ToLowerInvariant());
    }

    /// <summary>
    /// Streamed to disk beside its destination, then moved into place, so an interrupted
    /// fetch never leaves a truncated zip where the game would try to load it. The same
    /// shape as the ModDB download in <see cref="PackSyncer"/>, and shared with it.
    /// </summary>
    public static async Task DownloadAsync(HttpClient http, string url, string target, CancellationToken ct)
    {
        var tmp = target + ".partial";
        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = File.Create(tmp))
            {
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }

            File.Move(tmp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    public static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var s = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(s, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
