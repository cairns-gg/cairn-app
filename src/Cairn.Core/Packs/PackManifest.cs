using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cairn.Core.Packs;

/// <summary>A mod the pack asks for. Version is optional; when set it is an exact pin.</summary>
public sealed class PackMod
{
    [JsonPropertyName("modid")] public string ModId { get; set; } = "";
    [JsonPropertyName("version")] public string? Version { get; set; }

    /// <summary>
    /// The game version this pack targeted when somebody accepted that this mod publishes
    /// nothing marked for it. Null for the ordinary mod, which needs no such thing.
    ///
    /// A mod that has not caught up with the game is otherwise unaddable: the resolve
    /// refuses it and the sync reports "no release marked for game 1.22.6", which is true
    /// and unhelpful to somebody who has run it and knows it works. This is where that
    /// person's testimony lives — in the manifest rather than in local state, because it is
    /// part of what the pack is, and a pack that syncs only on the machine it was made on
    /// is not a pack you can share.
    ///
    /// It records the version rather than a bare "yes" so it can stop applying. Retarget
    /// the pack from 1.22 to 1.23 and nobody has tested anything: the acceptance describes
    /// a combination that no longer exists, and inheriting it would quietly install an
    /// untested mod for a game nobody ran it against. Same rule as a chosen install, which
    /// stops applying when the pack's version moves away from it and comes back when it
    /// moves back.
    /// </summary>
    [JsonPropertyName("acceptedFor")] public string? AcceptedFor { get; set; }

    /// <summary>
    /// Whether the acceptance still describes the pack in front of us.
    ///
    /// Same major.minor, not the same version: 1.22.6 to 1.22.7 is a patch the game itself
    /// treats as interchangeable for mods — <see cref="ModDb.MatchQuality.SameMinor"/> is
    /// built on exactly that — so re-asking on every patch bump would train people to say
    /// yes without reading. A minor bump is where the question becomes real again.
    /// </summary>
    public bool AcceptsUnmarkedFor(string gameVersion)
    {
        if (string.IsNullOrWhiteSpace(AcceptedFor) || string.IsNullOrWhiteSpace(gameVersion))
            return false;

        try
        {
            return GameVersions.IsSameMajorMinor(AcceptedFor, gameVersion);
        }
        catch (ArgumentException)
        {
            // A hand-edited manifest with something unparseable in it. Not an acceptance.
            return false;
        }
    }
}

/// <summary>
/// Declared intent, hand-editable and meant to be committed and shared: which mods,
/// for which game version, and optionally which server this pack is for.
/// Exact resolved versions live in <see cref="PackLock"/>, not here.
/// </summary>
public sealed class PackManifest
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }

    /// <summary>
    /// A sentence or two on what the pack is for, shown wherever it is offered to someone
    /// else. Short on purpose: it sits in listings beside other packs, where a paragraph
    /// pushes everything else off the screen — and the mod list already says what is in
    /// the pack. What it cannot say is who it is for, which is this.
    /// </summary>
    [JsonPropertyName("description")] public string? Description { get; set; }

    /// <summary>Room for two real sentences, and not for an essay.</summary>
    public const int MaxDescription = 280;

    /// <summary>Game version to resolve against, e.g. "1.22.5".</summary>
    [JsonPropertyName("gameVersion")] public string GameVersion { get; set; } = "";

    /// <summary>Optional "host:port" — lets a pack launch straight into its server.</summary>
    [JsonPropertyName("connect")] public string? Connect { get; set; }

    [JsonPropertyName("mods")] public List<PackMod> Mods { get; set; } = [];

    /// <summary>
    /// Hotkeys the pack ships, as code → combination: <c>{ "scribepinhud": "Ctrl+P" }</c>.
    ///
    /// Twenty mods bring twenty sets of defaults and several land on the same key. The
    /// author sorts that out once; without somewhere to put the answer, every person who
    /// installs the pack sorts out the same collisions again. This is that somewhere, and
    /// it is in the manifest — the shared document — because the whole value is that it
    /// reaches the people who did not do the work.
    ///
    /// Names rather than the numeric codes the game stores: <c>53</c> is Backspace, and a
    /// manifest nobody can read by eye is one nobody can review before importing.
    /// Null rather than empty when there are none, so the file of a pack that never set one
    /// looks exactly as it did before this existed.
    /// </summary>
    [JsonPropertyName("keybinds")] public Dictionary<string, string>? Keybinds { get; set; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Everything wrong with this manifest, pack and mods alike.
    /// </summary>
    public IEnumerable<string> Validate() => ValidatePack().Concat(ValidateMods());

    /// <summary>
    /// Problems with the pack itself, as opposed to any one mod in it.
    ///
    /// Split out because they are not the same kind of trouble. A missing id or an
    /// unusable game version means nothing can be installed at all; one bad mod entry
    /// means one mod cannot be. Treating the second as the first is how a single
    /// un-addable search result — a ModDB page with no modid — stopped a whole pack
    /// syncing, recoverable only by hand-editing pack.json.
    /// </summary>
    public IEnumerable<string> ValidatePack()
    {
        if (string.IsNullOrWhiteSpace(Id))
            yield return "Pack 'id' is required.";

        if (!GameVersions.IsPlausibleVersion(GameVersion))
            yield return $"Pack 'gameVersion' is not a usable version string: '{GameVersion}'. "
                         + "Write a bare version like \"1.22.5\" — the game silently reads "
                         + "\">=1.22.5\" as major version 0, which matches everything.";

        // Deliberately no length check on the description. The cap belongs where one is
        // written — pack settings, `init --description`, and the server on publish — not
        // where one is read. Refusing to open somebody's pack over a blurb 281 characters
        // long would be a bad trade for a field that is decoration; strict about what is
        // sent, tolerant about what arrives.

    }

    /// <summary>Problems with individual mod entries, each naming the entry it is about.</summary>
    public IEnumerable<string> ValidateMods()
    {
        foreach (var (mod, problem) in ModProblems()) yield return Describe(mod, problem);
    }

    /// <summary>
    /// Each unusable mod entry and why, so a caller can drop that one and carry on rather
    /// than refusing the pack.
    /// </summary>
    public IEnumerable<(PackMod Mod, string Problem)> ModProblems()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in Mods)
        {
            if (string.IsNullOrWhiteSpace(m.ModId))
            {
                yield return (m, "it has no modid — ModDB pages that publish no mod id, "
                                 + "such as a download listing or a modified client, cannot "
                                 + "be installed into a pack");
                continue;
            }

            if (!seen.Add(m.ModId))
            {
                yield return (m, "it is listed more than once");
                continue;
            }

            if (m.Version is not null && !GameVersions.IsPlausibleVersion(m.Version))
                yield return (m, $"its version pin '{m.Version}' is not a bare version "
                                 + "like \"1.3.0\"");
        }
    }

    private static string Describe(PackMod mod, string problem) =>
        string.IsNullOrWhiteSpace(mod.ModId)
            ? $"A mod entry cannot be used: {problem}."
            : $"'{mod.ModId}' cannot be used: {problem}.";

    /// <summary>
    /// Synchronous by design. Manifests are small local files, and callers include UI
    /// constructors — an async load there invites sync-over-async deadlocks on the
    /// Avalonia UI thread.
    /// </summary>
    public static PackManifest Load(string path)
        => JsonSerializer.Deserialize<PackManifest>(File.ReadAllText(path), JsonOptions)
           ?? throw new InvalidDataException($"{path} is empty or not a pack manifest.");

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}

public sealed class LockedMod
{
    [JsonPropertyName("modid")] public string ModId { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("filename")] public string FileName { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("releaseId")] public int ReleaseId { get; set; }
    [JsonPropertyName("fileId")] public int FileId { get; set; }

    /// <summary>Computed by Cairn on first download; ModDB publishes no hash.</summary>
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";

    [JsonPropertyName("side")] public string? Side { get; set; }

    /// <summary>
    /// The mods that pulled this one in, when the manifest did not name it directly.
    /// Null for a mod the pack asked for itself — the common case, where an empty list
    /// would just be noise in a file people read.
    /// </summary>
    [JsonPropertyName("requiredBy")] public List<string>? RequiredBy { get; set; }

    /// <summary>
    /// The game versions ModDB marks this release for, recorded only when they do not
    /// include the one the pack targets. Null for every ordinary mod.
    ///
    /// The lock is "exactly what was installed", and an unmarked release is the case where
    /// that phrase carries the most weight: it is there because somebody accepted it, and a
    /// lock that forgot would make the next sync — which installs from the lock without
    /// resolving anything — report it as a clean, matched mod. It also reads plainly in a
    /// file people open: "markedFor": ["1.21.4"] beside a pack targeting 1.22.
    /// </summary>
    [JsonPropertyName("markedFor")] public List<string>? MarkedFor { get; set; }
}

/// <summary>
/// Exactly what was installed, so a pack reproduces byte-for-byte for anyone who
/// clones it. Generated — edit the manifest instead.
/// </summary>
public sealed class PackLock
{
    [JsonPropertyName("gameVersion")] public string GameVersion { get; set; } = "";
    [JsonPropertyName("mods")] public List<LockedMod> Mods { get; set; } = [];

    /// <summary>
    /// Drops the parts of every entry that only ModDB is entitled to assert.
    ///
    /// A lock may say WHAT to install; it does not get to say WHERE the bytes come from.
    /// That distinction was always the intent — <see cref="PackSyncer"/> says so where it
    /// decides whether to believe a lock — but it was enforced by asking whether the URL
    /// pointed at a host ModDB serves from, which is not the same question. Anyone may
    /// upload a mod, so anyone may put a file on that host: a shared lock could name a
    /// reputable mod id and version beside a URL for something else entirely, and the
    /// SHA-256 sitting next to it was no defence, because whoever writes the URL writes
    /// the hash to match.
    ///
    /// Clearing these sends every entry down the resolve path instead, where the lock's
    /// version is used as the pin and ModDB answers where that release lives. The
    /// author's hash then becomes what it should always have been: a check that the bytes
    /// ModDB serves are the bytes the author had, which fails loudly when they differ.
    ///
    /// Modid, version and sha256 stay, and so do side and markedFor. Those are the
    /// author's to claim, and they are what makes a shared pack reproduce rather than
    /// merely resemble.
    /// </summary>
    public void ClearResolvedLocations()
    {
        foreach (var mod in Mods)
        {
            mod.Url = "";
            mod.FileName = "";
            mod.ReleaseId = 0;
            mod.FileId = 0;
        }
    }

    public static PackLock? Load(string path)
        => File.Exists(path)
            ? JsonSerializer.Deserialize<PackLock>(File.ReadAllText(path), PackManifest.JsonOptions)
            : null;

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, PackManifest.JsonOptions));
    }
}
