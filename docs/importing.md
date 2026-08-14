# Importing the mods you already have

Nearly everybody arriving at Cairn has already played Vintage Story. They have a Mods folder
with thirty mods in it, and what the launcher used to offer them was an empty pack and a
search box.

This is why that import works the way it does — why it asks ModDB about mods that are
already on disk, why nothing it creates is pinned, and why a mod ModDB will not serve is
named and skipped rather than copied in.

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
