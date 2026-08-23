# What's new in Cairn 0.9.5

Everything since the 0.8 series. Nothing here needs anything from you unless it says so — see
**Upgrading** at the end.

## Updates notice what a revision actually changed

**0.9.3.** The commonest revision an author publishes takes the month's mod updates and changes
nothing else. Cairn could not see it: followers reported **"already on the author's newest
revision"**, `cairn-server update` said the same and exited 0, and the mods never moved.

A pack names the mods it wants, not usually their versions; those live in the lockfile. Cairn
was comparing the mod *lists*, which stay identical while everything that matters moves
underneath them. It compares versions now, and says which moved:

```
Revision 18: 5 updated.
  scribe                   1.1.1 → 1.2.1
```

Nothing gets pinned by taking one, so the pack goes on following the author. If a pack of yours
has been stuck, check for an update — you get the author's newest, which is the same set of mods
anyone importing it fresh would get. On a server, `cairn-server update <pack>` and restart.

**0.9.1 fixed the same shape of thing for mod settings**, which were also missing from the
comparison, along with two ways an update could go wrong once it did land: the pack's mod
settings were emptied on the way through, and the launcher went on showing the previous
revision's keybinds and settings until your next edit saved them back over the new ones. Taking
the author's next revision puts an affected pack right.

## Importing your install brings what you are actually running

**0.9.3.** Import used to hand you Cairn's decisions with nothing to press: a mod ModDB
publishes nothing for on your game version was left out, and a mod whose exact release it does
not serve was swapped for a newer one.

Now the version you have is the version that goes in, wherever ModDB will still serve it —
updating stays a button rather than a side effect of importing. **Everything that can go in
starts ticked**, and you untick what you do not want. A mod Cairn genuinely cannot install — one
ModDB has never heard of — keeps its box, unticked and greyed, with the reason beside it: Cairn
installs from ModDB and never copies the zip out of your folder, because a pack whose mods come
from one machine cannot be shared or reproduced by anybody.

**Your mod settings come across too**, ticked by default: the right mods with the authors'
defaults are not what you were playing. **Your worlds are one box** naming what they weigh, off
by default because they are gigabytes. Both are copied, never moved.

**Cairn is also better at finding your Vintage Story**: Windows now includes `Program Files
(x86)` and the usual game folders on your other drives, Linux `/opt/vintagestory`,
`~/vintagestory` and `XDG_DATA_HOME`. If it still cannot see yours, the import screen says where
it is reading from, in two lines you can correct:

```
Game   1.22.6    /Applications/Vintagestory.app                    Change…
Mods   /Users/you/…/VintagestoryData/Mods                          Change…
```

**Game** is the folder holding `VintagestoryAPI.dll`, checked as you choose it, so a folder that
is not an install is refused. On macOS pick the folder *containing* `Vintagestory.app` — a
picker will not go inside the app itself. **Mods** is asked for separately because the game
keeps it separately, wherever its `dataPath` points; pick your Mods folder or the folder holding
it, and the worlds follow. Most people never need this.

Both are remembered, and correcting the install is worth doing even if you never import again: a
pack whose game version matches now launches from your copy instead of Cairn downloading a
second one.

## A mod your mods need is no longer refused for want of a tag

**0.9.2.** Mods can require other mods, and Cairn fetches those for you. But if ModDB carried no
release of one marked for your pack's game version, it was left out, and the game then disabled
the mod that wanted it. There was nothing you could do from the pack: a mod that is only there
because something else requires it has no row to accept anything on.

Cairn now installs it on the word of the mod that asked for it, which is the ordinary case for a
bridge or compatibility patch. It is not done quietly — the row says **marked for 1.21.5,
1.21.6** beside the version for as long as that is true, and the log names which of your mods
asked for it on every launch:

```
floralzonesmediterraneanregion 1.0.19 is marked for 1.21.5, 1.21.6, not 1.22.5
 — installed because floralzones122bridge requires it, and it may misbehave
```

Mods you add yourself are unchanged: one with nothing marked for your version still asks first.
**And removing a mod now takes what it brought with it** as soon as you remove it, rather than
on the next Play.

## A pack can carry the mod settings that make mods work together

**0.9.0, completed in 0.9.1.** Some mods only get along once you have edited a config file —
Terrain Slabs needs Footprints named in a list before the two behave. Pack authors used to work
that out once and then write "and go and edit this file" in the description, which everyone
either did by hand or, more often, did not.

A pack carries those values now, and Cairn puts them in place when you press Play. In
`pack.json`:

```json
"modConfig": {
  "terrainslabs.json": {
    "compatibleMods": ["footprints"]
  }
}
```

Paths are relative to the pack's own `ModConfig` folder, so `"XLeveling/mining.json"` works too.

**It carries only the values it names**, so a pack does not go stale when a mod ships a new
setting, and you can read a pack's `modConfig` before importing to see exactly what it will
change. **Anything you have changed yourself stays changed**: a pack gets a value the first time
it asks, and never takes it back. Cairn says which it set and which it left alone, every launch:

```
terrainslabs.json: set compatibleMods
statushud.json: left showClock alone — it has been changed here
```

**`cairn-server` applies them too**, which matters because a good half of these settings are
server-side rules — who may recover a grave, how fast food spoils, what view distance a client
may ask for.

**Mods that use ConfigLib are covered**, including the ones that keep settings in a `.yaml` that
does not exist until the mod has run once. 0.9.1 writes those ahead of time from the mod's own
settings description, so the pack's values are in place for your first launch. That matters most
for settings that shape the world — how far apart ruins stand, how often a structure spawns —
because terrain is not generated twice. Where a mod does not describe itself well enough to
write the file safely, Cairn waits and says so.

A few files cannot be carried, and Cairn says so: `.ini` files, files whose contents are a list
rather than settings, and files with `//` comments, which several mods use to document
themselves and which rewriting would delete.

### The Mod config tab picks them out for you

You do not have to write any of that by hand. A pack has a **Mod config** tab, next to Hotkeys,
listing the settings you have changed from what each mod first wrote, with the old value beside
the new one:

```
Rooms.Enabled              BedSpawn.json               was false   true
AllowOtherPlayersPickup    gravestones.json            was false   true
StepHeight                 StepUpAdvancedConfig.json   was 1.2     1.6
compatibleMods             terrainslabs.json           was []      [footprints]
```

Tick one and the pack carries it. Change a setting in game, alt-tab out, and it is in the list.
**Open config folder** takes you to the pack's own `ModConfig` folder when you would rather edit
a file yourself.

Cairn learns what a mod ships by remembering what it wrote on its first run, so **for packs you
already have the list starts empty** until you play the pack once. **Show all** lists every
setting Cairn can read, which is also how to find a value you changed during a pack's very first
session.

## Cairn speaks more than English

**0.9.0, finished in 0.9.2.** There is a **Language** setting in Preferences, applied as you
choose it — no restart. By default Cairn follows the language your Vintage Story is set to, then
your system's, then English, so if you already play in Spanish it starts in Spanish without
being told.

The things that go wrong are translated too: the moment you most want your own language is the
moment something has failed.

| | |
|---|---|
| English | complete |
| Español, Français | draft, awaiting review |
| Português (Portugal), Português (Brasil) | draft, awaiting review |

The four translations are machine-written and still waiting on a reviewer who speaks the
language, which each file says at the top. Anything added later shows in English rather than
going blank.

**To fix something, or add a language:** the files are the same flat JSON every Vintage Story
mod ships in `assets/<domain>/lang/`. Point `CAIRN_LANG_DIR` at a folder holding your own
`de.json` and Cairn reads it in preference to the built-in ones and offers it in the picker —
nothing to build.

## Packs you share are listed by default

Sharing offers **Public** or **Unlisted** and used to arrive on Unlisted, which is a fine thing
to choose and a poor thing to be given: plenty of packs were shared by people with no idea
nobody could find them. New shares start on **Public — listed in browse**. Unlisted is the same
one tick away, and a pack you have already published keeps what it was published with.

**Your server address is still stripped**, and choosing Unlisted does not put it back on its
own. Tick **Include** beside it if you want the address in the pack you hand your own players.

## Moving Cairn's files takes the launcher with it

**Preferences → Move…** put everything on the new drive and then the window carried on as though
it had not: the Settings tab named the old disk, and pressing Play downloaded every mod again
into a folder no longer in use. Restarting fixed it, because the files had been in the right
place all along.

The pack list, the paths on screen and the next launch now follow the move as it finishes.

**If this happened to you**, the mods downloaded a second time are in the old location, where
Cairn will never look. That folder is safe to delete — the only thing worth keeping in it is the
small `home` file, which is what points Cairn at the new drive.

## Optimum, for 1.22.7 and for the version you are still on

**0.9.4.** Vintage Story **1.22.7** is out, and so is the Optimum build for it: Cairn now builds
**Optimum 0.3.11** from a pack's Settings tab, exactly as before.

Optimum supports one game version at a time and drops the one before it, and Cairn used to do the
same. That meant a Cairn update could take the option away from a pack that had not moved yet —
and a pack does not move until the mods it uses have releases for the new version, which can be
weeks. Cairn now keeps the older build as well: a pack on **1.22.5** is still offered **Optimum
0.3.5**, named as itself, while a pack on 1.22.7 is offered the new one.

Nothing you have already built is affected, and a pack already set to run an optimised client goes
on running it.

## Smaller things

- **Moving a pack to a new game version stops asking ModDB the same questions over and over.**
  **0.9.5.** Checking a version change looked up every mod; pressing **Publish** afterwards
  looked up every mod, synced, then looked up every one of them again before the window
  opened. On a seventy-mod pack that was the better part of three hundred requests, and a
  long wait watching mod names go by. Cairn now settles what is installed before it asks
  anything — that part is two files on your own disk — and remembers for a few minutes what
  ModDB said about a mod, so the check, the sync and the share window share one answer
  instead of fetching it three times. The same pack now takes about eighty.

  It also fixes something quieter. The version-change check promised to show you exactly what
  the sync would do, but the two asked ModDB separately — so a mod updated in the minutes
  between them meant the sync installed something the check never showed you. They now read
  the same answer, so what you approve is what you get. **Asking for updates is unaffected**:
  pressing **Update** always asks ModDB afresh, because "what is newest" is the one question a
  remembered answer must not be allowed to answer. Nothing to do.
- **You can say where Cairn keeps its files before it has any.** **0.9.3.** On a fresh
  install Preferences answered *"there is nothing at ~/.cairn to move"*, as though choosing a
  location needed something to have been put in the wrong place first. The button reads
  **Choose…** when there is nothing to move and **Move…** when there is.
- **A mod setting you change after ticking it is the one the pack carries.** **0.9.3.** The
  tick was being read as "carry this value, as it stood when you ticked it": change the
  setting afterwards and every row on screen followed, while the pack went on declaring the
  old one and publishing it. Unticking and reticking was the only way out, and nothing said
  so. Publishing and exporting now read the files again, so this holds whether or not you
  opened the Mod config tab first — and the pack says it has something to publish.
- **Share says what publishing would change.** **0.9.3.** *Since revision 4: 1 mod added, 5 at
  different versions, 3 mod settings changed.* Read against what the site is actually serving,
  because a pack you have been playing for a month has moved in ways nobody remembers. A first
  publish has nothing to compare against and says what the pack contains instead; a site that
  cannot be reached says so, rather than letting that pass for nothing having changed. And
  coming back from a session re-checks, so tuning a mod in game and quitting turns the button
  into **Publish changes** without having to click away from the pack and back.
- **"Has this pack changed?" stops being a question about whitespace.** **0.9.3.** Cairn
  answered it by hashing the published document as text, so anything that altered how the
  document was *written* — a field that stopped being included, values recorded in a
  different order — read as a change to the pack, and publishing to settle it issued a
  revision with nothing in it. It now compares the document's shape. A pack you published
  with an earlier Cairn may say **Publish changes** once: open Share and it will check what
  the site is serving, find your pack unchanged, and settle by itself.
- **Escape closes any of Cairn's dialogs.** **0.9.3.** Preferences, Share, the pack update and
  game version windows did not, and Preferences could only be dismissed from the title bar.
- **Preferences no longer runs off the bottom of the screen.** **0.9.3.** At larger interface
  sizes, and in languages whose text runs longer than the English, the Overview tab could put
  the language picker somewhere unreachable.
- **A mod row too long for the window wraps instead of running off it.** **0.9.2.** A mod with
  a long name quietly lost whatever came last off the right-hand edge.
- **`cairn-server` can say which build it is.** **0.9.2.** `cairn-server version`, `--version`
  and `-v` all answer, and `run` prints it as its first line so `journalctl` has it for a
  server nobody watched start.
- **Your settings stop overwriting each other.** Cairn's preferences file was rewritten whole
  by whichever setting you had just changed, which is why there was never more than one. It
  now keeps every setting it knows about, and every setting it does *not* — so a preference
  written by a newer Cairn survives being opened by an older one.
- **A few sentences that were wrong in English got fixed**, found by writing them out for
  translation: "1 thing you changed differ from the author's" now says *differs*.

## Upgrading

Nothing to do, with one exception.

**If you ran 0.9.0 and a pack's ConfigLib settings never took**, upgrading is not enough on its
own: Cairn wrote itself a note saying you owned those values, and it is still there. Delete this
file and the next launch sets it right —

```
<your pack>/data/cairn-modconfig.json
```

— which on a server is `~/.cairn/packs/<pack>/data/cairn-modconfig.json`. It holds only Cairn's
record of what the pack last asked for, so losing it means the next launch treats the pack's
values as a first word again. Settings you genuinely changed yourself, change back afterwards,
or edit the file and remove just the entry for the mod that was stuck.

If one of those settings shapes the world, the terrain you already have was generated against
the mod's default and stays as it is; the pack's value applies to ground generated from here on.
Whether that is worth starting a world over is a judgement only you can make, and on an
established server it usually is not.