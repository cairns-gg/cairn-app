# What's new in Cairn 0.9.0

Everything that changed since the 0.8 series, in one place. Nothing here needs any action
from you unless it says so.

## A pack can carry the mod settings that make mods work together

Some mods only get along once you have edited one of their config files. Terrain Slabs needs
Footprints named in a list before the two behave together; plenty of other pairs are the
same. Until now the author of a pack worked that out once and then had to write "and go and
edit this file" in the description — so everyone who installed the pack either did it by
hand or, more often, did not, and ran a pack that was quietly worse than the author's copy
of it.

A pack can now carry those values, and Cairn puts them in place when you press Play.

In `pack.json`:

```json
"modConfig": {
  "terrainslabs.json": {
    "compatibleMods": ["footprints"]
  }
}
```

The path is relative to the pack's own `ModConfig` folder, so a mod that keeps its settings
in a subfolder works too — `"XLeveling/mining.json"`.

**It carries only the values it names.** Not a copy of the whole file: the rest of that
file stays whatever the mod says it should be, which means a pack does not go stale when the
mod ships a new setting, and it means you can read a pack's `modConfig` before importing it
and see exactly what it intends to change.

**Anything you have changed yourself stays changed.** The first time a pack asks for a
value, it gets it. After that, if you change that setting — in game, or by editing the file
— it is yours, and later versions of the pack will not take it back. Cairn says which
values it set and which it left alone, every launch, rather than editing your files quietly:

```
terrainslabs.json: set compatibleMods
statushud.json: left showClock alone — it has been changed here
```

If a pack stops setting something, it says that too, and leaves the value as it is.

**`cairn-server` applies them as well**, and prints the same lines at startup. A good half of
these settings are server-side rules — who may recover a grave, how fast food spoils, what
view distance a client may ask for — so an admin following a pack gets the author's answers
without editing anything by hand.

**Mods that use ConfigLib are covered**, both kinds — the ones where ConfigLib is just an
in-game editor for the mod's own config file, and the ones where it keeps the settings itself
in a `.yaml`. Anything you change through ConfigLib's screen afterwards stays yours, as
always. One wrinkle for the second kind: ConfigLib writes that file the first time the mod
runs, so a pack's value for it arrives on your **second** launch rather than your first.
Cairn says so when it happens.

A few files still cannot be carried, and Cairn says so instead of pretending: `.ini` files,
files whose contents are a list rather than a set of settings, and files containing `//`
comments — several mods use those to document their own settings inside the file, and
rewriting one would delete them. In a real 74-mod pack that leaves 110 of 114 config files
usable.

## The Mod config tab picks them out for you

You do not have to work out which key you changed, or write any of the above by hand. A pack
now has a **Mod config** tab, next to Hotkeys.

It lists the settings you have changed from what each mod first wrote — with the old value
beside the new one, so you can see at a glance whether a value is yours or the mod's. Tick
one and it is carried in the pack, saved as you go. Untick it and it is not.

```
Rooms.Enabled              BedSpawn.json               was false   true
AllowOtherPlayersPickup    gravestones.json            was false   true
StepHeight                 StepUpAdvancedConfig.json   was 1.2     1.6
compatibleMods             terrainslabs.json           was []      [footprints]
```

Change a setting in game, alt-tab out, and it is in the list.

**Open config folder** is there too, for when you would rather edit a file yourself — it
opens the pack's own `ModConfig` folder in Finder or Explorer, which is otherwise buried
several levels down and different for every pack. **The list keeps up on its own**: save a
file in an editor, or change a setting in ConfigLib's screen, and the row updates while you
are looking at it.

Cairn learns what a mod ships by remembering what it wrote the first time it ran, so **for
packs you already have, the list starts empty** — play the pack once and it fills in. It says
so rather than pretending nothing has changed. There is a **Show all** box for every setting
Cairn can read, which is also the way to find a value you changed during a pack's very first
session, before there was anything to compare it against.

## Cairn speaks more than English

Every word the launcher says now comes from a translation file rather than being written into
the program, and there is a **Language** setting in Preferences to pick one. It applies as
you choose it — no restart, the window you are looking at changes under you.

By default Cairn works it out: it follows **the language your Vintage Story is set to**, then
your system's, then English. If you already play in Spanish, Cairn starts in Spanish without
being told.

**The translations are drafts, and they say so.** English is complete. Spanish, French and
both Portuguese variants cover the interface you see most — tabs, buttons, the mod list, the
Mod config tab, Preferences — and are waiting on a professional reviewer before anyone should
trust them. Anything not translated yet shows in English rather than going blank, so a
half-finished language reads as half-finished.

Five to choose from:

| | |
|---|---|
| English | complete |
| Español, Français | draft, awaiting review |
| Português (Portugal), Português (Brasil) | draft, awaiting review |

The two Portuguese variants are separate files rather than one with regional patches, because
they differ throughout rather than in a handful of words.

**If you want to fix something, or add a language:** the files are the same flat JSON every
Vintage Story mod ships in `assets/<domain>/lang/`, so if you have written one for a mod you
have written one of these. Point `CAIRN_LANG_DIR` at a folder holding your own `de.json` and
Cairn reads it in preference to the built-in ones and offers it in the picker — no need to
build anything.

## Packs you share are listed by default

Sharing a pack offers **Public** or **Unlisted**, and it used to arrive on Unlisted. That is
a fine thing to choose and a poor thing to be given: plenty of packs were shared by people
who had no idea nobody could find them, because the setting was already answered and the
only thing left to press was Publish.

New shares now start on **Public — listed in browse**, so a pack you share can be found and
tried. Unlisted is the same one tick away it always was, and a pack you have already
published keeps whatever it was published with.

**Your server address is still stripped**, and now in both directions — choosing Unlisted
does not put it back on its own. An unlisted link gets pasted into a chat like any other, so
if you want the address in the pack you hand your own players, tick **Include** beside it.

## Moving Cairn's files now takes the launcher with it

**Preferences → Move…** put everything on the new drive, and then the window carried on as
though it had not: the pack's Settings tab still named the old disk, a new pack was made
against it, and pressing Play downloaded every mod again into a folder that was no longer
being used. Restarting Cairn put it all right, because the files had been where they should
be the whole time — it was the launcher that had not noticed.

It notices now. The pack list, the paths on screen and the next launch all follow the move
as it finishes, with no restart.

**If this happened to you**, the mods that were downloaded again are in the old location,
where Cairn will never look for them. It is safe to delete that folder — the only thing in
there worth keeping is the small `home` file, which is what points Cairn at the new drive.

## Upgrading

Nothing to do. A pack that carries no mod config behaves exactly as it did, and Cairn
stays in English unless it finds a reason not to.
