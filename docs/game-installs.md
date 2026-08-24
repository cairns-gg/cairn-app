# Getting a game to launch

Cairn installs Vintage Story itself and, when the machine has no .NET, a private runtime for
it. That covers the ordinary case in a paragraph — see the README. This is the rest: what
happens when the game is a Flatpak, when somebody wants the optimised community client, and
what Cairn will and will not run without being told to.

## Private .NET runtimes

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

## The game as a Flatpak

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

## Optimised clients, built on the machine

[Optimum](https://mods.vintagestory.at/optimum) is not a mod. It is a fork of the client,
distributed as ~95 patches that have to be applied to a *decompiled* copy of the game and
recompiled — a procedure well beyond what most players will do, and the reason it is far
less used than its performance would justify. Cairn can do it for them:

```
cairn-cli optimum                   what it would cost, without doing any of it
cairn-cli optimum build [--yes]     clone, decompile, patch, compile, install
cairn-cli optimum clean             delete the build tree, keeping the client

cairn-cli optimum --game 1.22.5     the same, for a game version that is not the newest
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
- **Each build is pinned to a commit**, not a branch — Cairn builds the revision that was
  actually tested, so somebody else's push cannot turn into a Cairn feature that stopped
  working. The pin carries the game version with it, because a given Optimum revision
  builds for exactly one Vintage Story version.
- **Cairn knows several of them, one per game version.** Optimum supports one version at a
  time and drops the previous one; packs do not move that quickly, because a pack sits on
  the version its mods have releases for. With a single pin, shipping a Cairn release took
  Optimum away from those packs — an update nobody asked for, removing something that
  worked. Old revisions keep building the same client for ever, since Optimum pins the
  upstream refs it patches against; what ages is the evidence, not the revision. So the
  list is as long as somebody is willing to re-run 20-minute builds for, and an entry
  nobody will re-run should be deleted rather than kept: a button that fails twenty minutes
  in is worse than no button. `OptimumSource.Known` is the list, and `ForGame` is the only
  thing that decides which build a pack is offered.
- **Cairn cannot install the prerequisites**, so it names all of them at once with a
  reason and a command each. Windows needs only Git (`bootstrap.ps1` implements every
  fixup natively); Linux and macOS additionally need perl, python3, curl and tar. A .NET
  SDK is *not* a prerequisite — Cairn fetches a private one the same way it fetches a
  private runtime.
- **The result is a variant, and a variant never runs by accident.** See below.

## A modified client only runs because you said so

A fork reports the version it was forked from, so it is indistinguishable from the real
game by metadata alone. An Optimum build of 1.22.5 answers "is 1.22.5 installed?" exactly
as the stock game does — and would then be handed silently to every 1.22.5 pack on the
machine. That is ruled out by construction rather than by care:

- a build Cairn made marks itself with a `.cairn-variant` file, and one somebody pointed
  Cairn at is recorded in `~/.cairn/games/external.json` — the two say the same two things
  and are applied at the same point. A directory that says neither reads as the stock game,
  whatever else it contains, and no automatic lookup ever returns a variant either way:
  only a choice recorded against a specific pack;
- both name **which executable to run**. Optimum ships a copy of the vanilla client
  plus its own launcher, byte-identical game binaries and all, and does its patching at
  startup from that launcher. An install without this runs the stock game while every
  message says otherwise — which is exactly what happened before the marker carried it.
  A named launcher that is missing, or a name carrying a path rather than a bare filename,
  makes the install invisible rather than falling back to the stock binary beside it: the
  fallback was that same substitution reached by writing something a marker may not say;
- a recorded choice stops applying when the pack's game version moves away from it. The
  pack's mods were resolved against the version it *now* targets, so a client nothing in
  it was chosen for is not an override, it is a mismatch. It stops applying too when the
  directory is no longer a modified client at all — see `ChoiceState.NotAVariant` below;
- the diagnostics report says which install a pack actually runs, and marks a variant
  loudly. "The game is behaving oddly" is unanswerable without it.

### A client you built yourself

Cairn's pin only moves when Cairn does, so somebody who builds Optimum themselves was waiting
on a Cairn release to use it — and a pack on a game version Cairn has no revision for was
offered nothing at all. **Use a client I built…** in a pack's Settings tab, or:

```
cairn-cli optimum use <dir> --pack <id>       run a client you built
cairn-cli optimum use --stock --pack <id>     put the pack back
cairn-cli optimum forget <dir>                stop offering it
```

`ClientAdoption` decides whether a directory can be used, so both front-ends refuse the same
things: not an install, no Optimum launcher in it, no readable version, or a version other
than the pack's. The launcher check is the one that matters — Optimum's output is a copy of
the vanilla client plus its own launcher, so a directory without one is the stock game, and
taking it would produce a pack announcing Optimum and playing vanilla.

Three things about this are deliberate:

- **The directory is referenced, never copied.** Copying would defeat the point: the reason
  to point at your own build is that you rebuild it, and a copy goes stale the moment you do.
  Cairn runs the folder you named and never updates or deletes it, and **Forget this client**
  in Preferences → Games forgets the record rather than the directory.
- **The record lives on Cairn's side**, in `~/.cairn/games/external.json`, rather than as a
  `.cairn-variant` written into their tree. A marker there is the obvious implementation and
  is wrong for exactly one reason: Optimum's packager rewrites its output directory, so the
  marker does not survive a rebuild — and what is left is a directory that reads as the stock
  game with a pack still pointed at it, launching vanilla with nothing able to say so.
  `ExternalClients` carries the reasoning; `GameStore.At` is the only place it is applied,
  which is what keeps an *unrecorded* directory reading as whatever it looks like.
- **A choice that stops meaning anything stops applying.** `ChoiceState.NotAVariant` is the
  case: forget a client, or delete a marker out of a build, and the recorded directory is
  still an install of the right version. Honouring it then runs the stock binary sitting
  beside the launcher — out of somebody's build directory, right after they asked Cairn to
  stop using it — so the pack falls back to the stock install and says why.

The build tree is kept under `~/.cairn/builds/optimum` so a rebuild is minutes rather than
another full decompile. It is a few gigabytes idle between pin bumps, hence
`optimum clean`.

One tree serves every build, which is why two things happen around it that would otherwise
look like fussiness. The remote is reset to the revision's own repository before fetching,
since the builds do not all come from the same fork. And bootstrap is passed `--refresh`
whenever the tree was last built at a *different commit*: it reuses the decompiled snapshot
and the cloned upstream forks whenever they are merely present, and those were cloned at the
refs of whichever revision put them there — reusing them across revisions produces a client
made of two, which nothing downstream could detect. The note recording what the tree holds
is `~/.cairn/builds/optimum-tree.json`, kept outside the checkout for the same reason the
log is.

## What a downgrade actually risks

Retargeting a pack at an older game version warns about its worlds, and the warning is
worth reading rather than fearing. The game is more forgiving here than it first appears:
opening a save a newer build touched produces a warning — `"versionmismatch-savegame": "Was
opened in a newer version of the game, might not load correctly"` — and not a refusal.

The one-way step is the *file format* upgrade, which prompts separately ("This world uses an
old file format that needs upgrading … It is also suggested to first back up your savegame")
and is keyed on `GameVersion.DatabaseVersion` rather than on the version string. That number
is still `2` and has not moved once in this source history. So it is a rare event, and not
something a patch-level change brings on.
