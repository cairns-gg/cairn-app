# What's new in Cairn 0.7.0

Everything that changed since the 0.6 series, in one place. Nothing here needs any action
from you unless it says so.

## A pack can carry its hotkeys

Twenty mods bring twenty sets of hotkeys, and several of them land on the same key. You find
that out in game, one keypress at a time — and so does every single person who installs your
pack, from scratch, reaching the same answers you already reached.

A pack now has a **Hotkeys** tab, and what you decide there travels with the pack.

It lists every hotkey the pack contains — the mods' and the game's own — read straight out of
the files, so it is all there before the pack has ever been launched. Anything that fires on
the same key as something else is marked, with the other one named, and **Only conflicts**
narrows the list to just those and counts them. The search box takes a mod name, a hotkey's
name, or a key: type `P` and you get everything on P, which is usually the actual question.

Each row has the key it will use and, where you have changed it, the key the mod ships with
beside it — so "is that what it came with or what I picked?" is answerable at a glance. Click
the key, press a new one, and that is the rebind; **Escape** backs out. There are two other
answers to a collision, and often a better one: **Unbind** puts a hotkey on no key at all,
which is the honest resolution when five mods want P and four of them do not need a key; and
**Reset** drops the pack's opinion so the hotkey goes back to whatever its mod ships, now and
in future versions.

Nothing to save. A rebind writes the pack as you make it, the same as adding a mod does.

**Movement and the mouse buttons are held back.** A pack filling in a key for a mod you have
never run is a favour; the same pack quietly moving your jump key is not. Those rows say what
they are and ask to be unlocked first — you can still move them, because sometimes you have
to, but it takes a decision rather than a slip.

## What that means for a pack you install

On the first launch, the pack's hotkeys are put into that pack's controls **only where you
have not set that key yourself**. Anything you have already bound is yours and is left alone —
Cairn does not overwrite a decision you made. The pack's log says what it bound, because a
keyboard that changes without mentioning it is not one you would trust again.

That also means it keeps working as the pack grows: when an update adds a mod, its hotkey
arrives with it rather than only reaching people who had not installed the pack yet.

When you take an update, the author's new answers come with it — unless you had changed that
particular key yourself, in which case yours stays. Rebinding something in a pack you follow
is not undone by the next revision.

Each pack keeps its own controls, as it always has, so none of this touches your other packs
or your plain Vintage Story.

## A game that is running stays running

Start a game, click another pack to look at something, and the launcher used to forget the
game was up: the notification went, **Play** came back, and pressing it would have started a
second copy on the same save.

The pack that is playing is now marked in the sidebar, so "is it already running?" no longer
means selecting each pack in turn. While a game is up, that pack's Play button becomes **Force
quit** — behind a confirmation, because it is a kill rather than a quit, and it is the way out
of a game that has stopped drawing but is still holding the save open. A game you force-quit
is recorded as one you asked to end, so it does not get reported back to you as a crash.

A game that closes while you are looking somewhere else still writes its session back and
still reports a bad exit, against the pack that actually ran it.

## Smaller things

- **Hotkeys the files cannot answer for are said out loud.** A few mods build their hotkey
  names or keys while the game is running rather than writing them down, so there is nothing
  to read. Those either show with no default key or are counted at the top of the tab as ones
  it could not read — rather than quietly leaving you with a list that looks complete.
- **The tab notices mods arriving.** Adding a mod while the Hotkeys tab is open, or opening it
  on a pack that was still downloading, brings the new hotkeys in rather than showing the list
  from before.
- **The game's own hotkeys need its version installed.** Most collisions are between a mod and
  vanilla rather than between two mods, and the vanilla side is read from the installed game —
  so a pack whose version you have not downloaded yet lists its mods' hotkeys only.

## Upgrading

Nothing to do. Packs, worlds, hotkeys and installed game versions carry over untouched.

- A pack you already have carries no hotkeys until you set some, and reads exactly as it did.
- A pack's hotkeys reach your controls on its next Play, and only where you have not already
  bound that key.
