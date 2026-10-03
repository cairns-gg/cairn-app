using Cairn.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cairn.Core.Launch;

namespace Cairn.Core.Packs;

/// <summary>A mod the pack asks for. Version is optional; when set it is an exact pin.</summary>
public sealed class PackMod
{
    [JsonPropertyName("modid")] public string ModId { get; set; } = "";
    [JsonPropertyName("version")] public string? Version { get; set; }

    /// <summary>
    /// Where to fetch this mod from instead of ModDB. Null for the ordinary mod.
    ///
    /// A private mod — one written for a single server, or not yet fit to publish — has
    /// nowhere on ModDB to be resolved from, and until this existed the only way to run one
    /// from a pack was to drop the zip into <c>Mods/</c> by hand on every machine, where
    /// nothing tracked it and a shared pack could not carry it. A URL is the least the pack
    /// can say that lets every copy fetch the same file.
    ///
    /// The URL says where; the lock's SHA-256 says what. The first sync records the hash of
    /// what it fetched, and every sync after that refuses a file whose bytes have moved —
    /// the same promise ModDB mods get, made about a host nobody moderates. Taking a new
    /// build is an update, asked for like any other. See <see cref="ModUrl"/> for what an
    /// address may look like and <see cref="PackSyncer"/> for how one is installed.
    ///
    /// In the manifest rather than the lock because it is intent: the author chose it and a
    /// recipient reads it before importing, on the same screen that names the server the
    /// pack joins. A lock never gets to say where bytes come from —
    /// <see cref="PackLock.ClearResolvedLocations"/> — and this is what makes that rule
    /// survive the feature: the location a follower fetches from is one their author wrote
    /// into the document they were shown, not one a lock carried in beside it.
    /// </summary>
    [JsonPropertyName("url")] public string? Url { get; set; }

    /// <summary>Whether this mod is fetched from <see cref="Url"/> rather than ModDB.</summary>
    [JsonIgnore] public bool IsFromUrl => !string.IsNullOrWhiteSpace(Url);

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

    /// <summary>
    /// Values the pack sets in the mods' own config files, as path → the values it asserts:
    /// <c>{ "terrainslabs.json": { "compatibleMods": ["footprints"] } }</c>. The path is
    /// relative to the pack's <c>ModConfig/</c>, with <c>/</c> as the separator on every
    /// platform, so a mod keeping its settings in a folder is reachable as
    /// <c>"XLeveling/mining.json"</c>.
    ///
    /// Two mods often need a line in one of their configs to work together — Terrain Slabs
    /// wants Footprints named in its list before the two behave — and the author works that
    /// out once. This is where the answer goes, for the same reason
    /// <see cref="Keybinds"/> is here: the value is entirely in reaching the people who did
    /// not do the work.
    ///
    /// **Only the values the pack asserts, not the whole file.** A mod's config is its own
    /// document, it versions with the mod, and a captured copy replayed into a pack whose
    /// user has moved on a major version is how a mod ends up refusing to load. A sparse
    /// object is also the only form a person can review before importing somebody's pack —
    /// which for a file that changes how the game plays is the point.
    ///
    /// <see cref="ModConfigFiles"/> owns what happens to these at launch, and the rule is
    /// not the one <see cref="ClientHotkeys"/> uses. Read it before changing this.
    /// </summary>
    [JsonPropertyName("modConfig")] public Dictionary<string, JsonObject>? ModConfig { get; set; }

    /// <summary>
    /// How much mod config one pack may carry, over every file together.
    ///
    /// Capped because this is a shared document: it is published, fetched by everyone who
    /// imports the pack, and meant to be readable by eye. The values that earn their place
    /// here are the handful that make mods agree with each other — in a real pack the median
    /// config file is 600 bytes and the largest is a 149KB ore table, and a pack that
    /// swallowed that one whole would have stopped being a manifest.
    /// </summary>
    public const int MaxModConfig = 64 * 1024;

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
            yield return Lang.Get("pack-id-required");

        if (!GameVersions.IsPlausibleVersion(GameVersion))
            yield return Lang.Get("pack-gameversion-unusable", GameVersion);

        // The pack's connect address is handed to the game as the value of --connect, and
        // it arrives from somebody else's pack. See ServerAddress: the point is not that a
        // strange address would fail to connect, it is that a value beginning with '-' is
        // read by the game's own parser as a further option.
        if (ServerAddress.Problem(Connect) is { } connect)
            yield return Lang.Get("pack-connect-problem", connect);

        // Checked here rather than at launch, so a pack naming ../../etc is refused by
        // import — the moment it arrives from someone else — and not on the machine of
        // whoever eventually presses Play. See ModConfigFiles.PathProblem.
        foreach (var problem in ModConfigProblems()) yield return problem;

        // Deliberately no length check on the description. The cap belongs where one is
        // written — pack settings, `init --description`, and the server on publish — not
        // where one is read. Refusing to open somebody's pack over a blurb 281 characters
        // long would be a bad trade for a field that is decoration; strict about what is
        // sent, tolerant about what arrives.

    }

    /// <summary>
    /// Everything wrong with the mod config this pack carries.
    ///
    /// A pack problem rather than a mod problem: these name files, not mods, so there is no
    /// single entry to drop and carry on with. A manifest that asks to write outside
    /// <c>ModConfig/</c> is one to refuse whole.
    /// </summary>
    public IEnumerable<string> ModConfigProblems()
    {
        if (ModConfig is null || ModConfig.Count == 0) yield break;

        foreach (var (file, patch) in ModConfig)
        {
            if (ModConfigFiles.PathProblem(file) is { } problem)
                yield return Lang.Get("pack-modconfig-bad-path", file, problem);

            else if (patch is null)
                yield return Lang.Get("pack-modconfig-empty", file);
        }

        var size = JsonSerializer.Serialize(ModConfig, JsonOptions).Length;
        if (size > MaxModConfig)
            yield return Lang.Get("pack-modconfig-too-big", size / 1024, MaxModConfig / 1024);
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
                yield return (m, Lang.Get("pack-mod-no-modid"));
                continue;
            }

            if (!seen.Add(m.ModId))
            {
                yield return (m, Lang.Get("pack-mod-duplicate"));
                continue;
            }

            if (m.Version is not null && !GameVersions.IsPlausibleVersion(m.Version))
                yield return (m, Lang.Get("pack-mod-bad-pin", m.Version));

            if (!m.IsFromUrl) continue;

            // Refused here, at the edge, rather than at download time: a manifest arrives
            // from somebody else, and an address that would be fetched in the clear is a
            // fault in the document rather than in the network it is opened on.
            if (ModUrl.Problem(m.Url) is { } problem)
                yield return (m, Lang.Get("pack-mod-bad-url", problem));

            // A pin names a release to look for on ModDB, and there is no ModDB in this
            // entry to look on. Rather than pick which of the two to believe, say so.
            else if (m.Version is not null)
                yield return (m, Lang.Get("pack-mod-url-and-pin"));
        }
    }

    private static string Describe(PackMod mod, string problem) =>
        string.IsNullOrWhiteSpace(mod.ModId)
            ? Lang.Get("pack-mod-unusable-anon", problem)
            : Lang.Get("pack-mod-unusable", mod.ModId, problem);

    /// <summary>
    /// Synchronous by design. Manifests are small local files, and callers include UI
    /// constructors — an async load there invites sync-over-async deadlocks on the
    /// Avalonia UI thread.
    /// </summary>
    public static PackManifest Load(string path)
        => JsonSerializer.Deserialize<PackManifest>(File.ReadAllText(path), JsonOptions)
           ?? throw new InvalidDataException(Lang.Get("pack-manifest-empty", path));

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

    /// <summary>
    /// Whether this was fetched from the manifest's own URL rather than resolved on ModDB.
    ///
    /// Recorded because the two kinds of entry mean different things to the next sync and
    /// nothing else tells them apart once <see cref="PackLock.ClearResolvedLocations"/> has
    /// run: an imported entry of either kind is a mod id, a version and a hash. The sync
    /// treats a lock entry as binding only when the manifest still asks for the mod the same
    /// way — a mod moved from a URL onto ModDB must not be pinned to a version ModDB never
    /// published, and one moved the other way must not be held to a hash of a different
    /// file. Absent, rather than false, for every ordinary mod.
    /// </summary>
    [JsonPropertyName("fromUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool FromUrl { get; set; }

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
    /// Files Cairn installed into Mods that this lock no longer describes, waiting for the
    /// next sync to remove them. Null when there are none, which is nearly always.
    ///
    /// The sweep removes only what the previous lock named — that is what keeps it off the
    /// mods people place by hand — so anything that rewrites the lock between two syncs
    /// hides from it every file the rewrite stops naming. Taking an author's revision is
    /// that rewrite: a mod they removed, or the old version of one they moved, stayed in
    /// Mods for ever, out of the lock and the launcher's sight and loaded by the game all
    /// the same. Written down rather than worked out at sync time because Cairn can close
    /// between the two.
    ///
    /// This machine's own record, and never anybody else's: a lock arriving from elsewhere
    /// could otherwise name a player's own mod as Cairn's to delete. PackBundle strips it on
    /// the way out and drops it on the way in.
    /// </summary>
    [JsonPropertyName("retired")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Retired { get; set; }

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
    ///
    /// A mod fetched from a URL loses its URL here too, and keeps <see cref="LockedMod.FromUrl"/>.
    /// The address it is fetched from is the manifest's — the document the recipient was
    /// shown — and the sync reads it from there and nowhere else, so a lock naming somewhere
    /// different was never going to be believed. Clearing it keeps the invariant simple:
    /// an imported lock carries no locations at all, whatever kind of entry it is.
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
