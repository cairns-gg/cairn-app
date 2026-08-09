# What's new in Cairn 0.6.0

Everything that changed since the 0.5 series, in one place. Nothing here needs any action
from you unless it says so.

## Mods no longer load twice

If a mod was already installed in plain Vintage Story and you put the same mod in a pack, the
game loaded **both copies** — the pack's and the one in your own Mods folder. That is the
bug behind "why do I have two of Olla", and it applied to every pack.

The cause was three steps apart. A new pack starts from a copy of your own game settings, so
your keybinds and graphics carry over; that file records where the game looks for mods, as
full paths, written the first time Vintage Story ran; and the setting Cairn uses to point the
game at a pack's mods *adds* to that list rather than replacing it. So every pack quietly
searched your personal Mods folder as well as its own.

Packs now load their own mods and nothing else. It is fixed for existing packs too, on their
next Play — and if a pack was loading something it should not have been, the pack's log says
which folder it has stopped reading. **Your own Vintage Story is untouched**: the mods in it
still load exactly as they always have when you launch the game normally.

If a pack has been getting a mod from your own folder rather than from its own list, that mod
will now be missing from it. Add it to the pack and it comes back — or bring the whole folder
in at once, which is the next item.

## Make a pack from the mods you already have

Most people arrive at Cairn having already played Vintage Story, with a Mods folder holding
thirty mods. Until now the launcher offered them an empty pack and a search box.

**Import… → From your Vintage Story install** reads that folder and builds a pack from it.
It lists every mod it finds straight away and then says what will become of each one:

- mods that go in, **at the versions you are running** — not the newest ones, so the pack is
  what you have been playing;
- a mod ModDB no longer publishes, or never did, named and left out, because a pack is a list
  of mods anyone can install;
- a mod switched off in Vintage Story, left off, since it is not part of what you play;
- a mod with no release for your game version, and what will be installed instead.

Nothing is pinned. The versions you are running are what gets installed, and **Update** works
on them afterwards exactly as it does for any other pack. The pack is built for the game
version your install is, and you are not asked to choose one.

Cairn only ever *reads* your Mods folder. Nothing is moved, copied or deleted, and plain
Vintage Story goes on working as before.

From the command line: `cairn-cli import-install "My mods"`, with `--dry-run` to see what it
would take without creating anything.

## Import asks where a pack is coming from

The **Import…** button used to be one box that took either a link or a pasted pack and worked
out which. It now asks, with the three ways in named: your Vintage Story install, a link, or
pasted text. Links are shown for approval before anything is taken, exactly as they were.

## Smaller things

- **New packs start from your settings again.** A pack made in 0.5 quietly began with bare
  defaults instead of your keybinds and graphics, because of the same change that gave each
  pack its own worlds.
- **Making a pack no longer signs you in as somebody older.** The copied settings brought a
  copy of your login with them, and being the newest one on the machine it won — which could
  put every other pack back on a session you had already replaced.

## Upgrading

Nothing to do. Packs, worlds and installed game versions carry over untouched.

- The mod-loading fix is applied to each pack the next time you press Play.
- A pack that was relying on a mod from your own Mods folder will be missing it until you add
  it to the pack.
