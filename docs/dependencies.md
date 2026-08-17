# Mod dependencies

A mod can require another mod. `carryon` needs `carryonlib`; Expanded Foods needs
`aculinaryartillery`. Install one without the other and the game disables it on startup —
which is where most people first learn the dependency existed.

Cairn installs exactly the modids a pack names, so today it produces that situation.

## ModDB does not know

The obvious place to look is the API, and it isn't there. Neither the mod object nor the
release object carries dependency data:

```
mod keys:      assetid author comments created downloads follows homepageurl issuetrackerurl
               lastmodified lastreleased logofile … releases screenshots side tags text …
release keys:  changelog created downloads fileid filename mainfile modidstr modversion
               releaseid tags
```

The only trace is prose inside `changelog` HTML — *"requires carryonlib 1.0.0-pre.4"* —
which is not something to parse.

## `modinfo.json` does

The authoritative source is inside the zip, which is also what the game reads:

```
carryon         "dependencies": { "game": "1.22.0", "carryonlib": "1.0.0-pre.8" }
betterruins     "dependencies": { "game": "1.22.0" }
shelfobsessed   no "dependencies" key at all
```

Four properties of that data, each of which is a bug if assumed away:

- **The field is optional.** `shelfobsessed` omits it entirely. Null, not empty.
- **Versions are minimums.** `ModDependency.Version` is documented as "the minimum version
  requirement", so `"carryonlib": "1.0.0-pre.8"` means ≥. It is also parsed by the same
  splitter that makes `">=1.22.0"` silently mean `0.22.0` in a game dependency —
  `"1.0.0-pre.8"` splits on `.` and `-` to `[1, 0, 0, pre, 8]`, and `pre` becomes `0`.
- **Three ids are the game, not mods.** `game`, `survival` and `creative` are the asset
  domains in the install. Asking ModDB for them finds nothing; they must be filtered.
- **Ids are case-insensitive in practice.** `ModInfo.IsValidModID` allows lowercase and
  digits only, but compare without case anyway rather than trusting every author.

## What this does to sync

Dependencies live inside the zip, so **a pack's full mod set is not knowable until it has
been downloaded**. Sync stops being one pass over `manifest.Mods` and becomes a fixpoint:

```
seed the queue with the manifest
  take a mod → resolve → download (or verify what is on disk)
             → read modinfo.json → enqueue dependencies not already seen
until a round adds nothing
```

Termination is by a visited set of modids, which also handles two libraries that declare
each other. Everything else in the syncer — the lock check, the checksum refusal, the
untrusted-URL fallback, the stray-zip sweep — applies unchanged to a mod that arrived as a
dependency, because by the time it is processed it is just another mod in the queue.

Dependencies must be read for **every** locked mod, not only freshly downloaded ones. A
pack that is already settled still has to know its closure, or removing `carryon` would
never reveal that `carryonlib` is now an orphan.

## Where they are recorded

In the **lock, not the manifest**.

`pack.json` is declared intent. You did not ask for `carryonlib`, so writing it there would
misstate what you asked for — and since the manifest is what travels when you publish, the
misstatement would travel too. `pack.lock.json` is what is actually installed, which is
exactly what a dependency is.

Three things fall out of that placement rather than needing to be built:

- **Orphan removal.** The syncer rebuilds the lock each run and deletes zips it did not
  account for. Remove `carryon` and `carryonlib` stops being reachable in the next closure,
  so its zip goes with it.
- **Publishing.** The bundle carries the lock, so a recipient receives `carryonlib` at the
  author's exact version without the author having done anything.
- **The both-at-once case.** A mod that is both explicitly added and required by something
  else appears once in the manifest and once in the lock. Removing the manifest entry
  leaves it installed, because the closure still reaches it.

The lock entry records why it is there:

```json
{
  "modid": "carryonlib",
  "version": "1.0.0-pre.8",
  "requiredBy": ["carryon"],
  "sha256": "…"
}
```

Absent for a mod the manifest asked for directly. This is additive — `System.Text.Json`
ignores unknown members, so an older Cairn reads a newer lock without complaint, and
`PackBundle.CurrentFormat` must **not** be bumped for it (`Parse` rejects anything above
the current format outright).

## Failures

- **A dependency that is not on ModDB** — bundled, renamed, or never published. Report the
  id *and who wanted it*; `no release marked for game 1.22.5` is unhelpful when the user
  never heard of the mod being named.
- **A version minimum that cannot be met** — the newest release for this game version is
  older than what the dependent asks for. Warn rather than fail: the game itself only
  checks at load, and a pack that installs with a warning beats one that refuses.
- **A dependency with no release marked for this game version** — installed anyway, on the
  word of the mod that requires it, and warned about on every sync naming who wanted it.
  See below.
- **A mod that fails to download** — its dependencies are not discovered, and should not
  be guessed at. It is already a failed step.

## Who accepts a mod ModDB has not marked

A mod the manifest names carries an `acceptedFor` when somebody said they ran it against a
version it publishes nothing for; without one it fails, loudly, and the launcher's *Add
anyway* and `cairn-cli add --accept-unmarked` are how that testimony gets written down.

A dependency has no manifest entry, so it can hold no acceptance — and no control anywhere
could write one, since dependency rows carry no actions for the reasons above. Applying the
named-mod rule to it therefore had exactly one outcome: refusal, with no argument available.

So a dependency is accepted by **the mod that requires it**. Floral Zones' 1.22 bridge is
marked for 1.22 and requires seven region mods last marked for 1.21 — a mismatch that is the
entire purpose of a bridge mod. Refusing them installed the bridge and nothing it bridges,
which the game then disables on startup over the missing dependency, so the refusal bought
no safety; it only moved where the pack broke and took away the lever. The requirer is also
the better witness: its author shipped a release for this game version that names these mods
by id, which is a stronger statement about the pairing than a user ticking a box.

It is not silent. The release is recorded in the lock as `markedFor`, the sync warns every
run — *"installed because floralzones122bridge requires it, and it may misbehave"* — and the
row says `marked for 1.21.5, 1.21.6` in the pack's mod list for as long as it is true.

A mod the manifest names is unaffected, including one that is also required by something
else: it is in the pack because you asked for it, so it is yours to accept.

## What cannot be known in advance

**Previews cannot see new dependencies.** The game-version change dialog and update check
both work from ModDB metadata without downloading anything, so neither can predict that
retargeting to 1.23 pulls in a library that the 1.22 build did not need. Downloading to
find out would make the preview cost as much as the change.

So a preview says the mod set may grow; it does not enumerate it. That is honest, and it
matches the existing treatment of `could not be checked` — a preview is worth nothing if it
guesses.

The fix for this lives on the server, not the client: cairns.gg harvesting modid → deps
from the zips of published packs would let the client know the closure before downloading.
It is metadata rather than archives, so it does not touch the rule about not hosting mods.
Not v1.

## Updates ride along

A dependency is in the lock, not the manifest, so nothing offers it an update of its own.
Instead it inherits: `allowUpdates` seeds a set of mods permitted to move, and a mod that
may move passes that to everything it requires, down the chain. Updating `carryon` while
`carryonlib` stays behind is how the game ends up disabling the mod, and the newer mod is
usually the whole reason the newer library is needed.

Membership is read when a mod is dequeued rather than when it is enqueued, so a second
requirer discovered while the library is still queued can also free it to move. A plain
sync — which is what every Play does — still moves nothing.

## Interface

Dependency rows render under whatever pulled them in, marked `↳`, captioned *required by
carryon*, and ordered so a library always follows its requirer rather than sorting away
from it alphabetically.

They carry no Remove and no version dropdown. Remove is incoherent while the dependent is
still in the pack — the next sync would reinstate it. Pinning was considered and left out
for now: a dependency has no manifest entry to hold a pin, so honouring one would mean
promoting it to a mod the pack names, which is a different action wearing a dropdown's
clothes. A control that silently fails to persist is worse than no control.

Adding a mod triggers a **background sync**, because otherwise the row sits there with no
version until the next Play — and a mod's dependencies cannot be shown at all until it has
been downloaded and its `modinfo.json` read. The added row says *downloading…* until sync
reaches it. That sync is quiet: it installs and logs, but a failure does not raise the
pane's error banner, because adding the mod did work and Play will report properly when it
retries.
