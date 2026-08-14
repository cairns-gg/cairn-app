# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Cairn manages Vintage Story modpacks: a pack is a manifest + lockfile + a directory of mod
zips, handed to the game via `--addModPath`. Any mods — nothing filters by side; a
server-side one is installed with a warning (`PackSyncer.cs`), not skipped. It also installs the game
itself and private .NET runtimes for it, and publishes packs to cairns.gg.

`README.md` is unusually complete — it documents the reasoning behind most decisions here
(sync semantics, macOS notarisation, R2 publishing, Inno Setup switches). Read the relevant
section before changing anything in those areas; the reasons are rarely re-derivable.

## Build and test

```bash
dotnet build                                                   # whole solution

./dev.sh                    # publish for this host only (~5s) — prefer over `dotnet run`
./dev.sh --run              # build, then launch it
./dev.sh --no-sign          # skip macOS code signing
./dev.sh --cli              # CLI only (~2s)
./dev.sh --local            # build + run against a cairns on localhost, sandboxed in ~/.cairn-dev

./build-release.sh          # all four RIDs; ./build-release.sh linux-x64 for one
./build-macos-app.sh        # artifacts/osx-arm64/Cairn.app
```

On macOS `dotnet run` uses whatever SDK is on `PATH`; if that is x64 the launcher runs under
Rosetta and feels sluggish. `dev.sh` publishes for the host RID, and on macOS produces the
`.app` bundle (needed for `cairn://` link registration, the Dock tile and the menu-bar name).

### Tests

The two suites run **differently**, and getting this wrong looks like success:

```bash
dotnet test tests/Cairn.Core.Tests/Cairn.Core.Tests.csproj           # xunit v2, ~428 tests
dotnet test tests/Cairn.Core.Tests/Cairn.Core.Tests.csproj --filter "FullyQualifiedName~PackSync"

dotnet build tests/Cairn.App.Tests/Cairn.App.Tests.csproj
dotnet tests/Cairn.App.Tests/bin/Debug/net10.0/Cairn.App.Tests.dll   # xunit v3, ~201 tests
dotnet tests/Cairn.App.Tests/bin/Debug/net10.0/Cairn.App.Tests.dll -method '*Version*'
dotnet tests/Cairn.App.Tests/bin/Debug/net10.0/Cairn.App.Tests.dll -class '*ShareWindowTests'
```

`Cairn.App.Tests` is **xunit v3** (required by `Avalonia.Headless.XUnit` 12.x) and is a
self-hosting executable. `dotnet test` on it — or on the solution — discovers **zero tests
and exits 0**, so a whole suite silently vanishes. Always run the dll directly.

The UI tests render the real window on Avalonia's headless platform with Skia drawing and
assert on the visual tree, because Avalonia resolves bindings at runtime and a stale binding
path fails silently. Two things to know when writing them:

- A `TabControl` only realises the selected tab — select it before asserting on its contents.
- The whole assembly shares one collection (`AvaloniaTests.Collection`) with parallelisation
  off. The headless session cannot survive being torn down and rebuilt across collection
  boundaries; splitting a class out reintroduces an intermittent, misattributed failure.
  `TestAppBuilder` also points `CAIRN_HOME` at a fresh temp dir so the suite never reads the
  developer's real `~/.cairn/settings.json`.

The `Cairn.Core.Tests` conformance suite compiles only when `VINTAGE_STORY` points at a game
install (`HAS_GAME`); it runs Cairn's version comparator against the real
`Vintagestory.API.Config.GameVersion` over a corpus. A clean checkout skips it.

### Auditing what ModDB actually serves

```bash
dotnet run tools/moddb-audit.cs -- fetch     # ~8000 mods, 1 req/s, resumable
dotnet run tools/moddb-audit.cs -- check     # Cairn's own parser over the corpus
```

A file-based app (`#:project`), so it compiles against the real DTOs without being a fourth
project. `check` drives `ModDbClient` through a handler serving the cached bytes rather than
restating the parse — a script with its own copy of the rules only proves the copy agrees
with itself. It reports entries that fail outright, releases that parse but cannot be
installed, and a census of which JSON kinds each field was actually seen holding, which is
what catches the next `"fileid": null` before a user does.

`fetch` is deliberately serial at one request per second and stops after ten consecutive
failures: ModDB publishes no rate limit and sends no headers about one, so the only safe
reading is that someone is paying for the bandwidth. Re-running costs nothing for what is
already cached.

## Architecture

Four projects, one engine:

- **`src/Cairn.Core`** — everything. Deliberately references **nothing**, not even the game's
  assemblies, so it builds in a clean CI container with no Vintage Story install.
- **`src/Cairn.Cli`** (`cairn-cli`) — headless front-end; a `switch` over `args[0]` in `Program.cs`.
  A development tool, deliberately not shipped in releases.
- **`src/Cairn.App`** (`cairn`) — Avalonia 12 GUI, CommunityToolkit.Mvvm, `ViewLocator`
  mapping `*ViewModel` → `*View` by name.
- **`src/Cairn.Server`** (`cairn-server`) — follows a pack and runs a dedicated server under
  systemd. Shipped for **linux-x64 only**. Server installs live under `~/.cairn/servers`,
  apart from the client versions in `~/.cairn/games`.

**Rules live in Core, not in a front-end.** Both UIs mutate packs only through `PackStore`,
and every policy question — may this be published (`PublishRecord.WouldChange`,
`ShareState`), may this URL be imported (`PackSources`), is this id safe (`PackId`) — is
answered by a Core type both call. A check implemented in a view model is a check the CLI
does not make, and a hidden button is not a rule: commands are reachable regardless of what
is drawn.

### The pack model

```
~/.cairn/packs/<id>/
  pack.json        PackManifest — declared intent, hand-editable, shareable
  pack.lock.json   PackLock — exactly what was installed, with SHA-256 per mod
  cairns.json      PackLink — this copy's relationship to cairns.gg (never shared)
  Mods/            the zips, handed to the game via --addModPath
  data/            this pack's Saves, ModConfig, Playerdata, clientsettings.json
```

`CairnPaths` is the single source of truth for all of these. The root is `CAIRN_HOME`, then a
`home` pointer file in the default root naming somewhere else, then `~/.cairn` — `CairnHome`
owns that order and the reasons, and the environment always wins. It is re-evaluated on every
access rather than cached, because the test suites move `CAIRN_HOME` per class.

**Sandbox with `CAIRN_DEFAULT_HOME`, not `CAIRN_HOME`** — it moves the *default* root rather
than overriding it, so the pointer file and Preferences → Move… behave exactly as they do for
a real user. `CAIRN_HOME` outranks the pointer, so a sandbox built on it exercises the one
branch nobody takes and makes the move refuse itself. `dev.sh --home/--local` and
`HomeMoveTests` both use it.

- **Sync installs what the lockfile says.** `PackSyncer` resolves against ModDB only when it
  must (never installed, moved pin, retargeted game version). Sync runs on every Play, and
  mods break saves, so a launch must not move mods underneath a pack. Updating is opt-in via
  `allowUpdates`. A pinned mod is never offered an update.
- **Sync is a fixpoint, not one pass.** Dependencies live in `modinfo.json` inside the zip —
  ModDB's API does not carry them — so the full mod set is unknown until things are
  downloaded. See `docs/dependencies.md` for the four properties of that data that are each a
  bug if assumed away.
- **`PackLink` decides ownership.** An imported pack is a `Follower` and can be neither
  published nor exported; `PackRole.Author` is the publishable end. `ShareState` is a
  projection recomputed from the manifest and lock, never cached — a cached one is how a
  button ends up lying about a pack. "Take over" is specced in `TODO.md` and unimplemented.
  A withdrawn pack keeps its `Url` and sets `Withdrawn` with `Published` cleared, so
  republishing it unchanged revives it; the flag is stored rather than inferred because a
  taken-over pack has the same shape (Author + URL + nothing published) and means the
  opposite.
- **Every pack has its own data path**, and `PackData` merges seven named session keys into
  each pack's `clientsettings.json` at launch so one login reaches all of them. Merging named
  keys — not copying the file — is what keeps keybinds and graphics settings per-pack. Cairn
  only ever *reads* the user's own Vintage Story data path.

### Launching

`GameProvisioner` gets a version launchable: `GameStore`/`GameInstaller` fetch the game
(Inno Setup `.exe` on Windows, tarball elsewhere), `RuntimeStore`/`DotnetRuntimeInstaller`
fetch a private .NET when the machine has none. `GameLibrary` merges Cairn's installs with
whatever pre-existing install `GameInstall.TryLocate` finds (`VINTAGE_STORY` wins).
`GameLauncher` reads the game's Mach-O/PE/ELF arch and its runtimeconfig, resolves a matching
runtime, and sets **both** `DOTNET_ROOT` and `DOTNET_ROOT_X64` — or, when nothing matches,
deliberately sets neither, so hostfxr's own fallback is not clobbered.

### Version strings

The game parses versions with `int.TryParse` per segment, so `">=1.22.0"` silently becomes
`0.22.0` and matches everything. Cairn refuses such strings in a manifest rather than passing
them through — bare versions only (`"1.22.5"`). `GameVersions.IsPlausibleVersion` is the
gate; `GameVersionComparer` is a deliberate port of the game's own comparator, held to it by
the conformance tests.

## Conventions

- **Comments say why, not what.** The codebase is dense with XML doc comments recording the
  reasoning and the failure mode a decision avoids, often naming what was tried and what it
  broke. Match that: a change that removes a constraint should say what makes it safe now.
  British spelling throughout ("notarised", "behaviour").
- **Commit messages are sentences in the imperative, capitalised, no prefix or scope** —
  "Keep each version's manifest, not just the latest", "Stop shipping cairn-cli in releases".
- Releases are cut by pushing a tag; `.github/workflows/release.yml` tests, builds all four
  artifacts, opens a draft GitHub release and publishes to Cloudflare R2. The version comes
  from the tag via `-p:Version` and is read back out of the assembly by `CairnVersion` —
  never hardcode one. An unstamped build reports `dev`.
