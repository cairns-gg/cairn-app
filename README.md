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

The launcher launches the game, so today a pack is what a *player* runs. Nothing in the
engine assumes that: `cairn-cli` drives the same `Cairn.Core` — resolve, verify, sync —
with no window and no game, which is what a server-side or scripted use would be built on.
It is not shipped in releases yet, so that is a door left open rather than a feature.

Cairn does not replace the game's ModDB integration; it fills the gap next to it.

The source is here to be read, not forked: Cairn is **source-available**, under the
[PolyForm Strict License 1.0.0](LICENSE.md) — noncommercial use, no redistribution, and one
[additional permission](#licence) for proposing changes back. If what you want to check is
that a download matches this source, that is
[a different question with a real answer](#verifying-a-download-against-this-source).

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
away from plain Vintage Story too. They remain reachable by launching the game normally.

There is one moment when that first objection does not hold: importing an install. The worlds
in that folder were played with the mods being imported, so the import dialog lists them with
their sizes, and a pack's Settings tab offers the same at any time afterwards — which is the
only route for a pack that already exists. **Copied, never moved.** Cairn does not write to
your data path, which is what makes "your plain Vintage Story goes on working" a fact rather
than an intention, and a world moved out of it would open nowhere but the pack that took it.
Nothing is ticked by default: the mods are the pack and arrive with it, while a world is
gigabytes and the pack works without one. A world the pack already has is refused rather than
overwritten — that is somebody's months of evenings, not a file to clobber on a checkbox.

`--addModPath` is still *additive* — the game always also searches `<install>/Mods` and
`<dataPath>/Mods` — but with a per-pack data path that second directory is the pack's own, so
nothing leaks between packs.

### The mods folder a pack used to inherit

That was not the whole story, and the gap produced the most-reported bug there has been:
install a mod in plain Vintage Story, add the same mod to a pack, and the game loaded **two
copies of it**.

The game does not work out where to look for mods purely from `--dataPath`. It keeps the
list in `clientsettings.json`, as absolute paths, written the first time it ran:

```json
"stringListSettings": {
  "modPaths": ["Mods", "/Users/you/Library/Application Support/VintagestoryData/Mods"]
}
```

A new pack's settings are seeded by copying the player's own, so their keybinds and graphics
carry over — and that copy brought the second path with it. `--addModPath` adds to that list
rather than replacing it, so every pack searched the player's personal Mods folder as well
as its own. The game's own log is unambiguous:

```
Will search the following paths for mods:
    ~/.cairn/games/1.22.6.app/Mods
    ~/Library/Application Support/VintagestoryData/Mods     <- not this pack's
    ~/.cairn/packs/anego/Mods
```

`ClientModPaths` rewrites the setting to name only the game's own Mods directory and this
pack's. It runs when a pack's settings are seeded and again on **every launch**, because
every pack made before this existed still carries the copied value and a launch is the only
thing that reaches into one. What it drops is reported — "no longer loading mods from …" —
since the first launch after the fix has fewer mods in it than the last one did, and that is
not a thing to discover in-game.

The setting is written even when the file or the key is absent, rather than left to the
game's default. The default is not the pack's: the log above is from a pack that had never
been played, launched with `--dataPath` pointing at an empty directory.

`<install>/Mods` stays in the list. It holds VSSurvivalMod, VSEssentials and VSCreativeMod —
the game itself — and it is not where mods are added by hand; the game ships a
`do_not_add_mods_here.txt` in it saying so.

Two smaller things fell out of the same investigation. `PackStore.Create` makes the data
directory, which is how a pack records that it has its own data path — and `EnsureDataPath`
was keyed off that same directory existing, so packs created through the launcher were never
seeded at all. And a seeded copy carries the player's login: since the newest session on the
machine wins by file timestamp, and a file copied a moment ago is the newest by
construction, making a pack would have signed every other pack back in as whoever the shared
data path last was. The seed is stripped of session keys, which arrive a moment later from
Cairn's own record.

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
cairn-cli import-install <name>         make a pack from the mods you already have
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

In the launcher: **Import…** in the sidebar, and **Export…** in a pack's Settings tab.

### Importing the mods you already have

Nearly everybody arriving at Cairn has already played Vintage Story. They have a Mods folder
with thirty mods in it, and what the launcher used to offer them was an empty pack and a
search box — which is a poor answer, and became a worse one once packs stopped inheriting
that folder (above). The two changes only make sense together.

```
cairn-cli import-install "My mods" --dry-run    # what it would take, and what it would not
cairn-cli import-install "My mods"              # create the pack
cairn-cli import-install "My mods" --from /path/to/other/Mods --game 1.22.6
```

Every zip is read for its own `modinfo.json` — the same reader sync uses — and then looked up
on ModDB. The second half is worth justifying, because the mods are right there on disk:

- **Your versions, without pinning them.** The manifest names the mod and nothing else, so
  what stops the next sync taking the newest release is the lockfile — and a lock entry needs
  a URL, a release id and a file id. The zip carries none of those. Without the lookup the
  import could honour *your versions* or *unpinned*, not both.
- **Which mods cannot go in a pack, before the pack exists.** A pack is a list anyone can
  fetch. A mod that has been taken down since it was installed is indistinguishable, on disk,
  from one that has not — and finding out on the first Play is finding out too late.

The folder is listed as soon as it has been read, which is instant; each row says
`checking…` until its own lookup lands. Holding the list back for the lookups made finding
somebody's own mods look like the slow part of the job.

What comes back is one line per mod, including the ones that will not make it:

```
+ A Culinary Artillery Experimental 2.0.0-dev.21: ready — 2.0.0-dev.21
+ Self-Recording Thermometer 0.5.0: ready — 0.5.0
- Alloy Calculator Stuzzichino 1.2.19: unknown — ModDB has no mod with id 'alloycalculatorstuzzichino'
4 of 5 mods can go in a pack for game 1.22.6
```

A mod ModDB will not serve is skipped and named. Copying its zip into the pack is the other
answer, and a worse one: a pack whose mods come from a folder on one machine cannot be
shared, published or reproduced by anyone, which is most of what a pack is for.

**The versions you are running are imported, and nothing is pinned.** A pin means "stay
here", and nobody choosing this has said that — they have said "start me where I am". So the
manifest names the mods and the exact releases go into the lockfile, which is what sync
installs from; the update button works exactly as it does for any other pack. Pinning
instead would reproduce the folder too, and then freeze it forever.

The lock entries are written with no checksum, because nothing has been downloaded yet.
That is a state the syncer already handles — it verifies against a locked hash when there is
one and records the hash it computed when there is not — so the first sync fetches precisely
those releases and fills the rest in. Taking the hash from the player's own copy would be
the wrong answer: it would describe bytes ModDB may not serve, which is exactly the mismatch
the field exists to catch.

In the launcher this is one step and asks one question — what to call the pack. Choosing the
source reads the folder immediately, because reading it is what choosing it meant; switching
to another source cancels that, so somebody who came to paste a link does not wait on forty
ModDB lookups on the way past.

The game version is not among the questions. A pack made from the mods you are running is a
pack for the game you are running them on, so it is taken from the install and stated rather
than offered. There was a dropdown here briefly, defaulted from the newest version Cairn knew
about and sitting next to the button as "Scan for game 1.22.6" — which read as a filter on
the scan, and asked something with one sensible answer. Moving a pack to another game version
is a different job, and Settings already does it properly, with a preview of what it would do
to every mod. The CLI keeps `--game` because it is a scriptable tool and that is what flags
are for.

Two judgements are worth spelling out. A mod switched off in Vintage Story is left off — it
is not part of what is being played, and importing it would quietly turn it back on. And a
release marked for no version like the pack's is imported as **accepted**, since running it
is the same testimony `--accept-unmarked` records — but only when the folder was being
played on a game version like the pack's. Someone importing a 1.21.4 install into a 1.22.6
pack has said nothing whatever about 1.22.6, so those mods move to the newest release the
new game actually has.

The same dialog offers the **worlds** in that install, and a pack's Settings tab offers them
at any time afterwards — the only route for a pack that already exists. A world made under a
mod set generally cannot be opened without it, so importing the mods and leaving the worlds
behind is half a job. They are copied rather than moved, and nothing is ticked by default;
see "each pack has its own worlds" above for why both of those are deliberate.

Cairn only ever *reads* the folder. Plain Vintage Story goes on working exactly as it did.

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

It applies only to mods a manifest names. A dependency discovered inside a zip is nobody's
testimony, so it fails as it always did.

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

```
cairn-cli games                     installed and available versions
cairn-cli games install 1.22.5      download, verify md5, unpack
cairn-cli games remove 1.22.5
```

Game versions are managed with `cairn-cli games` (above)
or **Preferences → Storage** in the launcher, which is where installed versions are removed
and private runtimes managed. **Clean up** there sweeps every version no pack targets, plus
any private runtime left with nothing to run and the icon and mod-detail caches — one sweep
for everything that comes back on its own.

A client built from source is deliberately **not** in that sweep, and neither is its build
tree. Everything else there is a download that Play would fetch again; a built client is
twenty minutes of compiling, so on the same rule it would vanish the moment the last pack
using it was retargeted, from a button offering to tidy up. The build tree is listed with
its size and removed by its own **Remove**, which says what it costs to undo. It lists each item with its size and what it
frees, and asks first. Nothing it removes is irreplaceable, which is what makes it safe to
offer: Play downloads whatever a pack needs. A pack whose manifest will not load blocks the
sweep rather than being treated as needing nothing. Nothing nags about a missing version: pressing **Play** fetches
whatever the pack needs, so that screen exists mainly to give the disk space back — it also
reports what Cairn is using and can empty its caches.

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

### The game as a Flatpak

On Linux the game is commonly installed from Flathub, and on an immutable distribution —
Bazzite, Silverblue, SteamOS — that is often the only way it can be. Cairn finds such an
install and uses the .NET that comes inside it:

```
$ cairn-cli diagnostics
Game installs
  system   1.22.6     X64  /var/lib/flatpak/app/at.vintagestory.VintageStory/current/active/files/extra/vintagestory

$ cairn-cli launch mypack --dry-run
using runtime .NET 10.0.8 (x64) at .../current/active/files/lib/dotnet
```

The runtime is the part that matters. Such a machine can have no system .NET at all, so
without reading the one inside the deploy Cairn concludes the game cannot start — and then
downloads a private runtime to sit beside the perfectly good one it did not look at.

Three things about this are less obvious than they look:

**It is an ordinary install in an unusual place.** `/app` in the sandbox is `files` in the
deploy on the host, and the Flatpak unpacks the shipped tarball as extra data rather than
building it — so the game is at `files/extra/vintagestory` and its .NET at `files/lib/dotnet`,
and everything in between is exactly what the tarball contains. Nothing special is done to
read it; the directory is simply added to the list of places `GameInstall.TryAt` is pointed at.

**Nothing goes through `flatpak run`.** The sandbox grants the app almost no filesystem
access — on a stock install, `xdg-pictures/Vintagestory` and some GTK config — so a pack
directory in `~/.cairn` is invisible to the game inside it and `--addModPath` would name
nothing. `flatpak run --filesystem=<dir>` can grant it, but there is no need: the apphost
and every native library the game bundles resolve against the host, so Cairn launches the
binary directly with `DOTNET_ROOT` pointed into the deploy, exactly as it does for a
tarball install. The sandbox is stepped around rather than negotiated with. The cost of
this choice is a host that lacks the game's shared libraries, where the Flatpak would have
worked and a direct launch will not.

**Detection reads paths, never `flatpak`.** The obvious approach — `flatpak info
--show-location` — resolves user and system installs alike, but answers with the
content-hashed deploy directory, whose name changes on every `flatpak update`. Cairn walks
`<installation>/app/<id>/current/active` instead: symlinks Flatpak repoints as it updates,
so a path already recorded keeps working. Installations are the two standard roots plus
anything declared in `/etc/flatpak/installations.d`, which is how a Steam Deck puts
Flatpaks on the SD card.

`VINTAGE_STORY` still overrides all of it, and pointing it at a deploy's
`files/extra/vintagestory` picks the bundled runtime up too — the runtime is found from the
layout, not from having discovered the directory ourselves.

### Optimised clients, built on the machine

[Optimum](https://mods.vintagestory.at/optimum) is not a mod. It is a fork of the client,
distributed as ~95 patches that have to be applied to a *decompiled* copy of the game and
recompiled — a procedure well beyond what most players will do, and the reason it is far
less used than its performance would justify. Cairn can do it for them:

```
cairn-cli optimum                   what it would cost, without doing any of it
cairn-cli optimum build [--yes]     clone, decompile, patch, compile, install
cairn-cli optimum clean             delete the build tree, keeping the client
```

or **Build Optimum…** in a pack's Settings tab, which shows the same warning and then a
window with the live build log.

The warning matters more than it looks. Everything else Cairn installs is a download
measured in minutes; this is a **15–30 minute compile needing 4–6 GB**, so starting it
without saying so would be a trick. It can be cancelled at any point, and cancelling
leaves packs and existing installs untouched.

Five things about this are deliberate:

- **The client is built for the machine, not for the stock download.** On Apple Silicon
  that means a native arm64 client, which is most of the point of building one — and it is
  decided by the machine's architecture rather than by Cairn's own, so an x64 Cairn under
  Rosetta does not quietly produce an emulated client. It also means the build can need a
  .NET the stock install does not; see [Requirements](#requirements).
- **Optimum's own scripts do the work.** Cairn drives `bootstrap`, `dotnet build` and the
  platform packager rather than reimplementing them. A second implementation of a
  95-patch bootstrap would only ever prove it agrees with itself, and its failure mode is
  a client that looks right and is not.
- **The build is pinned to a commit**, not a branch — Cairn builds the revision that was
  actually tested, so somebody else's push cannot turn into a Cairn feature that stopped
  working. The pin carries the game version with it, because Optimum targets exactly one
  Vintage Story version at a time.
- **Cairn cannot install the prerequisites**, so it names all of them at once with a
  reason and a command each. Windows needs only Git (`bootstrap.ps1` implements every
  fixup natively); Linux and macOS additionally need perl, python3, curl and tar. A .NET
  SDK is *not* a prerequisite — Cairn fetches a private one the same way it fetches a
  private runtime.
- **The result is a variant, and a variant never runs by accident.** See below.

### A modified client only runs because you said so

A fork reports the version it was forked from, so it is indistinguishable from the real
game by metadata alone. An Optimum build of 1.22.5 answers "is 1.22.5 installed?" exactly
as the stock game does — and would then be handed silently to every 1.22.5 pack on the
machine. That is ruled out by construction rather than by care:

- a build marks itself with a `.cairn-variant` file, and no automatic lookup ever returns
  one — only a choice recorded against a specific pack;
- the marker names **which executable to run**. Optimum ships a copy of the vanilla client
  plus its own launcher, byte-identical game binaries and all, and does its patching at
  startup from that launcher. An install without this runs the stock game while every
  message says otherwise — which is exactly what happened before the marker carried it;
- a recorded choice stops applying when the pack's game version moves away from it. The
  pack's mods were resolved against the version it *now* targets, so a client nothing in
  it was chosen for is not an override, it is a mismatch;
- the diagnostics report says which install a pack actually runs, and marks a variant
  loudly. "The game is behaving oddly" is unanswerable without it.

The build tree is kept under `~/.cairn/builds/optimum` so a rebuild is minutes rather than
another full decompile. It is a few gigabytes idle between pin bumps, hence
`optimum clean`.

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
```

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

## Build

```bash
dotnet build
dotnet test tests/Cairn.Core.Tests/Cairn.Core.Tests.csproj   # 428 tests, 432 with the game
dotnet tests/Cairn.App.Tests/bin/Debug/net10.0/Cairn.App.Tests.dll   # 201 UI tests
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

**Withdrawing is not deleting, and it is not permanent.** `cairn-cli unpublish` takes the
pack down; the row survives on the site and the URL answers 410 with a tombstone rather
than 404, because these links live in chat scrollback and committed `pack.json` files
indefinitely. Publishing again revives the pack at the same address — that is what
withdrawing means for an author, as against an administrator withdrawing one, which the
server refuses to let a republish undo and says so.

Coming back has to survive the unchanged-check above, which would otherwise refuse the one
publish that matters: the pack is down, and republishing it byte-for-byte is exactly how it
returns. So a withdrawal clears the local publish record and keeps the URL, and the pack
reads as **Withdrawn** rather than as one never shared — the launcher says the address is
still yours and offers **Publish again**. The slug is editable once more, which is also how
a pack gets renamed: unshare, then re-share under the new name.

**A withdrawal made on the site never reaches your machine**, and that is the case the
refusal got wrong for longer. Nothing pushes to a launcher, and share state is a local
projection on purpose — asking the server whether a pack has changed, on every pack, to
draw a button would be a great deal of network for a question that is almost always "no".
So the belief is checked at the one moment it is about to block somebody: publishing a pack
the machine thinks is unchanged first asks whether it is still being served. A 410 there
clears the record and the publish goes through. Anything else — including a server that
cannot be reached — leaves the refusal standing, because not knowing is not the same as
knowing it is gone, and inventing a withdrawal would throw away the record on a flaky
connection.

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

### Registering the scheme

| platform | how | state |
|---|---|---|
| macOS | `CFBundleURLTypes` in the bundle, written by `build-macos-app.sh` | **works** — verified cold and with the app already running |
| Windows | `HKCU\Software\Classes\cairn`, written on startup | **works** — verified by clicking a link |
| Linux | `~/.local/share/applications/cairn-url-handler.desktop`, written on startup | **works** — verified by clicking a link |

macOS gets this free from the bundle format: LaunchServices reads the plist the first time
it sees the `.app`, so shipping a bundle *is* the registration. Windows and Linux have no
equivalent — registering there is an explicit act of installation, and Cairn ships as one
binary in an archive with no installer to perform one. So `PackLinkHandler` does it for the
app on startup, off the critical path, and never fails a launch over it.

On every start rather than once, because both mechanisms record an absolute path: somebody
who moves the binary would otherwise be left with a scheme pointing at where it used to be.
Nothing is written when the recorded value already matches, so the usual case costs a read.

**Windows still wants single-instance handling**, which this does not add. With no installer
it launches a *new* copy per click, and two launchers sharing one `~/.cairn` can race.
Registering the scheme is what makes the link arrive at all; making a second click reach the
window already open is a separate job.

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
| Linux (server) | `cairn-<v>-linux-x64-server.tar.gz` | `ubuntu-latest`, cross-published |

The server is a separate artifact rather than a second file in the Linux tarball: somebody
putting a server in a container wants that binary and not a desktop launcher, and the
reverse is just as true. It is the only artifact `cairn-server` ships in — see
[running a server](#running-a-server-cairn-server) for why Linux alone.

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

### Verifying a download against this source

The source is published so it can be read. That is worth very little on its own: reading it
tells you what Cairn *would* do, and the thing on your disk is a binary somebody else built.
Three separate mechanisms close that gap, and they are worth keeping distinct, because each
answers a question the other two cannot.

**1. The manifest says what the bytes should be, and is signed.** `manifest.json` carries a
SHA-256 for every artifact, and `manifest.json.minisig` is a detached signature over it made
in the `manifest` job — which holds the signing key and no credential that can write to
object storage. The public half is [`cairn.pub`](cairn.pub), committed here.

```bash
minisign -Vm manifest.json -p cairn.pub
sha256sum -c SHA256SUMS
```

That proves the download is intact and is what the key holder meant to ship. It proves
nothing about where it came from, and a reader who has no reason to trust the key holder
gains nothing from it at all.

**2. The build attestation says which commit it was built from, and GitHub signs that.**
Every release artifact is attested with `actions/attest`: GitHub mints a Sigstore
certificate against the workflow's own OIDC identity and signs a SLSA statement binding the
artifact's digest to this repository, this workflow file, this commit and this run.

```bash
gh attestation verify cairn-1.2.3-linux-x64.tar.gz --repo dizzyd/cairn-app
```

This is the one that answers the inspector's question, and it is the only one here that
does not rest on trusting whoever cut the release. Nobody can forge it by hand — not
whoever holds the R2 credentials, not whoever holds the minisign key, not the account
owner. The only thing that produces one is that workflow actually running on that commit.
The bundle is published beside the downloads as `cairn-<version>.intoto.jsonl` and named by
the manifest, so it also verifies offline, for somebody who took the file from
`download.cairns.gg` and never touched GitHub:

```bash
gh attestation verify cairn-1.2.3-linux-x64.tar.gz \
  --repo dizzyd/cairn-app --bundle cairn-1.2.3.intoto.jsonl
```

Public repositories only, on every plan; a private one needs Enterprise Cloud. The step is
skipped while this repository is private, and the manifest then carries no `attestation`
field rather than naming a file nobody made.

**3. The checksums exist in two places reached by different credentials.** `SHA256SUMS`
goes to R2 with the artifacts and is also written into the GitHub release, which the R2
token cannot touch. That is detection rather than prevention: whoever holds the R2 keys can
still replace a download, but not without the two copies disagreeing.

Three smaller things make the chain legible end to end:

- **The commit is inside the binary.** CI sets `SourceRevisionId` from `GITHUB_SHA`, so the
  informational version is `1.2.3+<sha>`, and the diagnostics report — *Copy diagnostics* in
  the launcher, `cairn-cli diagnostics` on the command line — prints it beside the version.
  The manifest and the attestation name that same commit, so a bug report identifies the
  source that produced it, and the three either agree or visibly do not.
- **The dependency graph is pinned.** Every project has a `packages.lock.json` and CI
  restores in locked mode, so "built from this commit" also fixes the 33 resolved packages,
  including the native payloads that end up inside the signed artifact and that no `.csproj`
  names. Without that, the same commit could build from different code.
- **The build log is public.** Once this repository is, the run named in the manifest is
  readable by anyone, including everything the workflow did to produce the artifacts.

**Tag releases with a signed tag** — `git tag -s` — so that tag → commit is attributable the
same way the attestation makes commit → artifact attributable. It is the one link in the
chain that is a habit rather than something the workflow enforces.

What none of this offers is a **reproducible build**. You cannot rebuild a commit and get a
byte-identical artifact: the single-file bundle is compressed, and the macOS bundle carries
a signature with a timestamp in it and a stapled notarisation ticket that only Apple can
issue. `ContinuousIntegrationBuild` is set under CI so the *managed assemblies* are
deterministic, which is enough to rebuild a commit and diff the DLLs inside the bundle —
useful, and short of a guarantee. The attestation is what stands in for one, and the
difference is worth being plain about: it says GitHub watched this workflow build these
bytes from this commit, not that anyone else can produce them again.

And the obvious limit, since the whole section is about trust: none of it says the source is
*good*. It says the binary is that source. Reading it is still your job.

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

Requires .NET 10. The game is a framework-dependent apphost, so it needs a .NET matching
*its* architecture: on Apple Silicon that is arm64 for a 1.22-or-later client and x64 —
installed via Microsoft's `.pkg`, which writes `/etc/dotnet/install_location_x64` — for an
older one. Cairn itself is architecture-agnostic: it only spawns the game, and reads
`VintagestoryAPI.dll` metadata without loading it.

## Licence

Copyright 2026 Dave Smith, under the **PolyForm Strict License 1.0.0**. The full text is in
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

- **A commercial server host cannot run `cairn-server`.** Noncommercial means noncommercial,
  and that boundary catches some people who would be good users. It is the price of the same
  clause that stops the resale case, and it was chosen knowing that.
- **Contributions need their own permission**, which they have. The licence grants no right
  to make changes, so fork-branch-pull-request would otherwise be an infringement before
  anybody had read the diff. The [additional permission](LICENSE.md#additional-permission-for-contributions)
  at the end of `LICENSE.md` allows a fork kept for the purpose of proposing a change, and
  licenses what you propose to the licensor so it can actually ship in a release.

PolyForm Strict is not on the SPDX licence list — only the Noncommercial and Small Business
variants are — so GitHub shows no licence badge for this repository and no tooling will
recognise an identifier for it. That is why the terms are stated here as well as in the file.

The dependencies are all permissive and nothing here conflicts with them: Avalonia,
CommunityToolkit.Mvvm and BouncyCastle are MIT or MIT-style. Vintage Story itself is neither
bundled nor redistributed — Cairn downloads it from the publisher, under the publisher's own
terms, and that is unaffected by any of this.
