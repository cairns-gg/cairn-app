# Settings a pack can carry

A pack fixes the mods and their versions. It does not fix anything about how the game is
set up to run them, and for some packs that is most of the point: "wilderness survival,
harsh winters, 3× ore" is as much of a curation as the mod list, and today the author has
to write it in the description and hope.

Nor does it fix the parts that are not curation at all, only work. Twenty mods bring twenty
sets of hotkeys, several of which collide; the author reconciles them once, and then every
person who imports the pack reconciles the same collisions again from scratch. That one is
the strongest case here and the argument for building this at all — see *Keybinds*.

This is about a pack declaring those values and Cairn applying them — on a dedicated server
through `cairn-server`, and on the machine of somebody who imported the pack and pressed
Play.

## Three layers, and the one that traps people

Settings live in three places with different owners and very different lifecycles. Almost
every wrong answer here comes from treating them as one thing.

| Layer | Where it lives | Lifecycle | Who writes it today |
|---|---|---|---|
| **Client settings** | `<datapath>/clientsettings.json` | Read at startup, written at exit | Cairn already: `ClientSession`, `ClientModPaths` |
| **World config** | Inside the `.vcdbs` savegame — **there is no file** | Seeded at world creation; afterwards `/worldconfig` and a restart | Nobody |
| **Mod config** | `<datapath>/ModConfig/*.json` | Whenever the mod feels like it | The mods themselves |

All three are already per-pack, because every pack launches with its own `--dataPath`. That
is the one piece of luck in this design: no new isolation mechanism is needed anywhere.

## World config: only the savegame has it

The gameplay layer — world size, climate, ore rates, creature strength, spoil rates — is an
`ITreeAttribute` serialised into the savegame. A save is SQLite, and the whole world state
is one blob in one row:

```
tables:   chunk, gamedata, mapchunk, mapregion, playerdata
gamedata: (savegameid, data)  →  1 row, 1,333,044 bytes
```

Measured 2026-08-09 against a real pack save. So **editing world config from outside the
game is not on the table.** It would mean rewriting a proprietary binary attribute tree
inside somebody's world, which is the same class of act as moving mods underneath a pack —
the thing this project already refuses everywhere else.

That leaves creation time, and `/worldconfig` afterwards.

## What the game actually offers

Measured against the 1.22.6 client and the 1.22 API/survival source.

**Launch flags** — from `VintagestoryLib.dll`'s own help text:

| flag | help text |
|---|---|
| `--openWorld` | "Opens given world. If it doesn't exist it will be created" |
| `--rndWorld` | "Creates a new world with a random name. Use -p modifier to set playstyle" |
| `--playStyle` | "Used when creating a new world" |

Cairn already passes `--openWorld` (`GameLauncher.cs:86`). `--playStyle` is two lines. But a
playstyle is a *named bundle* — you pick `wildernesssurvival`, you do not set `landcover`.

**The savegame API** — `VintagestoryApi/Server/ISaveGame.cs`:

```csharp
bool IsNew { get; }                          // "True if this is a newly created world"
ITreeAttribute WorldConfiguration { get; }
string PlayStyle { get; set; }   int Seed { get; set; }   string WorldType { get; set; }
```

**The event order** — from `IServerEventAPI`'s own documentation:

- `SaveGameCreated` — "Triggered after a savegame has been created - i.e. when a new world
  was created"
- `SaveGameLoaded` — after the world data is loaded
- `InitWorldGenerator(handler, forWorldType)` — "Triggered **before the first chunk**, map
  chunk or map region is generated... Called right after the save game has been loaded"

Worldgen reads its values in that last one: `GenMaps.initWorldGen` pulls `worldClimate`,
`landcover`, `globalTemperature`, `polarEquatorDistance` and `upheavelCommonness` straight
out of `SaveGame.WorldConfiguration` (`VSEssentials/.../GenMaps.cs:217`). Anything written
at `SaveGameCreated` or `SaveGameLoaded` lands before it.

## The companion mod

Which means a mod can do this, and vanilla already does. `VSSurvivalMod`'s temporal
stability system fills in its own world config on load
(`Systems/TemporalStability/TemporalStability.cs:245`):

```csharp
api.Event.SaveGameLoaded += () => {
    bool prepNextStorm = sapi.WorldManager.SaveGame.IsNew;
    if (!sapi.World.Config.HasAttribute("temporalStability")) {
        string playstyle = sapi.WorldManager.SaveGame.PlayStyle;
        ...SaveGame.WorldConfiguration.SetBool("temporalStability", true);
```

So: a small mod that reads the pack's declared values and writes them into
`SaveGame.WorldConfiguration` at the right moment. Three things make this the right shape
rather than a workaround.

**Singleplayer comes free.** A local world runs an internal server, so the same server-side
mod system runs there. One mechanism covers `cairn-server` and Play, which nothing else in
this document manages.

**The channel already exists.** `GamePaths.ModConfig` is `<DataPath>/ModConfig`, and
`ICoreAPI.LoadModConfig(filename)` reads from exactly there. Cairn launches every pack with
its own `--dataPath`, so Cairn writes one file into the pack's `ModConfig/` before launch
and the mod reads it. No new path, no new isolation, nothing to keep in sync.

**Distribution stops being a problem.** Publish the mod to ModDB once and packs reference it
by `modid` like any other mod — Cairn installs it the way it installs everything else. This
is what makes the mod route acceptable at all: Cairn redistributes nobody's zip, and a
generated pack-local mod would have been the first time it did. Cairn can add it to the
pack automatically when the manifest declares `world`.

## Creation-only versus changeable

The game declares which is which. `WorldConfigurationAttribute` carries the flag
(`VintagestoryApi/Common/Playstyle/WorldConfiguration.cs:39`):

```csharp
public bool OnlyDuringWorldCreate = false;
```

Counted across vanilla survival's declarations in `VSSurvivalMod/Properties/AssemblyInfo.cs`:
**64 attributes, 15 creation-only, 49 changeable.** The 15 are world shape and climate:

```
worldWidth, worldLength, worldClimate, landcover, oceanscale, landformScale,
upheavelCommonness, geologicActivity, polarEquatorDistance, globalTemperature,
globalPrecipitation, globalForestation, startingClimate, graceTimer,
storyStructuresDistScaling
```

Everything else — `creatureStrength`, `foodSpoilSpeed`, `temporalStorms`, `toolDurability`,
`microblockChiseling`, `playerHealthPoints`, `seasons`, `snowAccum` — can move later.

**Do not hardcode that list.** `api.ModLoader.Mods` gives every loaded mod, each carrying
`Mod.WorldConfig.WorldConfigAttributes` with `Code`, `OnlyDuringWorldCreate`, `DataType`,
`Values` and `Default`. Asking the game classifies keys declared by mods *in the pack* as
correctly as vanilla's, and a copied list is a list that is wrong the first time a mod ships
a new attribute.

It also gives validation for free: `Values` is the declared allowed set, so
`landcover: "0.85"` — not one of the steps — is reported rather than silently generating a
world nobody asked for.

The classification then decides the behaviour, and this is the whole rule:

| | at world creation | on an existing world |
|---|---|---|
| **creation-only** | apply | report the difference, change nothing |
| **changeable** | apply | apply on load; it takes effect this session |

The second column's top row is not timidity. It could not take effect anyway, and writing it
would leave the savegame claiming a value the terrain does not have.

The second column's bottom row works because consumers read at `InitWorldGenerator` and
during startup, both after `SaveGameLoaded`. "Restart the world or server to apply changes"
— which every `/worldconfig` set answers with — and "relaunch through Cairn" are the same
event.

## Values are strings

Playstyle presets write every value as a JSON string, and vanilla reads them defensively:

```csharp
float landcover = api.World.Config.GetString("landcover", "1").ToFloat(1f);
```

So a real JSON `true` where the game expects `"true"` is a silent wrong answer, not an
error. The manifest should hold strings, and the mod should write strings, whatever the
declared `dataType` says.

## The Customize screen

At world creation the client's Customize screen has *already* populated
`WorldConfiguration` from the chosen playstyle. Vanilla's `HasAttribute` guard — fill in
only what is unset — would therefore almost never fire, so a pack's values would have to
overwrite what the player just picked, silently, moments after they picked it.

The way out is for the same mod to **also declare a playstyle** built from the pack's
values (`ModInfo(WorldConfig = "{ playstyles: [...] }")`). Then on the client the pack's
setup appears as a named preset the player selects and can inspect in Customize, while on a
headless server the `SaveGameCreated` write applies the same values with no GUI in the loop.
Same values, honest at both ends.

## Client settings

Already half-built. `ClientSettingsFile` reads `clientsettings.json` as a tree and writes it
back so the settings Cairn does not own survive untouched; `ClientSession` and
`ClientModPaths` are two existing writers. A third is the same shape.

The file is typed buckets — measured on a real install, 2026-08-09:

| bucket | keys | examples |
|---|---|---|
| `intSettings` | 50 | `viewDistance`, `maxFps`, `fieldOfView`, `maxAnimatedElements` |
| `boolSettings` | 50 | `developerMode`, `pauseGameOnLostFocus`, `toggleSprint` |
| `floatSettings` | 24 | `guiScale`, `gammaLevel`, `fontSize` |
| `stringSettings` | 16 | `language`, `modDbUrl`, **and the login** |
| `stringListSettings` | 5 | `modPaths`, `disabledMods`, `customPlayStyles` |

Plus `keyMapping` and `dialogPositions` at the top level.

**This needs an allowlist, not a denylist.** The login lives in `stringSettings` beside
`language` — `sessionkey`, `sessionsignature`, `playeruid`, `mptoken`, `entitlements`,
`useremail`, `playername`, the exact list in `ClientSession.Keys`. A feature that lets a pack
carry arbitrary string settings is a feature that publishes somebody's session to cairns.gg.
Machine-specific keys are the other half of the argument: `screenWidth`, `screenHeight`,
`gameWindowMode`, `audioDevice`, `glContextVersion`, `weirdMacOSMouseYOffset`. And two that
look like ordinary strings and are not: `modDbUrl` and `masterserverUrl` point the client at
its mod database and its server list, so a pack that could set them could redirect somebody
who imported it. Nothing should travel that was not deliberately allowed to.

The genuinely useful case is narrow and worth it anyway: when a model needs more joints than
the configured cap allows, the game throws with "In clientsettings.json, please try
increasing the \"maxAnimatedElements\" setting"
(`VintagestoryApi/Common/Model/Animation/Animation.cs:119`). A mod that cannot render
without it is a pack requirement, not a preference, and it is exactly the kind of thing a
pack should be able to state instead of leaving in its description.

## Keybinds

**Built.** `Cairn.Core/Hotkeys` reads them, `pack.json` carries them, `ClientHotkeys`
applies them at launch, and the pack pane has a Hotkeys tab. The rest of this section is
the reasoning; where it says "would", it now does.

The strongest case of anything in this document, and the one that was worth building first.

Twenty mods bring their own hotkeys, several of them land on the same key, and the author
works that out once by hand. Then every single person who imports the pack works it out
again, separately, from scratch. Nothing carries the answer.

It is not hypothetical. From a real pack's settings, 2026-08-09:

```json
"keyMapping": {
  "statushudconfiggui": { "KeyCode": 53, "SecondKeyCode": null, "Ctrl": false, ... },
  "xpdropsedit":        { "KeyCode": 53, ... }
}
```

`53` is `BackSpace` (`VintagestoryApi/Client/Input/EnumGlKeys.cs:294`). Two mods, one key,
in a pack that is otherwise ready to hand to somebody.

### Where the defaults come from

Hotkey defaults are registered in code —
`RegisterHotKey(hotkeyCode, name, key, type, ...)` (`IInputAPI.cs:123`) — and declared in no
file anywhere in the zip. There are exactly two ways to learn them: run the mod, or read it.

**Running is out.** Loading mod assemblies and calling into them against a stub API means
executing arbitrary downloaded code outside the game, on the launcher's authority, in a
runtime with no sandbox. The game ships `disableModSafetyCheck` because running this code
is a decision somebody makes; a launcher that quietly does it to read a keybind has made a
much larger promise than "Cairn installs your mods". It would also be fragile — mod startup
touches the world and registers blocks, and would fault constantly against a stub.

**So Cairn reads.** `HotkeyScan` walks the IL of each assembly and recovers the arguments
written literally at each call site. Bytes in, list out, nothing executed. The same scan
works on `VintagestoryLib.dll`, which is what makes the answer useful rather than merely
interesting: most collisions are between a mod and vanilla.

What survives all of that is the part worth carrying in the pack. Cairn can find the clash;
only a person can decide which mod moves, and that decision is what every importer would
otherwise make again.

### The format cooperates

`keyMapping` is a flat object at the top level of `clientsettings.json`, keyed by hotkey
`Code`:

```json
{ "KeyCode": 53, "SecondKeyCode": null, "Ctrl": false, "Alt": false, "Shift": false, "OnKeyUp": false }
```

It is a **delta** — most pack data paths have no entries at all, and no vanilla binding
appears in any of them. So a pack's keymap is a short, readable, diffable list of codes,
and merging it per code is the natural operation. `ClientSession.MergeInto` already writes
named keys into this file without disturbing the rest; this is the same act against a
different object.

### Rules

- **Seed a data path that has never launched; afterwards fill only codes with no entry.**
  A binding the player has set is theirs permanently.
- **Merge per code, never replace the object**, so a pack update that adds one binding does
  not undo the four somebody fixed themselves.
- **Say so.** "bound 6 mod hotkeys from the pack" in the pack's log, the way
  `ClientModPaths.Confine` reports what it dropped. A keyboard that silently changes
  behaviour is alarming in a way a mod path is not.
- **Unknown codes are harmless.** A code belonging to a mod that is not installed binds
  nothing. There is no validation to do and none to invent.

### Three answers, not two

A collision has a third resolution and it is often the right one: five mods want P, and for
four of them the honest answer is not another key but none at all. So a row can be

- **bound** to a combination,
- **unbound** — written `"none"` in the manifest and `KeyCode: -1` in the settings, which is
  what the game means by unset (its own `KeyCombination.ToString` answers "?" for a negative
  code; zero is `GlKeys.Unknown` and renders as that word). No key event carries either, so
  the hotkey never fires.
- or **reset**, which is the pack saying nothing about it — a different thing from unbinding,
  because the hotkey then follows whatever its mod ships, including a default the mod moves
  later.

Two unbound hotkeys do not collide with each other. Reporting that would fill the conflicts
list with the rows somebody had already dealt with.

### Where the accessibility line actually falls

Rebinding what somebody chose is the harm; filling in bindings for mods they have never run
is not. The keys that need protecting are the ones a player's hands know without looking:
`MovementControls`, `MouseControls` and `MouseModifiers`.

Those rows are **marked and held back, not forbidden**. The tab labels them "movement
control" or "mouse button" and the buttons wait for an Unlock click on that row. A hard lock
was the first attempt and it was the wrong rule: it caught `sitdown`, which is movement by
type, sits on G, and is exactly the key a mod is likely to want. An author moving it has a
reason; the unlock is there so it is a decision rather than a slip. The unlock is per row and
per session, and never saved.

The type itself is not in the settings JSON — it is an argument to `RegisterHotKey`, which is
why reading the IL is what makes the distinction available at all.

The companion mod can. `ICoreClientAPI.Input.HotKeys` is an
`OrderedDictionary<string, HotKey>`, and each `HotKey` carries `KeyCombinationType`,
`CurrentMapping` **and** `DefaultMapping` (`VintagestoryApi/Client/Input/HotKey.cs`). So
in-game it can:

- refuse to touch movement and character controls;
- tell a deliberate rebind from a mod's own default, by comparing the two mappings — which
  the settings file cannot express and Cairn therefore cannot infer;
- **detect the collision rather than only apply the fix**: walk the registry, find codes
  sharing a combination, and tell the author what still needs reconciling before they
  publish.

The first two are what the tab does today, from the files alone. The third — detecting
collisions from inside a running game — is still the mod's to do, and it is what would
close the gap left by registrations the scan cannot read.

### What the scan actually recovers

Measured over 74 mod zips and a 1.22.6 install, 2026-08-09:

| | |
|---|---|
| mod zips | 74 — **57 code mods**, 17 content-only, 0 source-only |
| assemblies registering hotkeys | 23 |
| hotkeys read from the pack's mods | 40 |
| hotkeys read from `VintagestoryLib.dll` | 68 |
| registrations with an argument computed at runtime | 5, reported rather than guessed |

Reading the game's own assembly is what makes the answer worth having: most collisions are
between a mod and vanilla rather than between two mods. In that corpus **five mods all
default to P**, and one wants F — which vanilla uses for tool mode.

Six of those forty needed the type initialiser reading as well as the call site:
`static readonly string HotkeyCode = "zoombutton"` compiles to a field load rather than an
inlined literal, and one mod wrote all five of its registrations that way. A field assigned
two different literals is left unknown — the walk does not follow branches, so it cannot
say which assignment runs.

What is left is genuinely out of reach from the files: two mods register through a helper
whose arguments come from its own callers, one builds its code from `Mod.Info.ModID`, and
one takes its key from its own config at runtime — there is no static default to find.

Vanilla overlaps itself on purpose (Shift is sneak, the click modifier and the middle
mouse button; Space is jump and fly), so a group that is entirely vanilla is not reported —
flagging it would teach people to ignore the list that also holds the real ones.

### Labels come out of the mods' own assets

A hotkey's *name* is rarely readable in the IL. Mods pass a lang key and let the game
resolve it — `Lang.Get("scribe:hotkey-scribepinhud")` — so the argument that reaches
`RegisterHotKey` is the result of a call and unknowable. What is knowable is the key it
asked for, and the translations ship in the same zips.

So `HotkeyScan` unwraps `Lang.Get` to the key it was given (folding `Concat` first, since
several mods build the key as `ModId + ":pickup-hotkey"`), and `HotkeyLang` reads every
`assets/<domain>/lang/en.json` in the pack into one table. **32 of the 40 rows get a real
sentence**: "CarryOn - Pick Up / Put Down" rather than `carryonpickupkey`.

One table for the whole pack rather than one per mod, because a key's domain names a mod
and not the zip it was registered from — XLib registers `xskills:hotkey-effectframehotkey`,
whose translation ships in the xSkills zip beside it.

Mods disagree about everything else, so the lookup tries in order: the key as written, the
key without its domain, a unique suffix match, then `hotkey-<code>` as a convention probe
for names that never reached the IL at all. Two candidates for a suffix means no answer —
a row labelled with the wrong mod's sentence is worse than one labelled with its own id.
The eight that stay as ids do so because nothing in the files says otherwise.

## Mod config, and ConfigLib

Mod config is not world config, and it needs no companion mod at all. It is files in
`<datapath>/ModConfig/`, which is already this pack's directory. Cairn writing them is the
same act as writing `clientsettings.json`.

ConfigLib (Maltiez; 626,925 downloads on 2026-08-09; both sides; requires ImGui) changes
what is *possible* rather than where anything lives: a unified in-game editor, configs for
content mods that otherwise have none, and by its own description it "allows changing values
in regular JSON patches, that can be valuable for server configurations and can make
tweaking other mods for server needs much easier". It does not require the mod to depend on
it — absent, asset defaults apply. A pack author who wants the GUI adds it to the pack like
any other mod.

Two consequences:

- **Seed, do not enforce.** A config file's schema belongs to the mod and versions with it.
  A config captured against mod 1.2 and replayed into a pack whose user has moved to 2.0 is
  how a mod ends up refusing to load. `PackData.EnsureDataPath` already has the right
  pattern: seed once, then it is the player's.
- **With ConfigLib installed the player can edit them in-game**, so the same drift question
  arrives here as everywhere else.

## One ownership rule

This is the same question on three surfaces, and it wants one answer rather than three:

> **Declare intent in the pack. Seed what has never existed. For what exists, apply only
> what is safe to apply, and report the rest.**

That is the shape sync already has with the lockfile: the manifest says what is wanted, the
lock says what is there, and the difference is shown rather than silently resolved. A
launcher that quietly rewrites a value an admin set with `/worldconfig`, or a graphics
setting somebody changed last night, has stopped being a launcher and started being an
opinion.

## Shape

```jsonc
"world": {
  "playStyle": "wildernesssurvival",
  "config": {
    "landcover": "0.8",          // creation-only: applied when the world is made
    "creatureStrength": "1.5",   // changeable: applied on every load
    "temporalStorms": "often"
  }
},

"keybinds": {
  "statushudconfiggui": "BackSpace",       // filled in only where the player has none
  "xpdropsedit": "Ctrl-BackSpace"          // hyphens: a plus is escaped to \u002B by
},                                         // System.Text.Json, and both forms are read

"clientSettings": {
  "maxAnimatedElements": { "atLeast": 230 }   // raise if below, never lower
}
```

Key names rather than the file's numeric `KeyCode`, because a manifest is hand-editable and
`53` is not something anybody should have to look up. `GlKeyNames.ToString` already maps the
enum to the names the game's own controls screen shows, so the two directions exist.

The `atLeast` verb is deliberate and specific to the numeric client settings. The joint cap
is "Limited by max amount of shader uniforms of around 60, but depends on the gfx card"
(`GlobalConstants.cs:59`), so a pack that hard-set it would be asserting something about
hardware it has never seen — and one that set it *downwards* from a value somebody else
needed would be pure vandalism.

All of it in `pack.json` rather than beside it, because it is declared intent, it is what an
author wants a follower to receive, and being in the manifest means it participates in the
publish fingerprint (`PublishRecord.WouldChange`) with no extra machinery.

## Open questions

- **Does mod config travel in the bundle?** `PackBundle` is manifest + lock, deliberately —
  intent and nothing else. World config values are small and belong in `pack.json` without
  argument. Mod config files are arbitrary JSON belonging to somebody else's mod, and
  carrying them would be the first time a bundle holds something that is not intent.
  Embedding them keyed by filename keeps them diffable and fingerprinted; a side channel
  does not. This is the one decision here that changes a shared format, so it wants settling
  before anything is written.
- **What is `customPlayStyles`?** It is a `stringListSettings` key, empty on a stock
  install. The assembly has `OnCopyPlaystyle`, `LoadPlayStyles` and
  `loadWorldConfigValuesFromPlaystyle` near the settings-key table, which reads like the
  Customize screen saving a user-defined preset — but that is inference from symbol names,
  not a fact. If it holds serialised playstyles, Cairn could install a pack's playstyle by
  writing a file it already writes, and the companion mod stops being needed on the client.
  The experiment is cheap and needs the GUI: snapshot `clientsettings.json`, create a world
  through Customize, diff that key.
- **Can `--withconfig` express nesting?** The server takes
  `--withconfig="{ key: 3, foo: 'value' }"` to override any config value, but the world seed
  lives at `WorldConfig.WorldConfiguration`. If nesting works, `cairn-server` need not write
  `serverconfig.json` at all.
- **Does `keyMapping` accumulate mod defaults, or only deliberate rebinds?** The two entries
  found in a real pack are both on Backspace, and nobody binds two things to one key on
  purpose — which suggests the game persists a mod's own default the first time it registers
  one. If so, an exported keymap can carry the collision rather than the fix, and the file
  alone cannot tell them apart. Comparing `CurrentMapping` against `DefaultMapping` in-game
  can. Worth confirming before deciding whether Cairn may build a pack's keymap by reading
  the file, or whether only the mod may produce it.
- **May a pack update change a running server's world?** Applying a changed pack value to an
  existing world is a gameplay change to somebody's live server, decided by its author rather
  than its admin. Reporting it is clearly right; applying it may not be.

## How this was checked

- Savegame schema: `sqlite3` read-only against a real pack save, 2026-08-09.
- Launch flags: string table of `VintagestoryLib.dll` from a 1.22.6 install.
- API surfaces and event ordering: the 1.22 `VintagestoryApi` source, quoted above with
  paths.
- Attribute counts: parsed out of `VSSurvivalMod/Properties/AssemblyInfo.cs`.
- Client settings buckets: a real `clientsettings.json`, key counts as listed.
- The keybind collision: `keyMapping` across every pack data path under `~/.cairn/packs`,
  2026-08-09. Two entries in one pack, both `KeyCode` 53; the rest empty.
- ConfigLib: `https://mods.vintagestory.at/api/mod/configlib`, 2026-08-09.
- **Not** independently verified: that `serverconfig.json`'s
  `WorldConfig.WorldConfiguration` seeds a new savegame and is inert afterwards. That comes
  from the server configuration reference and should be confirmed before `cairn-server`
  relies on it.
