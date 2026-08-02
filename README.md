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
- **Settings** tab — rename, write a description, change or clear the server, export, or
  delete the pack. The description travels with the pack, so it is what a recipient reads
  in the import dialog and on a published pack's page; capped at 280 characters, with the
  Save button held until it fits rather than cutting it silently later.
- **Changing the game version** is its own step, in Settings: pick a target from the
  versions ModDB publishes and press **Check…**. Nothing is written yet. A dialog lists
  every mod and what would happen to it — `keeps`, `updates`, `untested`, `breaks`,
  `pin fails` — worst first, because the reason to say no should not need scrolling to.
  The list scrolls and the buttons stay put, so a forty-mod pack reads the same as a
  three-mod one. Only **Change to X** commits it; closing the dialog any other way does
  not. Retargeting invalidates the lockfile for every mod, so
  this is a bigger change than it looks: it can move several mods at once, or leave one
  behind entirely.

  A downgrade additionally warns about the pack's own worlds. The game is more forgiving
  here than it first appears — opening a save that a newer build touched produces a
  warning, `"versionmismatch-savegame": "Was opened in a newer version of the game, might
  not load correctly"`, not a refusal. The one-way step is the *file format* upgrade, which
  prompts separately ("This world uses an old file format that needs upgrading … It is
  also suggested to first back up your savegame") and is keyed on
  `GameVersion.DatabaseVersion` rather than the version string. That number is still `2`
  and has not moved once in this source history, so it is a rare event and not something a
  patch-level change brings on. Mods that ModDB could not be asked about are reported as
  *could not be checked* rather than as working — a preview is worth nothing if it guesses.
- **Log** tab — what Cairn did, plus the game's own log. **Game log** pulls the tail of
  `client-main.log` into the pane and **Open logs folder** opens the directory, because
  when the game closes on startup the answer is in its log and nobody should have to know
  where that lives. A non-zero exit pulls the errors and warnings in by itself, with
  repeated lines collapsed — a failing render call logs the same line dozens of times a
  second and would otherwise bury the cause.
- **Preferences** is two tabs. **Overview** opens first: the version, where Cairn keeps its
  own files, and the interface scale. **Storage** is the disk-usage screen. On macOS the
  application menu's **About Cairn** opens Overview — it used to land on the disk-usage
  screen, which made it look like the wrong menu entry.

  The version is read from the assembly rather than kept in a constant, so there is one
  place a version is decided — the tag a release was cut from — and a build nobody stamped
  says `dev` rather than a number that will be believed. Local builds stamp `0.0.0` for
  exactly that reason; a default of `0.1.0` had dev builds claiming to be a release that
  existed.

  **Interface size** scales 100% to 200%, applied as you pick it and remembered. It scales
  the whole window rather than the font size, so buttons, rows and spacing grow with the
  text instead of the text getting cramped inside controls that stayed put — and it will
  not grow a window past the display it is on.
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
cairn-cli init <id> [--game <version>] [--connect host:port] [--description text]
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
The author's lock travels with the pack and their checksums with it, so the first sync
installs their exact versions and verifies the recipient got identical bytes:

```
$ cairn-cli sync anego          # lock says a checksum that does not match what downloaded
  x glassview   1.3.0 does not match the locked checksum — refusing it
```

Verified end to end: an exported pack imported into a clean Cairn home produced
byte-identical files (matching SHA-256 for every mod), and a deliberately altered
checksum was refused rather than installed.

The lock does that job alone, so import leaves the manifest as the author wrote it. Mods
they deliberately pinned arrive pinned — a pin is transmitted intent — and the rest arrive
*followed*: installed at the author's exact version, still offered updates later. Writing
the lock's versions into the manifest instead would pin everything, and a pinned mod is
never offered an update, so every imported pack would be frozen the day it landed.
`--loose` is the opposite choice, and discards the lock as well as the pins.

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

Game versions are managed with `cairn-cli games` (above)
or **Preferences → Storage** in the launcher, which is where installed versions are removed
and private runtimes managed. **Clean up** there sweeps every version no pack targets, plus
any private runtime left with nothing to run and the icon and mod-detail caches — one sweep
for everything that comes back on its own. It lists each item with its size and what it
frees, and asks first. Nothing it removes is irreplaceable, which is what makes it safe to
offer: Play downloads whatever a pack needs. A pack whose manifest will not load blocks the
sweep rather than being treated as needing nothing. Nothing nags about a missing version: pressing **Play** fetches
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
dotnet test tests/Cairn.Core.Tests/Cairn.Core.Tests.csproj   # 393 tests, 397 with the game
dotnet tests/Cairn.App.Tests/bin/Debug/net10.0/Cairn.App.Tests.dll   # 193 UI tests
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

### Testing against a local cairns

```bash
cd ../cairns && ./dev.sh          # the server, in its own terminal
./dev.sh --local                  # a launcher pointed at it
```

`--local` sets `CAIRNS_SERVER=http://localhost:5080` *and* `CAIRN_HOME=~/.cairn-dev`,
because the second half is not optional: publishing writes a `cairns.json` into the pack
recording where it went, and doing that to a real pack leaves it claiming to live at a
localhost URL that stops existing when the server does. `--server URL` and `--home DIR`
set them separately.

Sign-in mail is printed to the server's terminal rather than sent — see the cairns README.

Import refuses plain `http://` — a pack names the mods, their download URLs *and* their
hashes, so anyone able to rewrite one in flight picks what gets installed and writes
hashes to match. **Loopback is exempt**, because those packets never leave the machine:
`http://localhost:5080/you/pack.json` imports, `http://cairns.gg/…` does not. The check
is `PackSources`, in Core, so both front-ends answer it the same way.

## Opening a pack from the web

A pack page has an **Open in Cairn** button behind a `cairn://` link, which is the whole
point of the scheme: most people will never install the CLI.

```
cairn://cairns.gg/dizzyd/anego   ->   https://cairns.gg/dizzyd/anego.json
cairn://localhost:5080/me/pack   ->   http://localhost:5080/me/pack.json
```

A host and two segments is the entire grammar (`PackUri`), and the launcher puts back the
https itself — loopback excepted, so a link on a local server works while testing. There is
deliberately no URL nested inside the URL: anybody's web page can contain one of these, and
a nested address would mean parsing an attacker's string and deciding which schemes to
honour.

**Following a link never installs anything.** It fetches the document and shows what is in
it — pack name, the author's description, who published it, the host it came from, the game
version, every mod and the exact version each would install, and the server it would launch
into if it has one — then waits for a yes or a no. Saying yes still only writes a manifest;
mods arrive on a sync they ask for.

That mod list comes from the **lockfile**, not the manifest: a mod pulled in to satisfy a
dependency is in one and not the other, so a list built from the manifest would show fewer
mods than actually get installed. Those rows are marked `dependency`.

### An imported pack stays its author's

Importing a document that carries a canonical URL records the pack as a **follower** of it
(`cairns.json`, `PackRole.Follower`). The Share button is then absent rather than present
and refusing, with a line under the pack name saying where it came from — a control that
simply vanishes reads as a bug, not a rule.

The rule is not the hidden button. `cairn-cli publish` refuses the same packs, and
`PublishPack` checks before doing anything, because a bindable command is reachable
whether or not something is drawn for it. A bundle imported from a *file* has no canonical
URL, so nobody owns it and it stays yours to publish.

**Export goes too**, and not only for the same reason. A `.cairn` file carries the manifest
and the lock and nothing else — no canonical URL, no author — so a copy made from somebody
else's pack reaches the next person as an unowned one they may publish freely. Passing on
the link keeps it attributed, and keeps whoever you sent it to getting the author's updates.

### Publishing the same thing twice

A revision that differs from its predecessor in nothing but its number tells every follower
there is an update and then has none for them, so Publish is refused when nothing has
changed — in the Share window, where the button dims and says which revision it matches,
and in `cairn-cli publish`.

"Changed" is not only the bytes. Visibility and whether the server address is included are
part of what was published, so flipping a pack from unlisted to public is a real change
with nothing to show for it in the document. That is also why the window still opens on an
unchanged pack: those choices are the reason to come back to one. `PublishRecord.WouldChange`
is the whole rule, and both front-ends ask it.

**The address is fixed once published.** On cairns the URL *is* the pack, so publishing the
same one under a different slug does not move it — it creates a second pack and leaves the
first live under the same name, which is how you end up with two identical-looking packs
and no idea which is which. The Share window makes the field read-only after the first
publish; `cairn-cli publish --slug` refuses and points at `unpublish`.

An **unlisted** pack is marked as such beside its URL, and on its page on the site. The two
are indistinguishable from outside, and which one a pack is decides whether passing the
link around is sharing it or publishing it.

That dialog, and not the scheme, is what makes a link from a stranger safe to click: the
answer to "this could be anything" is to say plainly what it turned out to be. An address
pasted into the import box gets the same treatment, since a URL from a chat message tells
you no more about its contents than one on a page. Text or a file you are holding imports
directly.

A name already in use is caught on the form rather than after agreeing — it is the one
thing on that dialog that was fixable, and finding out afterwards means the dialog is gone
and an error is in its place.

The link reaches the app two ways, and both are wired: macOS hands a *running* instance the
URL through an activation event, while Windows and Linux launch the handler afresh with it
in `argv`. Handling either alone leaves half the platforms dead.

### Registration is macOS-only so far

| platform | how | state |
|---|---|---|
| macOS | `CFBundleURLTypes` in the bundle, written by `build-macos-app.sh` | **works** — verified cold and with the app already running |
| Windows | `HKCU\Software\Classes\cairn`, needing either an installer or self-registration on start | **not done** — the link does nothing |
| Linux | a `.desktop` file carrying `MimeType=x-scheme-handler/cairn` | **not done** — the link does nothing |

Windows also wants single-instance handling before this is pleasant there: with no installer
it launches a *new* copy per click, and two launchers sharing one `~/.cairn` can race.

On macOS the scheme binds once LaunchServices has seen the bundle somewhere it scans, so a
freshly built `artifacts/` copy may need `lsregister -f` before a link finds it.

When a click seems to do nothing, the app writes one line to stderr saying whether the link
arrived and whether it was refused — the three causes (never delivered, delivered and
refused, worked but the window is behind something) otherwise look identical:

```bash
open --stdout /tmp/cairn.log --stderr /tmp/cairn.log -n artifacts/osx-arm64/Cairn.app
```

Release artifacts, all platforms at once:

```bash
./build-release.sh                 # osx-arm64, osx-x64, win-x64, linux-x64
./build-release.sh linux-x64       # or just one
```

Self-contained, single-file, compressed — roughly **36 MB** for the CLI and **47 MB** for
the launcher per platform. Cross-publishing works from any host. Note the RIDs here are
Cairn's *own* binary: the arm64 build exists so the launcher runs natively on Apple
Silicon, and it still resolves an x64 runtime for the x64 game.

### Cutting a release

Push a tag. `.github/workflows/release.yml` runs the tests, builds all four artifacts and
opens a **draft** release with them attached.

```bash
git tag -a v0.2.0 -m "v0.2.0" && git push origin v0.2.0
```

| platform | artifact | built on |
|---|---|---|
| macOS (Apple silicon) | `cairn-<v>-macos-arm64.zip` | `macos-latest` |
| macOS (Intel) | `cairn-<v>-macos-x64.zip` | `macos-latest` |
| Windows | `cairn-<v>-windows-x64.zip` | `ubuntu-latest`, cross-published |
| Linux | `cairn-<v>-linux-x64.tar.gz` | `ubuntu-latest`, cross-published |

Only macOS needs its own runner, because the `.app` bundle needs `codesign` and `plutil`;
the others are single-file binaries with no platform tooling behind them. The tag becomes
`CFBundleShortVersionString`, which is what Finder shows and what macOS compares to decide
whether an install is an upgrade.

Three details that are load-bearing:

- **`ditto`, not `zip`,** for the bundle. A `.app` holds symlinks and extended attributes,
  and plain `zip` flattens them into something macOS calls damaged.
- **`.tar.gz` for Linux**, because zip does not carry the executable bit and a download
  that needs `chmod +x` before it runs is a download that gets reported as broken.
- **Promotion is conditional, publishing is not.** Downloads come from R2, not from
  GitHub — the release here is a record of what was built. Uploading a version reaches
  nobody, because the files sit at a path nothing links to; moving `releases/latest.json`
  is what ships them, and that only happens when the macOS builds were notarised. An
  unnotarised build still uploads, still gets a URL, and simply is not made the download.

  Promote one anyway with a single `aws s3 cp latest.json`, if that is deliberate.

`workflow_dispatch` builds everything without publishing, which is how to find out a build
is broken before there is a tag claiming otherwise.

### Publishing to Cloudflare R2

**This is the distribution channel.** GitHub holds the source and a copy of each build;
people download from `download.cairns.gg`. Two secrets and three variables; with
`R2_ACCESS_KEY_ID` unset the job says so and does nothing.

| name | kind | what it is |
|---|---|---|
| `R2_ACCESS_KEY_ID` | secret | from an R2 API token with Object Read & Write |
| `R2_SECRET_ACCESS_KEY` | secret | the other half of it |
| `R2_ENDPOINT` | variable | `https://<account-id>.r2.cloudflarestorage.com` |
| `R2_BUCKET` | variable | the bucket name |
| `R2_PUBLIC_URL` | variable | the custom domain, e.g. `https://download.cairns.gg` |

The endpoint and bucket are variables rather than secrets so they appear in the logs. A
masked bucket name makes a failed upload much harder to read, and neither is a secret.

R2 speaks S3, so the client is the AWS CLI that runs on the runner already — with three
differences from a typical S3 provider, each of which is a way this quietly breaks:

- **No `--acl`.** R2 does not implement per-object ACLs and rejects one rather than
  ignoring it. What makes a file readable is the bucket's custom domain, which is a
  property of the bucket rather than of each object.
- **`AWS_DEFAULT_REGION=auto`.** R2 has one region, and the first label of the endpoint is
  the account id — so deriving the region from the endpoint, which is right for providers
  whose endpoint names their region, would sign requests for a region that does not exist.
- **Checksums only when required.** Recent AWS CLI versions add integrity checksums by
  default that not every S3-compatible provider accepts; asking for them only when needed
  survives CLI updates instead of breaking on one.

```
releases/1.2.3/cairn-1.2.3-macos-arm64.zip     immutable, cached for a year
releases/1.2.3/…                               every other artifact, plus SHA256SUMS
releases/1.2.3/manifest.json                   what that version was
releases/latest.json                           what to offer, cached for 5 minutes
```

**Versioned paths, never overwritten.** Somebody who linked a build a year ago should still
get that build, byte for byte — which is also what makes it safe to cache them forever,
since a URL cannot come to mean something else.

That includes the manifest. `latest.json` is rewritten every release, so a version's own
manifest is kept beside its artifacts — otherwise the sizes and checksums of 1.2.3 stop
existing the moment 1.2.4 ships, while the files they describe are still up. It is written
whether or not the version is promoted: a version nothing points at is still a version that
happened.

`latest.json` is the only mutable object, and holds the same bytes as the promoted
version's manifest rather than a pointer to it — one request for a reader, and no window in
which it names a manifest that is not up yet:

```json
{
  "version": "1.2.3",
  "publishedAt": "2026-08-01T17:50:51Z",
  "files": [
    { "platform": "macos-arm64", "name": "cairn-1.2.3-macos-arm64.zip",
      "url": "https://…/releases/1.2.3/cairn-1.2.3-macos-arm64.zip",
      "size": 48291043, "sha256": "f813d49e…" }
  ]
}
```

That is what a downloads page on the site should read, rather than a hardcoded list that
goes stale the release after somebody remembers to update it.

### Signing and notarising the macOS builds

This is the **direct-download** path, not the App Store one: somebody downloads a zip and
it opens. Nothing here submits an app anywhere, and none of it requires the sandboxing the
App Store insists on.

Two names in the table below suggest otherwise and are worth reading past. *Developer ID
Application* is the certificate Apple provides **for distribution outside the App Store** —
the store uses a different one. And an *App Store Connect API key* is just Apple's
credential system for their APIs; `notarytool` authenticates with it whether or not the
App Store is ever involved.

Five repository secrets turn it on. With none of them the workflow ad-hoc signs exactly as
it did before, and the release notes say so — there is no flag to remember.

| secret | what it is |
|---|---|
| `MACOS_CERTIFICATE` | the **Developer ID Application** certificate and key, exported as `.p12`, then `base64 -i cert.p12 \| pbcopy` |
| `MACOS_CERTIFICATE_PASSWORD` | the password set when exporting the `.p12` |
| `APPLE_NOTARY_KEY` | an App Store Connect API key (`.p8`), base64-encoded the same way |
| `APPLE_NOTARY_KEY_ID` | the key's ID, e.g. `ABCD123456` |
| `APPLE_NOTARY_ISSUER` | the issuer UUID from App Store Connect → Users and Access → Integrations |

**Developer ID Application**, not "Apple Development" or "Mac App Distribution" — those
cannot sign software distributed outside the App Store, and the difference is not visible
until notarisation refuses. Create it in the developer portal or Xcode → Settings →
Accounts → Manage Certificates, then export it *with its private key* from Keychain Access.

An **API key** rather than an app-specific password because it can be revoked on its own
and does not stop working when the Apple ID password changes.

The workflow imports the certificate into a keychain of its own, unlocked for that job
only, and calls `security set-key-partition-list` — without which `codesign` waits on a GUI
prompt nobody is there to answer and the job hangs until it times out.

All three steps are needed for a download that simply opens, and each covers a different
refusal:

| step | what it gets past |
|---|---|
| sign | "cannot be opened because the developer cannot be verified" |
| notarise | the quarantine warning macOS attaches to anything downloaded |
| staple | the same warning, for somebody whose first launch is offline |

They happen in that order, and stapling happens before packaging — staple afterwards and
the archive people download contains an app without its ticket.

#### The macOS bundle must stay non-single-file

`build-macos-app.sh` publishes a directory rather than a single file, and while the reason
written there is startup — a single-file build self-extracts before the window can appear —
it is also what makes notarisation possible at all. A single-file .NET app unpacks its
native libraries to `~/.net/<app>` on first run, so the binaries that actually execute do
not exist at signing time and cannot be notarised. Apple has nothing to inspect and the
extracted copies carry no signature.

The Windows and Linux artifacts are single-file, which is fine: neither platform checks.

#### Why `--deep`, which Apple discourages

.NET's apphost requires `cairn.runtimeconfig.json` and `cairn.deps.json` to sit beside the
executable, and `codesign` treats every non-code file in `Contents/MacOS` as nested code
that must carry its own signature. A `.json` cannot. Signing each nested binary and then
the bundle — the arrangement Apple actually recommends — fails at the last step, every
time, on a clean tree:

```
code object is not signed at all
In subcomponent: .../Contents/MacOS/cairn.runtimeconfig.json
```

Moving the payload out of `MacOS/` would mean replacing the apphost. The cost of `--deep`
is that the entitlements below reach nested code as well as the app; they are narrow, and
the notary service is the real arbiter of whether Apple minds.

What `--deep` does get right, checked rather than assumed: the hardened runtime reaches
every nested binary too, which is what notarisation requires.

```
libcoreclr.dylib     flags=0x10002(adhoc,runtime)
libSkiaSharp.dylib   flags=0x10002(adhoc,runtime)
cairn-cli            flags=0x10002(adhoc,runtime)
createdump           flags=0x10002(adhoc,runtime)
```

No Mach-O in the bundle is left unsigned, and `get-task-allow` — the debug entitlement that
guarantees rejection — is absent. `spctl -a` rejects an ad-hoc build, which is the expected
answer and the thing a real certificate changes.

#### Entitlements

`macos-entitlements.plist`, and each line is a hole in the hardened runtime, so each has a
reason written next to it. `allow-jit` and `allow-unsigned-executable-memory` are what
CoreCLR needs to compile IL at runtime — without them the app dies on launch rather than
degrading. `disable-library-validation` is the one worth trying to remove once notarisation
is working.

The hardened runtime is applied to ad-hoc builds too, so a local build fails the way a
released one would rather than saving the surprise. Verified by launching one: it starts.

### macOS application bundle

```bash
./build-macos-app.sh                       # artifacts/osx-arm64/Cairn.app
ICON=path/to/icon.png ./build-macos-app.sh # with an icon
SIGN_IDENTITY="Developer ID Application: …" ./build-macos-app.sh
```

Produces a real bundle — `Contents/MacOS`, `Contents/Info.plist`, `Contents/_CodeSignature`
— so it gets a Dock tile, proper foreground activation and its own name in the menu bar.
The launcher binary is `Contents/MacOS/cairn`, and it is the only program in there. The
CLI used to ship beside it; it is a development tool with no documentation aimed at
anybody downloading a launcher, so releases carry the launcher alone and `cairn-cli` is
run from the source tree.

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
