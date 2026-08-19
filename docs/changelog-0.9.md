# What's new in Cairn 0.9.3

Everything that changed since the 0.8 series, in one place. Nothing here needs any action
from you unless it says so.

## Updates notice a revision that only updates mods

**0.9.3.** The commonest revision an author publishes is the one where they have taken the
month's mod updates and changed nothing else. Cairn could not see it. Every follower said
**"already on the author's newest revision"**, `cairn-server update` said it and exited 0,
and the pack stayed on the versions it had — five mod updates waiting at the author's URL
that nothing would ever pull.

A pack lists the mods it wants; it does not usually name a version for any of them, because
not naming one is what lets you take updates. The versions the author actually shipped live
in the pack's lockfile, which is the file that makes their pack reproduce exactly on your
machine. Cairn decided whether an update did anything by comparing the two mod *lists* — and
those lists were identical, revision after revision, while everything that mattered moved
underneath them.

It now compares the versions as well. A revision that moves them says so, mod by mod, in the
update window and in `cairn-server update`:

```
Revision 18: 5 updated.
  scribe                   1.1.1 → 1.2.1
```

**Nothing gets pinned by taking one.** A mod nobody named a version for stays that way, so
the pack goes on following the author rather than freezing at whatever it caught up to.

**If a pack of yours has been stuck**, there is nothing to undo — check for an update and it
will be there. On a server, `cairn-server update <pack>` followed by a restart. The revisions
you missed are not applied one at a time: the author's newest is the one you get, which is
the same set of mods anyone importing the pack fresh would receive.

## Cairn finds your Vintage Story in more places, and you can point it at one

**0.9.3.** Cairn looks for your own install to work out which game version you are playing,
and it looked in three or four places per platform. If yours was somewhere else — a second
drive, `/opt`, your home directory, the 32-bit Program Files — it found nothing and said so,
and that was the end of the conversation.

**It also cost more than the message admitted.** The version an import targets comes from
that install, and so does something subtler: when a mod is not marked for your game version,
Cairn will take it anyway on the grounds that you are visibly running it. That allowance is
testimony about a particular version — so with no install to read, there was no version, and
the allowance quietly switched off. Mods you have been happily playing came back as *nothing
for this game version* and were left out, and nothing on screen connected that to the line
about no install being found.

More places are searched now. On Windows that includes `Program Files (x86)` and the
conventional game folders on your other drives; on Linux, `/opt/vintagestory`, a
`vintagestory` folder in your home directory, and whatever `XDG_DATA_HOME` points at.

**And if it still cannot find yours, tell it — on the import screen itself**, which now says
where it is reading from in two lines you can correct:

```
Game   1.22.6    /Applications/Vintagestory.app                    Change…
Mods   /Users/you/…/VintagestoryData/Mods                          Change…
```

**Game** is the folder holding `VintagestoryAPI.dll`. It is checked as you choose it, so a
folder that is not an install is refused on the spot rather than stored and quietly ignored,
and the mod list is read again against the version it turns out to be. On macOS pick the
folder containing `Vintagestory.app` — a folder picker will not go inside the app itself.

**Mods** is new too, and it is there because the game keeps these in two entirely separate
places: your install can be anywhere, and your mods, worlds and settings live wherever the
game's `dataPath` points. If you have moved that, point Cairn at your Mods folder and the
worlds offered underneath follow it. Pick either that folder or the one holding it; both
work. Most people never need this — the usual way to move it on Windows is a junction, which
leaves everything resolving as it always did.

Both are remembered, so these are once-only corrections. Correcting the install is worth it
even if you never import again: a pack whose game version matches now launches from your copy
instead of Cairn downloading a second one.

## An import brings what you are running, and you take out what you do not want

**0.9.3.** Importing from your install used to hand you Cairn's decisions and no way to argue
with them. A mod ModDB publishes nothing for on your game version was left out. A mod whose
exact release it does not serve for you was quietly swapped for a newer one. Both were
reported on the row and neither was something you could press.

That is backwards for a screen whose whole job is "give me a pack of what I am running". So:

**The version you have is the version that goes in**, wherever ModDB will still serve it —
including when nothing is marked for your game. Updating is a button, one press away, and
stays a decision rather than a side effect of importing.

**Every mod that can go in starts ticked, and you untick what you do not want.** The list
opens holding your folder and is read to take things out of, rather than assembled a mod at a
time.

**A mod Cairn cannot install keeps its box, unticked and greyed**, with the reason beside it.
Cairn installs from ModDB and never copies the zip out of your folder — a pack whose mods come
from one machine cannot be shared or reproduced by anybody — so a mod ModDB has never heard of
cannot be brought in however much anyone would like it to be.

**Worlds are one box now**, not a list: *Bring my 3 worlds across too (12.4 GB)*. It says what
it will cost and it is off by default, because a world is gigabytes and the pack works without
one. Choosing between them one at a time is still there, and better placed — the pack's own
Settings tab does it with the pack in front of you.

**And your mod settings come with them.** An import brought your mods and your worlds and left
behind everything that made them work together — plenty of mods only get along once a value
has been changed, so a pack with the right mods and the mod authors' defaults is not the thing
you had been playing, and there is no way to tell which of forty mods lost the setting that
mattered. *Bring my mod settings across too* is ticked by default: they are kilobytes, and
they are part of what you asked for.

Copied, never moved — your own Vintage Story goes on working exactly as it was, and nothing
already in the pack is written over. This puts the files in *your* copy of the pack; a pack
that carries settings to other people declares them in the Mod config tab, a value at a time,
which is unchanged. What this does is give that tab something to offer.

**If you have been running mods the tags call out of date**, this is the change you were
waiting for: they come in, named and ticked, saying what they are.

## Smaller things in 0.9.3

**Escape closes any of Cairn's dialogs.** Most already did; Preferences, Share, the pack
update and the game version windows did not, and Preferences could only be dismissed from
the title bar.

**Preferences no longer runs off the bottom of the screen.** At larger interface sizes — and
in the languages whose text runs longer than the English — the Overview tab could put the
language picker somewhere unreachable. It scrolls now.

## A mod your mods need is no longer refused for want of a tag

**0.9.2.** Mods can require other mods, and Cairn fetches those for you. If ModDB carried no
release of one of them marked for the version your pack is on, Cairn refused it — installing
the mod that wanted it, leaving out the thing it wanted, and putting the reason in the Log
tab. The game then disabled the mod on startup over a dependency that never arrived, and
there was nothing you could do about it from the pack: a mod that is only there because
something else requires it has no row you can accept anything on.

Cairn now installs it, on the word of the mod that asked for it. That is the ordinary case
for a bridge or a compatibility patch — Floral Zones' 1.22 bridge is built for 1.22 and
requires seven region mods whose newest releases are marked for 1.21, which is the entire
reason the bridge exists.

It is not done quietly. The mod's row says **marked for 1.21.5, 1.21.6** beside its version,
for as long as that is true, and the log says which of your mods asked for it on every
launch rather than only the first:

```
floralzonesmediterraneanregion 1.0.19 is marked for 1.21.5, 1.21.6, not 1.22.5
 — installed because floralzones122bridge requires it, and it may misbehave
```

**Mods you add yourself are unchanged.** One with nothing marked for your game version still
asks before it goes in, and still records that you were the one who said so. What is new for
those is the row: a mod you accepted months ago now says what it is marked for whenever you
open the pack, instead of only in the log of the launch that installed it.

**And removing a mod now takes what it brought with it.** Mods pulled in by another mod have
never had a Remove button of their own — the mod that wants them would only bring them back
— so removing the one that wanted them is how they go. That worked on the next Play and not
before it, which left a pack sitting there with seven mods requiring nothing and no way to
shift any of them. The launcher now settles the pack as soon as you remove something, the
same way it already did when you add something.

## ConfigLib settings a pack carries now actually arrive

**0.9.1.** A pack can carry mod settings. For mods that keep theirs in a ConfigLib `.yaml`,
0.9.0 said the value would arrive on your second launch, because ConfigLib writes that file
itself the first time the mod runs. It did not arrive on the second launch either. It never
arrived.

Cairn waited for the file, correctly — and then wrote down that it had already asked for
those values. So when the file turned up, holding nothing but the mod's own defaults, it read
as settings you had deliberately changed, and Cairn left them alone out of politeness. Every
launch, for ever, reporting "left alone — it has been changed here" about a setting nobody
had ever touched.

It hit ConfigLib `.yaml` mods only; a pack's values for ordinary `.json` config files landed
on the first launch as they always did. It hit `cairn-server` hardest, where a good half of
those settings are the server-side rules the pack exists to set.

**And they no longer wait at all.** Rather than fixing the second launch, 0.9.1 writes those
files before the first one, from the settings description the mod ships inside its own zip —
so a pack's values are in place before anything reads them. That matters most for the settings
that shape the world: how far apart ruins stand, how often a structure spawns. Those are read
while terrain is being generated, and terrain is not built again when the number changes
later. Under 0.9.0 a server's first world was generated against the mod's defaults no matter
what the pack said, and the only fix was to delete the world and start again.

Where a mod does not describe its settings well enough to write the file safely, Cairn waits
for ConfigLib as before and says so. It never writes over a file that already exists.

**If you ran 0.9.0 and a pack's ConfigLib settings never took**, upgrading is not quite
enough on its own — the note Cairn wrote to itself is still there, and it still says you own
those values. Delete this file and the next launch sets things right:

```
<your pack>/data/cairn-modconfig.json
```

On a server that is `~/.cairn/packs/<pack>/data/cairn-modconfig.json`. It is safe to delete:
it holds only Cairn's record of what the pack last asked for, and losing it means the next
launch treats the pack's values as a first word again. Settings you genuinely changed
yourself and want to keep, change back afterwards — or edit the file and remove just the
entry for the mod that was stuck. Writing the file ahead of time does not help here, because
by now it exists and Cairn will not write over one.

**If one of those settings shapes the world**, the terrain you already have was generated
against the mod's default and stays as it is; the pack's value applies to ground generated
from here on. Whether that is worth starting a world over is a judgement only you can make,
and on an established server it usually is not.

## Updates notice a revision that only changes mod settings

**0.9.1.** Publishing a revision that changed nothing but a mod setting — a normal thing to
publish, and the whole point of the feature above — produced a revision nobody could take.
Every follower checking for it said **"already on the author's newest revision"** and stopped
there. `cairn-server update` said it and exited 0, so a server sat on revision 10 while its
author published 11, with nothing anywhere reporting a problem.

Cairn works out whether an update does anything by comparing the two manifests field by
field, and mod settings were not among the fields it compared. Neither were keybinds nor the
server address, once; both were fixed when found, and this is the same omission a third time.
It now compares mod settings too, and a revision that changes them says **"changes mod
settings"** alongside the mods it adds and removes, rather than arriving unannounced.

**A second problem in the same place, which cost more.** When an update *did* go through —
because a mod changed as well — the pack's mod settings were dropped on the way. Taking any
revision at all emptied them, so a follower who updated lost every value the pack carried and
got no word that it had happened. Both halves are fixed together; there is nothing to do about
a pack this happened to beyond taking the author's next revision, which now brings the
settings with it.

**And in the launcher, an update could be undone by the next thing you did.** Taking a
revision wrote it to disk correctly, but the pack on screen went on holding the author's
*previous* keybinds and mod settings — so the next ordinary edit, renaming the pack or
changing its description, saved those old values back over the ones just taken. Nothing said
so, and the pack looked right until the next launch applied the wrong settings. The window now
takes the whole revision, and the Hotkeys and Mod config tabs refresh with it rather than
showing answers that are one revision out of date.

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
always. Those `.yaml` files do not exist until the mod has run once, so **0.9.1 writes them
ahead of time** from the mod's own settings description, and the pack's values are in place
for your first launch. Where a mod does not describe itself well enough to do that safely,
Cairn waits for it and says so, and the value arrives on the launch after.

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
being told. (On Windows the system step is skipped — Vintage Story's own setting is the
better signal there in any case.)

**The things that go wrong are translated too**, not only the buttons. A mod that cannot be
installed, a pack that will not import, a disk with no room on it — the sentence explaining
it comes from the same file as everything else, because the moment you most want your own
language is the moment something has failed.

**The translations are drafts, and they say so.** English is complete, and **0.9.2 finishes
the other four**: Spanish, French and both Portuguese variants now carry every sentence Cairn
can say rather than the screens you see most. That last part matters where it always did —
the sentence explaining a mod that would not install, a download that was corrupt, a disk
with no room on it. Those were exactly the ones still coming out in English, because they are
the ones nobody sees while translating the buttons.

They are still machine-written and still waiting on a reviewer who speaks the language, which
each file says at the top. Anything added later shows in English until somebody translates
it, rather than going blank.

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

## Smaller things

- **A mod row too long for the window wraps instead of running off it.** **0.9.2.** The
  name, the id, the version and any notes about a mod all sat on one line that could not
  shrink, so a mod with a long name — required by another mod, with something to say about
  what it is marked for — quietly lost whatever came last off the right-hand edge. The row
  now grows to a second line, and only when it has to.
- **Your settings stop overwriting each other.** Cairn kept its preferences in a file that
  was rewritten whole by whichever setting you had just changed, so a second setting would
  have vanished the first time you dragged the interface-size slider. That is why there was
  never more than one. It now keeps every setting it knows about, and every setting it does
  *not* — so a preference written by a newer Cairn survives being opened by an older one.
- **A few sentences that were wrong in English got fixed**, found by writing them out
  properly for translation: "1 thing you changed differ from the author's" now says
  *differs*, and the dialog that asks about a mod with no release for your game version no
  longer works out which version it means by taking its own sentence apart.
- **`cairn-server` can say which build it is.** It could not, by any spelling — so the one
  question every report about a server starts with had no answer on the box itself.
  `cairn-server version` answers it, `--version` and `-v` do the same, and `run` prints it
  as its first line so `journalctl` has it for a server nobody watched start.

## Upgrading

Nothing to do. A pack that carries no mod config behaves exactly as it did, and Cairn
stays in English unless it finds a reason not to.
