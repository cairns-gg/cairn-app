# Why a pack keeps to itself

Two decisions with the same root, and both changed behaviour people had already got used to:
a pack has its own data path, and a pack does not inherit the Mods folder of the install it
came from. Neither is configurable.

What follows is why, and what each one prevents — including the most-reported bug there has
been.

## Each pack has its own worlds, but you only log in once

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

## The mods folder a pack used to inherit

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
