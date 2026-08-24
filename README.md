# Cairn

A modpack manager for Vintage Story.

Vintage Story already handles one half of this well: join a server that has mods you lack
and it offers to download them from ModDB, dropping them in `<dataPath>/ModsByServer`.
What it has no concept of is a *pack* — a mod set you put together deliberately, pinned to
exact versions, reproducible on somebody else's machine and shareable as one thing. That
is what Cairn manages.

A pack is whatever you put in it. Cairn installs the mods a pack names and hands the lot to
the game, which in singleplayer is running the server itself and loads all of them — a mod
ModDB marks server-side is installed too, with a warning that it may do nothing on a client
that is only joining somewhere else.

Cairn does not replace the game's ModDB integration; it fills the gap next to it. It also
installs the game itself, and a private .NET runtime when the machine has none, so a pack is
something you can hand to somebody who has bought Vintage Story and nothing else.

The source is here to be read, not forked: Cairn is **source-available** under the
[PolyForm Strict License 1.0.0](LICENSE.md) — noncommercial use, no redistribution, and two
[added permissions](#licence). Checking that a download was built from what you are reading
is [a separate question with a real answer](docs/verifying.md).

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

**Check for updates** offers each one per row, with an **Update all**. A mod pinned to an
exact version is never offered one, because a pin is an instruction to stay put.

`launch` syncs and then starts the game with the pack stacked on:

```
Vintagestory --dataPath ~/.cairn/packs/anego/data \
             --addModPath ~/.cairn/packs/anego/Mods \
             --connect anego.example.com:42420
```

### A running game belongs to the pack, not to the pane

The launcher's pack pane is rebuilt every time you select a different pack, so a launch
tracked on it was forgotten the moment you clicked away: the pane came back saying nothing
was running, **Play** was enabled again, and pressing it would have started a second copy
of the game on the same save. Which pack has a game up is held for the whole session
(`RunningGames`), alongside each pack's log, which is kept there for the same reason.

So the sidebar marks the pack that is playing, and coming back to it finds the launch
where you left it. A pack's game closing while you are looking at a different pack still
writes its session back, still logs against the pack that ran, and still raises the crash
report — it waits on that pack's pane rather than being lost.

While a game is up, **Play** gives its slot to **Force quit** rather than sitting there
greyed out beside it, and the progress bar goes — an indeterminate bar that never fills
for the hours somebody is playing reads as a launcher that is stuck. Force quit is behind
a confirmation, because it is a kill and not a quit: everything since the last save goes,
and the game gets no chance to write one. It is there because a game that has stopped
drawing is still a process holding the pack's save open, and the alternative is Activity
Monitor. The exit is recorded as
asked-for, so it reads as a quit rather than as the crash its non-zero exit code would
otherwise make of it.

### Each pack keeps to itself

A pack gets its own data path at `packs/<id>/data`, so `Saves/`, `ModConfig/` and
`Playerdata/` belong to that pack and not to every pack at once — opening a world against
the wrong mod set is a leading way to ruin it. Because the worlds are inside the pack,
**deleting a pack deletes its worlds**, and the confirmation itemises exactly what goes.

Your login still follows you. Seven session keys are kept in `~/.cairn/session.json` and
merged into each pack at launch, so signing in anywhere signs you in everywhere while
keybinds and graphics settings stay per-pack. Cairn only ever *reads* your own Vintage Story
data path; your ordinary saves stay where they are and plain Vintage Story goes on working.

A pack also does not inherit the Mods folder of the install it came from — otherwise a pack
quietly contains mods it never listed, and the pack you tested is not the pack somebody else
gets.

[docs/pack-isolation.md](docs/pack-isolation.md) has the reasoning for both, and the
most-reported bug that came of getting the second one wrong.

### Mod settings a pack carries

Some mods only work together once one of their config files has been edited — Terrain Slabs
wants Footprints named in a list before the two behave. A pack can carry those values, so
the author works it out once instead of everybody who installs the pack working it out
again:

```json
"modConfig": {
  "terrainslabs.json": { "compatibleMods": ["footprints"] },
  "XLeveling/mining.json": { "enabled": true }
}
```

Paths are relative to the pack's `ModConfig/`, with `/` on every platform. Only the values
named travel — the rest of the file stays the mod's, so a pack does not go stale when the mod
adds a setting, and what a pack intends to change can be read before importing it.

**The first time a pack asks for a value it gets it; after that, anything you have changed
is yours** — later versions of the pack will not take it back. Every launch says which
values it set and which it left alone.

Mods using **ConfigLib** are covered both ways: where it edits the mod's own JSON, and where
it keeps the settings in its own flat `.yaml`. That second kind arrives on the second launch,
because ConfigLib writes the file itself the first time the mod runs and the `version` line
it puts at the top is not something to guess at. `.ini` files, files whose top level is a
list, and files containing `//` comments are refused out loud rather than half-applied; of
114 config files in a real 74-mod pack, 110 can be carried.

You do not write that by hand. The **Mod config** tab lists what you have changed from what
each mod first wrote, old value beside new, and a tick carries it — Cairn learns a mod's
defaults by remembering what it wrote the first time it ran, so the list is empty until a
pack has been played once, and says so. `cairn-server` applies these too, which matters
because half of them are server-side rules.

[docs/world-config.md](docs/world-config.md) has why the rule is not the one the hotkeys
use, how ConfigLib divides into the part this reaches and the part it does not, and what the
other two settings layers would take.

### Where all this lives, and moving it

Everything — packs, game versions, private runtimes, caches, build trees — is under one
root, which is `~/.cairn` unless something says otherwise. Three things can say otherwise,
in this order:

```
CAIRN_HOME                      an environment variable, and it always wins
~/.cairn/home                   a file holding one absolute path
~/.cairn                        the default
```

`CAIRN_HOME` has always worked and still does; `cairn-server` units are configured with it.
What it cannot do is stick, which is why the file exists: an environment variable set in a
shell does not reach a Start-menu launch, a `.desktop` entry, an `.app` bundle or a
`cairn://` activation, and those are how the launcher is actually started. The file is read
at the default location and cannot live in `settings.json`, which is inside the root it
would be configuring.

**With `CAIRN_HOME` set, Move… is disabled and says why.** Writing a pointer the variable
then outranks would look like it had worked and change nothing, so both front-ends refuse
rather than pretend. Unset it to move from the launcher, or carry on as before and point it
wherever you like — nothing about that path has changed. `home`, `home clear` and
`home discard` work either way.

**Preferences → Move…** copies everything to a directory you pick and then uses it. It is a
copy, not a rename — the point is to cross onto another disk, where a rename fails. Cairn is
repointed only once every file has arrived and been checked, so a failure at any stage
leaves the old root live and untouched. Links are recreated as links rather than followed,
and each pack's pinned install path is moved with it.

**One confirmation covers the whole of it**, deletion included — copy, check every file,
repoint, remove the original. Somebody doing this is out of disk space, so a version that
stopped after the copy would answer that with two of everything and a chore.

The order is what makes it safe: nothing is removed until every file has been confirmed
present at its full length and Cairn is already reading the new location. If the removal
fails — a permission, a file held open — the move still stands and says so, and
`cairn-cli home discard <dir>` retries it.

What survives is the pointer file, when the old root was the default: it lives *inside* the
directory being removed and is what now points Cairn at the new location. Both front-ends
keep it, which is also why `rm -rf ~/.cairn` is not the way to tidy up after a move that
somehow left something behind.

If the root is a pointer at somewhere that is not there — an unplugged disk, a share that is
down — Cairn refuses to start rather than falling back. An empty launcher does not read as
"that disk is not connected", it reads as "everything is gone", and the next thing it offers
is downloading the game again beside data that is perfectly fine.

## Usage

The launcher is the primary interface — everything can be done from it, no terminal
required:

- **Pack list** with New pack / Refresh; the new-pack form takes id, display name, game
  version and an optional server.
- **Import…** asks where the pack is coming from: the Vintage Story install already on this
  machine, a link, or pasted text. One dialog with the three named, rather than the box that
  took a URL or a pack and guessed which — and which had no way at all to offer the first.
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

  A downgrade warns about the pack's worlds as well, and mods ModDB could not be asked
  about are reported as *could not be checked* rather than as working — a preview is worth
  nothing if it guesses. What a downgrade actually risks is in
  [docs/game-installs.md](docs/game-installs.md); it is less than it sounds.
- **Log** tab — what Cairn did, plus the game's own log. **Game log** pulls the tail of
  `client-main.log` into the pane and **Open logs folder** opens the directory, because
  when the game closes on startup the answer is in its log and nobody should have to know
  where that lives. A non-zero exit pulls the errors and warnings in by itself, with
  repeated lines collapsed — a failing render call logs the same line dozens of times a
  second and would otherwise bury the cause.
- **Preferences** is two tabs. **Overview** opens first: the version, where Cairn keeps its
  own files, the interface scale and the language. **Storage** is the disk-usage screen. On
  macOS the application menu's **About Cairn** opens Overview — it used to land on the
  disk-usage screen, which made it look like the wrong menu entry.

  **Language** defaults to working it out: `CAIRN_LANG`, then what was chosen here, then
  the language Vintage Story itself is set to, then the system's, then English. Following
  the game is the useful step — somebody running an English Windows in German has already
  said which they would rather read. Changing it applies immediately; nothing restarts.

  Strings live in `assets/cairn/lang/<code>.json`, flat key against text, which is the
  format every Vintage Story mod already ships — so a translator has written one of these
  before. `CAIRN_LANG_DIR` reads loose files in preference to the built-in ones and offers
  them in the picker, so a translation can be written and seen without building anything.
  English is complete; Spanish, French and the two Portuguese variants are drafts pending
  review, and anything untranslated falls back to English rather than to a blank.

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
- **A newer Cairn** is offered once, in a dialog, with a **Download for macOS (Apple
  silicon)** — or whichever platform this build is — that opens the browser. Nothing is
  installed for anybody; replacing the app leaves packs, worlds and settings alone, and the
  dialog says so, because an unexpected update prompt makes people wonder what it is about
  to do to their saves.

  The manifest it reads is `releases/latest.json`, which the release workflow already
  published and nothing consumed — the same file cairns.gg serves its download page from,
  and one that is only promoted when the macOS builds were notarised. So the app cannot
  offer a build the site would not.

  It keeps checking rather than asking once at startup, because a launcher is left open —
  it is the thing you press Play from — and a startup-only check misses every release that
  happens while it is running. An hourly tick reads the clock; the day-long interval below
  decides whether that turns into a request.

  The whole of the remembered state is one Unix timestamp in `~/.cairn/last-update-check`
  — its own file because `settings.json` is written whole and would drop anything it did
  not know about. Nothing records which release has already been mentioned, so an update
  somebody declines is raised again the next day, and every day until they take it. That is
  the trade for having nothing to keep in step: a remembered version string that goes stale
  or unparseable is a popup that either never appears again or appears forever. A build
  nobody stamped says `dev` and is never told it is out of date.

```bash
dotnet run --project src/Cairn.App
```

Everything above is also scriptable: `cairn-cli` drives the same engine with no window.
It is a development tool and is not shipped in releases — see [docs/cli.md](docs/cli.md).

### Sharing a pack

`pack.json` is already the shareable part — declared intent, meant to be committed or
handed around. Export bundles it with the lockfile into one file:

**Export…** in a pack's Settings tab writes one, and **Import…** in the sidebar takes a
file, a URL or pasted text. Exporting without the lock shares intent only; importing loose
tracks the newest compatible release instead of pinning what the author had.

### Importing the mods you already have

Most people arrive with a Mods folder already full. **Import… → the Vintage Story install on
this machine** turns it into a pack: every zip is read for its own `modinfo.json`, looked up
on ModDB, and reported one line per mod — including the ones that will not make it.

```
+ A Culinary Artillery Experimental 2.0.0-dev.21: ready — 2.0.0-dev.21
+ Self-Recording Thermometer 0.5.0: ready — 0.5.0
- Alloy Calculator Stuzzichino 1.2.19: unknown — ModDB has no mod with id 'alloycalculatorstuzzichino'
4 of 5 mods can go in a pack for game 1.22.6
```

**The versions you are running are imported, and nothing is pinned** — you have said "start
me where I am", not "stay here", so the exact releases go into the lockfile and the update
button works as it does for any other pack. A mod ModDB will not serve is named and left
out, because a pack whose mods came from one machine's folder cannot be shared or reproduced,
which is most of what a pack is for.

Worlds in that folder are offered too, listed with their sizes and ticked by nobody:
copied, never moved, and refused rather than overwritten if the pack already has one.

[docs/importing.md](docs/importing.md) has the reasoning — why ModDB is asked about mods
that are already on disk, and why the lock entries start with no checksum.

### A mod that has not caught up, added on purpose

Small mods stop being updated while the game moves on, and a lot of them still run. ModDB
says nothing about that, so a resolve refuses them and the sync reports *"no release marked
for game 1.22.6"* — true, and no use to somebody who has installed it by hand and played
with it for a week.

That person can say so, once, per mod:

```
cairn-cli add mypack oreveintracers --accept-unmarked
```

or **Add anyway…** in the launcher, which asks first and states what is unknown. What gets
written is not a "yes" but the version it was a yes *about*:

```json
{ "modid": "oreveintracers", "version": "1.2.0", "acceptedFor": "1.22.6" }
```

Four things follow from that shape:

- **It lives in the manifest**, so it travels with the pack. An acceptance in local state
  would make a pack that syncs only on the machine it was made on, which is not a pack you
  can share.
- **It stops applying when the pack moves.** Retarget from 1.22 to 1.23 and nobody has
  tested anything, so the mod fails again — and says why: *"no release marked for game
  1.23.0; it was accepted for game 1.22.6, and this pack has moved to a different release
  series since"*. Patch bumps within a series do not re-ask, because the game treats those
  as interchangeable and a question asked on every patch is a question nobody reads.
- **Sync says so every time**, not once when it was added: *"1.2.0 is marked for 1.20.12,
  not 1.22.6 — installed because the pack accepts it, and it may misbehave"*. The versions
  are named, because how far behind a mod is decides whether you believe it.
- **The lock records it too** — `"markedFor": ["1.20.12"]` beside the entry. Without that,
  the next sync installs from the lock without resolving anything and would report an
  untested mod as a clean, matched one.

**A dependency is accepted by the mod that requires it**, since it has no manifest entry to
hold an acceptance and no row to offer one — refusing it produced a pack nobody could argue
with, holding the mod that asked and not the mod it asked for. Floral Zones' 1.22 bridge is
marked for 1.22 and requires seven region mods last marked for 1.21, which is what a bridge
mod is. The line names who wanted it: *"1.0.19 is marked for 1.21.5, 1.21.6, not 1.22.5 —
installed because floralzones122bridge requires it, and it may misbehave"*. See
[docs/dependencies.md](docs/dependencies.md).

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
src/Cairn.Cli/      headless front-end (cairn-cli), a development tool
src/Cairn.App/      Avalonia GUI launcher (cairn)
src/Cairn.Server/   dedicated-server front-end (cairn-server), linux-x64
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

**Preferences → Storage** lists what is installed, removes versions, and manages the
private runtimes. **Clean up** there sweeps every version no pack targets, plus
any private runtime left with nothing to run and the icon and mod-detail caches — one sweep
for everything that comes back on its own.

It itemises what would go, with sizes, and asks first. Everything on that list is
replaceable — Play downloads whatever a pack needs — which is what makes it safe to offer at
all. A pack whose manifest will not load stops the sweep rather than being read as needing
nothing.

A client built from source is the exception, along with its build tree. On the same rule it
would vanish the moment the last pack using it was retargeted, and it is twenty minutes of
compiling rather than a download. The build tree has its own **Remove**, listed with its
size, so getting rid of it is a decision rather than a side effect.

Nothing nags about a missing version. Pressing **Play** fetches whatever the pack needs, so
that screen is really there to give disk space back.

Both list the machine's own install alongside Cairn's, because a pack launches from it
whenever the version matches — a list that omitted it would disagree with what actually
runs. Cairn will not remove one it did not install.

Versions land in `~/.cairn/games/<version>/` (`<version>.app` on macOS — see below), so
several can coexist and each pack launches the one its `gameVersion` names:

```
using game 1.21.5 at ~/.cairn/games/1.21.5
```

**On macOS the directory is named `<version>.app`**, and that suffix is load-bearing. The
shipped tarball has a flat layout — `Info.plist` at the top level, no `Contents/` — which is
the old-style form of a bundle, and the suffix is the whole of what makes macOS treat it as
one. The game's `Info.plist` sets `NSHighResolutionCapable` to `false`, and the window
server reads that only from a bundle. Without it the game is handed a Retina drawable it
asked not to have, sizes its viewport in points, and draws into the bottom-left **quarter**
of its own window — invisible in fullscreen, unmissable in windowed mode. A symlink does
not help: the window server resolves it and answers for the real path. Installs made before
this are renamed at startup, and a path a pack recorded before the rename is followed to the
bundle it became.

The cost is that `codesign` now reads these as bundles and objects — *"code has no resources
but signature indicates they must be present"* — the game's binary being ad-hoc signed with
no `_CodeSignature`. That is equally true of `/Applications/Vintagestory.app` after an
ordinary install, since it is the same layout and the same binary, and it only becomes a
refusal to launch for a **quarantined** copy. Nothing Cairn downloads is one:
`com.apple.quarantine` is applied by browsers and LaunchServices, not by HTTP clients.

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

### When it is not that simple

Three cases have their own answers, in [docs/game-installs.md](docs/game-installs.md):
a machine with **no .NET at all**, where Cairn fetches a private runtime and points only the
game at it; the game installed as a **Flatpak**, common on Bazzite and SteamOS, where the
runtime comes from inside the sandbox; and the **Optimum** community client, which Cairn can
build from source on the machine — a twenty-minute compile that nothing starts without an
explicit yes. Cairn builds a pinned revision of Optimum, so a pack can also be pointed at a
build you made yourself: **Use a client I built…**, which runs the folder you name and leaves
it entirely alone.

A modified client is only ever run because you said so: an install Cairn did not put there
is used when you point a pack at it, and never picked up on its own.

## Running a server: cairn-server

`cairn-server` is the headless end of Cairn — one binary to drop into a VM or an LXC, which
follows a published pack and keeps a server on it:

```
cairn-server install https://cairns.gg/you/your-pack   follow it, install the server and its .NET
cairn-server run [<id>]                                sync, then run in the foreground
cairn-server update [<id>]                             take the author's newer revision
cairn-server command [<id>] "/whitelist add dizzy"     talk to a running server
cairn-server unit [<id>] [--user] [--write]            systemd unit for it
cairn-server list                                      what is on this machine
cairn-server version                                   which build this is
```

`run` prints that version as its first line too, so the answer is in `journalctl` for a
server nobody watched start.

It is a **separate program from `cairn-cli`**, which is a development tool with two dozen
commands and is deliberately not shipped. Both are thin: what a sync installs, which install
can host, which .NET that needs — those are Core's, and are the same rules the launcher
applies. Released for **linux-x64 only**. The code runs anywhere, but a dedicated server is
published for Linux and Windows alone, `unit` writes systemd files, and the machines people
host on are Linux.

**It needs nothing on the box.** Verified on a stock Ubuntu 24.04 with no .NET at all:
`install` followed the pack, synced its mods, fetched the 51 MB dedicated server — not the
600 MB client — and a private .NET 10 beside it.

Four things are deliberate:

- **`run` installs what the lock says; `update` is a separate command.** A server follows a
  pack the way a player's copy does, but the consequence differs: a mod set that moves under
  a live world is a world that may not load, and nobody is sitting at the console when it
  happens. So a restart is not an update, and `update` says the running server keeps its
  mods until it is restarted.
- **Servers install under `~/.cairn/servers`**, not beside the client versions. A server and
  a client of the same version are different things wearing the same version number, and a
  machine can hold both — updating the client you play must not move the server a world is
  live on. In the other direction, a dedicated server download reports its version exactly
  as a client does, so `GameStore.Find` refuses one that has no client in it: a pack can
  never be handed a server to launch.
- **The console is a Unix socket**, owner-only, at `~/.cairn/run/<pack>.sock`. The server
  reads commands from stdin — which is why the shipped `server.sh` wraps it in `screen` —
  and a service started by systemd has no stdin worth the name, so `run` listens on the
  socket and writes what arrives to the server's stdin. Connect failing *is* the "not
  running" answer, so a command cannot block or vanish, and it doubles as the guard against
  two servers sharing one world directory. stdout is never redirected: journald gets the
  server's own output with nothing in the middle to lose a line.
- **Stopping is graceful.** `systemctl stop` sends SIGTERM, which `cairn-server` turns into
  the server's own `/stop` and waits — the unit sets `TimeoutStopSec=300`, because systemd's
  default is to give up after 90 seconds and `SIGKILL`, and a world being saved when that
  lands is a world rolled back. Measured on a real stop: 3.5 seconds, world saved.
  `Restart=on-failure` rather than `always`, so a server told to stop from inside the game
  stays stopped.

### Where a service runs, and the linger trap

`unit` writes a **template** — `cairn-server@.service` — so a box hosting three worlds has
three instances of one file rather than three files that drift. It never runs `systemctl`
itself: reloading a machine's systemd and enabling a service that starts at boot are the
administrator's decisions, so the commands are printed instead. A **system** unit runs as a
`cairn` user with `CAIRN_HOME=/var/lib/cairn`; Cairn does not create that account, because
one it made would outlive any uninstall it was part of, so the `useradd` line is printed
too. A **user** unit (`--user`) needs no root at all.

If you use a user unit, **enable linger**:

```
sudo loginctl enable-linger $USER
```

Without it, the systemd *user manager* is torn down when your last session ends and rebuilt
at the next login — so the server stops when you log out, starts again when you log in, and
in between shows up as a service that keeps reappearing with a new PID and re-loading its
mods. That reads exactly like a crash loop, and `NRestarts=0` is the tell: it is the manager
restarting, not the service. It is the first thing to check when a `--user` service will not
stay up.

## Building it yourself

```bash
dotnet build          # needs .NET 10
./dev.sh --run        # publish for this machine and launch it
```

Cairn.Core references nothing — not even the game's assemblies — so a clean checkout builds
without Vintage Story installed. [docs/building.md](docs/building.md) covers the rest:
cutting a release, signing and notarising the macOS builds, and where the downloads are
published.

## Requirements

**Cairn needs nothing installed.** Release builds are self-contained single files, so
there is no .NET prerequisite — download one binary and run it. A launcher that itself
required a runtime install could not help a user who has neither.

The one exception is [building an optimised client](#optimised-clients-built-on-the-machine),
which compiles the game from source and so needs Git — plus perl, python3, curl and tar
outside Windows. Nothing else in Cairn touches them, and it names whichever are missing
before it starts rather than failing partway.

**The game does need .NET.** Vintage Story is framework-dependent: its
`Vintagestory.runtimeconfig.json` asks for `Microsoft.NETCore.App` 10.0.0 and it bundles
no runtime. *Which* .NET depends on the client. `vs_client_linux-x64` and
`vs_install_win-x64` are x64 everywhere; macOS published `vs_client_osx-x64` alone until
1.22, which added a native `vs_client_osx-arm64`. On Apple Silicon Cairn installs the
native client and falls back to the x64 one only for versions that publish nothing else —
pre-1.22 releases would otherwise vanish from the list of versions it can install at all.

So an Apple Silicon machine can hold an arm64 install, an x64 one running under Rosetta, or
both, and each needs a .NET of its own architecture. **That makes "is there a runtime for
this" a question about an install, not about a version**, and Cairn asks it of the install a
pack will actually launch. Two installs of the same version routinely disagree: an Optimum
build made for the machine is arm64 while the stock download beside it may be x64, and a
Flatpak carries a runtime that serves the install that brought it and nothing else. Asking
about the version and then launching something else is how a pack refuses to start moments
after being told its version was ready.

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

Publishing an unchanged pack does nothing and says so, rather than minting a revision
nobody asked for — what counts as a change is the manifest and the lock, not the moment you
pressed the button. [docs/sharing.md](docs/sharing.md) covers the whole model: what a
revision is, what a withdrawn pack keeps, and why the button's label carries the state.

## Licence

Copyright 2026 Dave (Dizzy) Smith, under the **PolyForm Strict License 1.0.0**. The full text is in
[LICENSE.md](LICENSE.md), which is what governs; this section says why.

**Source-available, not open source.** The distinction is worth making rather than letting
someone discover it. Cairn installs software, writes into your game data and talks to the
network on your behalf, and none of that should have to be taken on trust — so the source is
published for anyone who runs it to read. That is the whole purpose. It is not published as
an invitation to fork it or to build a business on it, and the licence says so.

In short: **any noncommercial purpose is permitted**, and the grant excludes distributing
Cairn or making changes to it. So a fork is not licensed, and neither is redistributing the
binaries — which is also what keeps `download.cairns.gg` the only place Cairn comes from,
and therefore what makes "check it against the attestation" a complete instruction rather
than one that happens to apply to some copies.

Two consequences worth naming, because they are real:

- **A commercial server host may run `cairn-server`**, by an
  [additional permission](LICENSE.md#additional-permission-for-running-a-server) that says
  so. Strict permits noncommercial purposes only, which barred exactly the people
  `cairn-server` was written for — somebody who hosts game servers for other people cannot
  do it noncommercially — so the one component aimed at them was the one they could not use.
  The permission covers running it and not passing it on: a host may run it on their own
  machines, and may not ship it to customers in an image. The launcher is what these terms
  are protecting; the server is infrastructure that makes packs worth publishing.
- **Contributions need their own permission**, which they have. The licence grants no right
  to make changes, so fork-branch-pull-request would otherwise be an infringement before
  anybody had read the diff. The [additional permission](LICENSE.md#additional-permission-for-contributions)
  at the end of `LICENSE.md` allows a fork kept for the purpose of proposing a change, and
  licenses what you propose to the licensor so it can actually ship in a release.

PolyForm Strict is not on the SPDX licence list — only the Noncommercial and Small Business
variants are — so GitHub reports this repository's licence as "Other" (`NOASSERTION`) and no
tooling will recognise an identifier for it. A badge saying "Other" tells a reader nothing,
which is why the terms are stated here as well as in the file.

The dependencies are all permissive and nothing here conflicts with them: Avalonia,
CommunityToolkit.Mvvm and BouncyCastle are MIT or MIT-style. Vintage Story itself is neither
bundled nor redistributed — Cairn downloads it from the publisher, under the publisher's own
terms, and that is unaffected by any of this.
