# Cairn

A modpack manager for **client-side** Vintage Story mods.

Vintage Story already handles the server half of this well: join a server that has mods
you lack and it offers to download them from ModDB, dropping them in
`<dataPath>/ModsByServer`. What it has no concept of is a *curated client-side set* —
the QoL mods you personally want, versioned, reproducible, and shareable. That is what
Cairn manages.

Cairn does not replace the game's ModDB integration; it fills the gap next to it.

## How it works

A **pack** is a manifest plus a lockfile plus a directory of mod zips:

```
~/.cairn/packs/<id>/
  pack.json        declared intent - commit this, share this
  pack.lock.json   exactly what got installed, with hashes
  Mods/            the zips, handed to the game via --addModPath
```

```json
{
  "id": "anego",
  "name": "Anego Server",
  "gameVersion": "1.22.5",
  "connect": "anego.example.com:42420",
  "mods": [
    { "modid": "glassview" },
    { "modid": "unchisel", "version": "1.2.0" }
  ]
}
```

`sync` installs **what the lockfile says**. It resolves against ModDB only when it has
no choice: a mod never installed, a pin that has moved, or a pack retargeted at another
game version. It records the exact version and SHA-256, and deletes zips no longer part
of the pack. Mods stay zipped — the game loads zip archives directly.

That makes launching safe. Sync runs on every **Play**, and mods break saves, so a launch
must not be able to move a pack's mods underneath it — a settled pack syncs without
touching the network at all. Updating is something you ask for:

```
cairn-cli update anego --check     # what would move
cairn-cli update anego             # move all followed mods
cairn-cli update anego olla        # move just this one
```

or **Check for updates** in the launcher, which offers each one per row and an
**Update all**. A mod pinned to an exact version is never offered an update, because a pin
is an instruction to stay put.

`launch` syncs and then starts the game with the pack stacked on:

```
Vintagestory --dataPath ~/.cairn/packs/anego/data \
             --addModPath ~/.cairn/packs/anego/Mods \
             --connect anego.example.com:42420
```

### Each pack has its own worlds, but you only log in once

`Saves/`, `ModConfig/`, `Playerdata/` and `ModsByServer/` all live under the data path, and
the game gives no way to relocate them individually — only `Logs` has an override. So packs
share a data path or they share nothing.

Sharing one meant every world was reachable from every pack whatever its mods, and opening a
save against a different mod set is a leading way to ruin it. Packs therefore get their own
data path at `packs/<id>/data`.

Login lives under the data path too, which is why packs used to share one. Cairn carries the
session instead: seven keys inside `clientsettings.json` — `sessionkey`, `sessionsignature`,
`playeruid`, `mptoken`, `entitlements`, `useremail`, `playername` — are recorded in
`~/.cairn/session.json` and merged into each pack before it launches. Merging *named keys*
rather than copying the file is the point: the login follows you, while keybinds, graphics
settings and dialog positions stay per-pack.

Whichever copy was written most recently wins, so signing in inside any pack reaches the
others, and a session the game rotates mid-play is not lost. Cairn only ever **reads** your
own Vintage Story data path — it seeds from it and never writes to it.

Because a pack's data is inside the pack, **deleting a pack deletes its worlds**. The
confirmation itemises what goes — worlds by name and size, mods by count and size — and
says how much disk it hands back. A world made under a pack's mod set generally cannot be
opened without it, so leaving one behind would strand data nothing can read.

This applies to every pack, with no way to turn it off. Sharing a data path was briefly a
per-pack choice, for packs made before this existed — which presented the failure mode above
as a supported way to run. Those packs simply get a data path on their next launch instead.

Worlds already in your own Vintage Story data path stay there. They are your ordinary saves,
Cairn cannot know which pack — if any — they belong to, and claiming them would take them
away from plain Vintage Story too. They remain reachable by launching the game normally; to
put one inside a pack, copy its `.vcdbs` into `packs/<id>/data/Saves`.

`--addModPath` is still *additive* — the game always also searches `<install>/Mods` and
`<dataPath>/Mods` — but with a per-pack data path that second directory is the pack's own, so
nothing leaks between packs.

## Usage

The launcher is the primary interface — everything can be done from it, no terminal
required:

- **Pack list** with New pack / Refresh; the new-pack form takes id, display name, game
  version and an optional server.
- **Mods** tab — what the pack contains, what is pinned, and what is actually installed.
  Remove a mod, or fetch its compatible releases and pin an exact version.
- **Add mods** tab — search ModDB and add a result to the pack.
- **Settings** tab — rename, change or clear the server, export, or delete the pack.
- **Changing the game version** is its own step, in Settings: pick a target from the
  versions ModDB publishes and press **Check…**. Nothing is written yet. A dialog lists
  every mod and what would happen to it — `keeps`, `updates`, `untested`, `breaks`,
  `pin fails` — worst first, because the reason to say no should not need scrolling to.
  The list scrolls and the buttons stay put, so a forty-mod pack reads the same as a
  three-mod one. Only **Change to X** commits it; closing the dialog any other way does
  not. Retargeting invalidates the lockfile for every mod, so
  this is a bigger change than it looks: it can move several mods at once, or leave one
  behind entirely.

  A downgrade additionally warns about the pack's own worlds, since Vintage Story upgrades
  a save when a newer build opens it and will not open one saved by a newer build. Mods
  that ModDB could not be asked about are reported as *could not be checked* rather than
  as working — a preview is worth nothing if it guesses.
- **Log** tab — what Cairn did, plus the game's own log. **Game log** pulls the tail of
  `client-main.log` into the pane and **Open logs folder** opens the directory, because
  when the game closes on startup the answer is in its log and nobody should have to know
  where that lives. A non-zero exit pulls the errors and warnings in by itself, with
  repeated lines collapsed — a failing render call logs the same line dozens of times a
  second and would otherwise bury the cause.
- **Play** syncs the mod directory and then launches. It is the only button that
  needs pressing; `cairn-cli sync` exists for reconciling without starting the game.

```bash
dotnet run --project src/Cairn.App
```

The CLI (`cairn-cli`) drives the same `Cairn.Core` engine, so every action is also
scriptable:

```
cairn-cli info                          show the detected install and data path
cairn-cli list                          list packs
cairn-cli init <id> [--game <version>] [--connect host:port]
cairn-cli add <id> <modid> [version]    add a mod to a pack
cairn-cli remove <id> <modid>           remove a mod from a pack
cairn-cli delete <id>                   delete a pack and its mods
cairn-cli search <text>                 search ModDB
cairn-cli sync <id>                     resolve + download
cairn-cli launch <id>                   sync, then start the game
```

### Sharing a pack

`pack.json` is already the shareable part — declared intent, meant to be committed or
handed around. Export bundles it with the lockfile into one file:

```
cairn-cli export anego -o anego.cairn.json     # omit -o to print it
cairn-cli export anego --no-lock                # intent only
cairn-cli import anego.cairn.json              # or a https:// URL
cairn-cli import shared.json --id anego-copy    # when the id collides
cairn-cli import shared.json --loose            # track newest instead of pinning
```

In the launcher: **Import…** in the sidebar (paste the file, or a URL), and **Export…** in
a pack's Settings tab.

Including the lock is what makes a shared pack *reproducible* rather than merely similar.
On import each mod is pinned to the version the author had, and the author's checksums
travel with it, so the first sync verifies the recipient got identical bytes:

```
$ cairn-cli sync anego          # lock says a checksum that does not match what downloaded
  x glassview   1.3.0 does not match the locked checksum — refusing it
```

Verified end to end: an exported pack imported into a clean Cairn home produced
byte-identical files (matching SHA-256 for every mod), and a deliberately altered
checksum was refused rather than installed.

Both front-ends mutate packs only through `PackStore`, so validation cannot be bypassed
by using one instead of the other — including on import, where a hostile `id` like
`../../etc` is rejected the same way it is on creation. Pack ids become directory names, so they are
restricted to letters, digits, `-` and `_` — an id like `../../etc` is refused rather
than escaping the store.

## Version strings: write bare versions

The game parses versions with `int.TryParse` per segment, so anything unparseable
silently becomes `0`:

```
">=1.22.0"  ->  [0, 22, 0, 3]      constraint becomes 0.22.0
"^1.22"     ->  [0, 22, 0, 3]
"garbage"   ->  [0, 3]
```

A dependency of `">=1.22.0"` is therefore satisfied by **1.19**, and a mod declaring it
advertises as installable on versions it cannot load on. It does not fail loudly. Cairn
refuses such strings in a manifest rather than passing them through — write `"1.22.5"`.

## Layout

```
src/Cairn.Core/     engine: ModDB client, manifest/lock, sync, launch
src/Cairn.Cli/      headless front-end (cairn)
src/Cairn.App/      Avalonia GUI launcher
tests/               unit, conformance and headless-UI tests
```

`Cairn.Core` references **nothing** — not even the game's assemblies — so the project
builds in a clean checkout or CI container with no Vintage Story install. Its version
comparator is a deliberate port of `Vintagestory.API.Config.GameVersion`, held to the
original by conformance tests that run the real implementation side by side over a corpus
of version strings. Those tests compile only when `VINTAGE_STORY` is set, and are skipped
otherwise.

## Installing the game

Cairn can install Vintage Story itself, from the official manifest at
`api.vintagestory.at`. Downloads need no authentication — the licence check happens at
in-game login, not at download — so Cairn can fetch the game, but you still need a
purchased account to play it.

```
cairn-cli games                     installed and available versions
cairn-cli games install 1.22.5      download, verify md5, unpack
cairn-cli games remove 1.22.5
```

or **Preferences → Storage** in the launcher, which is where installed versions are removed
and private runtimes managed. Nothing nags about a missing version: pressing **Play** fetches
whatever the pack needs, so that screen exists mainly to give the disk space back — it also
reports what Cairn is using and can empty its caches.

Both list the machine's own install alongside Cairn's, because a pack launches from it
whenever the version matches — a list that omitted it would disagree with what actually
runs. Cairn will not remove one it did not install.

Versions land in `~/.cairn/games/<version>/`, so several can coexist and each pack
launches the one its `gameVersion` names:

```
using game 1.21.5 at ~/.cairn/games/1.21.5
```

Two macOS details this avoids by construction. The install directory is **not** named
`*.app`: the shipped tarball has a flat layout with `Info.plist` at the top level and no
`Contents/`, so an `.app` suffix makes `codesign` treat it as a bundle, fail to find
`_CodeSignature/CodeResources`, and report the game as damaged. And nothing downloaded
this way is quarantined, because `com.apple.quarantine` is applied by browsers and
LaunchServices rather than by HTTP clients.

**Windows takes a different route.** It publishes only an installer `.exe` — there is no
client archive, the sole Windows zip being the *server* — so there is nothing to unpack.
That installer is Inno Setup 6, which takes a target directory and runs headless, so Cairn
downloads it, checks its md5, and runs it into the version's directory:

```
vs_install_win-x64_1.22.5.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART \
                              /NOICONS /MERGETASKS=!desktopicon /DIR=…
```

`/DIR` is what makes side-by-side versions possible at all: the wizard's default is one
`%APPDATA%\Vintagestory` for every version, so a pack pinned to 1.21 could never coexist
with one on 1.22. The install is per-user, so it normally raises no UAC prompt; declining
one is reported as a cancellation rather than a failure.

Icons take two switches because Inno splits them: `/NOICONS` covers Start menu entries that
have no associated Task, while the desktop shortcut **is** a Task and is reached only
through `/MERGETASKS`. That distinction matters — there is one shared "Vintage Story"
desktop shortcut, and every installer rewrites it to point at itself, so installing 1.22 and
then 1.21 would leave the player double-clicking into the older version.

One wrinkle worth knowing: every version's installer carries the same Inno Setup `AppId`,
so installing a managed copy repoints the player's existing Add/Remove Programs entry at
Cairn's directory — uninstalling "Vintage Story" from Settings would then remove the wrong
copy, and dangle once Cairn removed that version. Cairn captures the entry beforehand and
restores it afterwards, leaving the machine as it found it. Only `HKCU` is touched, since a
per-user install cannot write a machine-wide entry in the first place.

The desktop shortcut gets the same treatment, as a second line of defence: `/MERGETASKS`
only helps if the task is named the conventional `desktopicon`, and if it is not, the switch
is silently a no-op. So the shortcut is captured before installing and put back afterwards —
one the installer created is removed, one it overwrote is restored byte for byte. Only
`*.lnk` files whose name contains "vintage" are considered, so nothing else on the desktop
can be disturbed by an install that runs for several minutes.

### Private .NET runtimes

Each game version pins its own .NET major — 1.21 needs .NET 8, 1.22 needs .NET 10 — and
the game bundles no runtime. Rather than requiring several system-wide installs, Cairn
can keep private copies and point the game at the right one:

```
cairn-cli runtimes                  what cairn manages
cairn-cli runtimes install 8        fetch a private .NET 8 (sha512-verified)
cairn-cli runtimes remove 8.0.29
```

or **Install its .NET** in Preferences → Storage, enabled for an installed game whose
runtime is missing.

They live in `~/.cairn/runtimes/<version>-<rid>/` and are selected automatically at
launch. Demonstrated on a machine with only .NET 10 installed:

```
$ .../games/1.21.5/Vintagestory --version        # no private runtime
You must install or update .NET to run this application.
Framework: 'Microsoft.NETCore.App', version '8.0.0' (x64)

$ DOTNET_ROOT=~/.cairn/runtimes/8.0.29-osx-x64 .../Vintagestory --version
1.21.5
```

This is safe rather than invasive: hostfxr falls back to the machine's registered install
when `DOTNET_ROOT` holds no usable framework, so a private runtime can rescue a version
that would not otherwise start but cannot break one that already works. Sources are
Microsoft's public release metadata (`releases-index.json` → per-channel `releases.json`),
which publishes a SHA512 per file.

## Requirements

**Cairn needs nothing installed.** Release builds are self-contained single files, so
there is no .NET prerequisite — download one binary and run it. A launcher that itself
required a runtime install could not help a user who has neither.

**The game does need .NET.** Vintage Story is framework-dependent: its
`Vintagestory.runtimeconfig.json` asks for `Microsoft.NETCore.App` 10.0.0 and it bundles
no runtime. The published clients are **x64 on every platform**
(`vs_client_osx-x64`, `vs_client_linux-x64`, `vs_install_win-x64`), so they need an **x64**
.NET 10 — which matters on Apple Silicon, where a default .NET install is arm64 and cannot
host the game.

Cairn checks rather than assumes. It reads the architecture out of the game's Mach-O/PE/ELF
header and the required framework out of its runtimeconfig, then looks for a matching
runtime and reports what it found:

```
game arch   : X64
needs .NET  : 10.0.0
runtime     : .NET 10.0.10 (x64) at /usr/local/share/dotnet/x64
```

When launching, it sets **both** `DOTNET_ROOT` and the architecture-specific
`DOTNET_ROOT_X64` to that root. The arch-specific variable takes precedence for an apphost,
so setting only `DOTNET_ROOT` would lose to a stale `DOTNET_ROOT_X64` inherited from the
user's shell; setting both makes precedence irrelevant. When no suitable runtime is found
Cairn deliberately sets nothing — hostfxr falls back to the machine's registered install
when `DOTNET_ROOT` holds no usable framework, so writing a bad value cannot help and
clobbering a good one could hurt.

## Build

```bash
dotnet build
dotnet test tests/Cairn.Core.Tests/Cairn.Core.Tests.csproj   # 68 tests, 72 with the game
dotnet tests/Cairn.App.Tests/bin/Debug/net10.0/Cairn.App.Tests.dll   # 16 UI tests
```

Building to test, on whatever machine you are on:

```bash
./dev.sh              # build for this host only  (~5s)
./dev.sh --run        # build, then launch it
./dev.sh --no-sign    # skip code signing         (~4s)
./dev.sh --cli        # CLI only                  (~2s)
```

Prefer this over `dotnet run` on macOS. `dotnet run` uses whatever SDK is on `PATH`, and
if that SDK is x64 the launcher runs under Rosetta and feels sluggish; publishing for the
host rid is what produces a native build. On macOS `dev.sh` produces the `.app` bundle.

Release artifacts, all platforms at once:

```bash
./build-release.sh                 # osx-arm64, osx-x64, win-x64, linux-x64
./build-release.sh linux-x64       # or just one
```

Self-contained, single-file, compressed — roughly **36 MB** for the CLI and **47 MB** for
the launcher per platform. Cross-publishing works from any host. Note the RIDs here are
Cairn's *own* binary: the arm64 build exists so the launcher runs natively on Apple
Silicon, and it still resolves an x64 runtime for the x64 game.

### macOS application bundle

```bash
./build-macos-app.sh                       # artifacts/osx-arm64/Cairn.app
ICON=path/to/icon.png ./build-macos-app.sh # with an icon
SIGN_IDENTITY="Developer ID Application: …" ./build-macos-app.sh
```

Produces a real bundle — `Contents/MacOS`, `Contents/Info.plist`, `Contents/_CodeSignature`
— so it gets a Dock tile, proper foreground activation and its own name in the menu bar.
The launcher binary is `Contents/MacOS/cairn`; the CLI ships alongside it at
`Contents/MacOS/cairn-cli`, so one download provides both.

Deliberately **not** single-file: measured on an M-series machine, warmed, ten runs each,

| packaging | startup |
|---|---|
| plain directory (what the bundle uses) | **38 ms** |
| single-file, compressed | 78 ms |

Signing costs nothing measurable; the difference is single-file self-extraction. Larger on
disk as a result (113 MB vs 47 MB) — that is the trade.

Two macOS details worth knowing:

- `Application.Name` must be set in `App.axaml`. Without it Avalonia reports itself to
  LaunchServices as "Avalonia Application" regardless of `CFBundleName`.
- The bundle is signed with `codesign --deep` because macOS classifies managed `.dll`
  files as nested code by extension; signing only the bundle leaves them unsigned and
  `--verify --strict` fails. Apple discourages `--deep` for Developer ID submissions, so
  notarising would mean signing each nested binary explicitly instead.

Trimming is deliberately off — Avalonia leans on reflection, so it would need testing
per release rather than being assumed safe.

The UI tests render the real window on Avalonia's headless platform and assert on the
visual tree. That is deliberate: Avalonia resolves bindings at runtime, so a stale
binding path fails silently and the launcher would start looking fine and do nothing.
Note that a `TabControl` only realises the selected tab, so a test asserting on controls
in another tab has to select it first.

`Cairn.App.Tests` uses **xunit v3** because `Avalonia.Headless.XUnit` 12.x requires it;
pairing it with xunit v2 compiles and then discovers zero tests. xunit v3 projects are
self-hosting executables, hence running the dll directly rather than `dotnet test`.

Requires .NET 10. On macOS the game is a framework-dependent **x86_64** apphost, so it
needs an x64 .NET installed via Microsoft's `.pkg` (which writes
`/etc/dotnet/install_location_x64`). Cairn itself is architecture-agnostic — it only
spawns the game, and reads `VintagestoryAPI.dll` metadata without loading it.
