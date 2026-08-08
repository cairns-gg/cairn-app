# What's new in Cairn 0.5.1

Everything that changed since the 0.3 series, in one place. Nothing here needs any action
from you unless it says so.

## Downloads are about half the size

Cairn is roughly half what it was — the Windows download goes from 44.6 MB to 18.5, Linux
from 42.5 to 16.9, macOS from 48.8 to 23.5, and the server tool from 32.2 MB to 7.5. It is
still one self-contained file that needs no .NET installed on the machine; there is simply
less of it.

## Add a mod the game has outrun

Small mods stop being rebuilt while the game moves on, and plenty of them still work. ModDB
says nothing about that, so until now Cairn refused them outright — *"no release marked for
game 1.22.6"* — which is no help if you have installed one by hand and played with it for a
week.

Now you can say so. **Add** on a mod with no release for your version asks first, in plain
terms: it may still work, and it may fail to load or damage worlds this pack has already
made. Accept, and it installs.

What Cairn records is not a "yes" but the version it was a yes *about*. So the acceptance
travels with the pack when you share it, sync keeps mentioning it — naming what the mod *is*
marked for, so you can judge it — and if you retarget the pack to a different release series
it asks again, because nobody has tested that combination. From the command line it is
`cairn-cli add <pack> <modid> --accept-unmarked`.

## Run a faster client, built on your machine

Cairn can now build and install [Optimum](https://mods.vintagestory.at/optimum), a
performance-focused fork of the game client. It is distributed as patches that have to be
applied to a decompiled copy of the game, which is well beyond what most people will do by
hand — so Cairn does the whole procedure: fetch, patch, compile, package, install.

It tells you the cost before it starts, because this is unlike anything else Cairn installs:
**15–30 minutes of compiling and 4–6 GB of disk**, and it needs Git on the machine (plus
perl, python3, curl and tar outside Windows). It names whatever is missing, with the command
to install each. You can cancel at any point without affecting your packs or your existing
game installs.

A built client **never runs unless a pack is told to use it**. It reports the version it was
forked from, so it would otherwise be indistinguishable from the real game — instead it is
offered as a choice on a specific pack, and the diagnostics report says loudly when a pack is
running one. If you retarget a pack to a different game version, the choice stops applying
rather than quietly launching a client that pack's mods were never resolved against; move
back and it applies again, so trying another version for a minute does not throw away a
twenty-minute build.

## Choosing what a pack launches

Where there is a real choice of client, packs now show a single button and a sentence saying
what will run, instead of a picker on every pack. The **Storage** view accounts for what
building a client costs — the build tree is several gigabytes and used to appear in no total
anywhere — and it is listed separately with its own Remove, because unlike a downloaded
version it does not come back on its own.

Relatedly: **Clean up will no longer delete a client you built.** It used to sweep one the
moment the last pack using it was retargeted, from a button offering to tidy up.

## macOS: the native client, and windowed mode

On Apple Silicon, Cairn now installs the **native arm64 client** for game versions that
publish one (1.22.3 and later) instead of the Intel build running under Rosetta. Versions you
already have stay as they are — remove and reinstall one to get the native build.

**Windowed mode no longer draws the game into a quarter of its own window.** The game asks
macOS not to treat it as a Retina app, and that request was being dropped by the way Cairn
laid out its install directories. Existing installs are corrected automatically the first
time you run this version; you do not need to redownload anything.

## Linux: the game installed from Flathub

Cairn now finds Vintage Story when it was installed as a **Flatpak**, including on immutable
systems like Bazzite and SteamOS where that is often the only way to install it. It also uses
the .NET runtime the Flatpak brings with it, so on a machine with no system .NET Cairn no
longer reports that the game cannot start while downloading a second copy of a runtime that
was already there.

## The right .NET, for whatever is actually launching

Cairn works out which .NET runtime is needed from the client that will actually start, rather
than from the game version in general. Two installs of one version can genuinely need
different runtimes — a client built for your machine against the stock download, say — and
getting this wrong produced the least helpful message in the launcher: *"1.22.5 needs .NET
10.0.0, which could not be installed"*, moments after being told the version was ready.

## Running a server

New: **`cairn-server`**, a separate download for Linux (x64). Point it at a published pack's
address and it follows that pack, installs the dedicated server and the .NET it needs — on a
machine with nothing installed on it — and runs the server.

- It can run in the foreground for diagnosis, or under **systemd**; it writes the service
  file for you and prints the commands to enable it, as a system service or as your own user.
- `cairn-server update` takes the author's newer revision **when you ask**. A restart never
  moves the mods under a live world.
- `cairn-server command "/whitelist add someone"` talks to a running server, with no `screen`
  session needed.
- Stopping is graceful: the server is asked to save and exit, and given time to do it.

## Smaller things

- **Update checks say what they are doing** while they run, and the answer is remembered for
  ten minutes — pressing the button twice no longer re-asks ModDB about thirty mods to be
  told the same thing. Editing the pack — adding, pinning, retargeting, syncing — makes the
  answer stale immediately.
- **Pinning a mod** now happens in a window where you can see the versions, rather than a
  dropdown on every row, and the pin marker is drawn rather than borrowed from an emoji font.
- **One bad mod entry no longer breaks the whole pack.** Searching for "optimum" and pressing
  Add used to write an entry with no mod id, after which every sync failed with "Pack manifest
  is invalid" and the only way out was editing `pack.json` by hand. Results that are not mods
  no longer offer Add, and a single unusable entry now fails as itself while the rest of the
  pack installs.
- **A game version is checked as soon as you pick it**, rather than when you next press Play.
- **The "not an exact match" warning** now appears only when a change actually causes it,
  instead of on every mod that was already installed that way.
- **The diagnostics report keeps its characters on Windows**, and says which install a pack
  actually runs with.
- **Pressing enter searches again.** Typing a mod name and pressing enter searched; typing a
  different one over it and pressing enter did nothing, which is the moment it most
  obviously should.
- **A mod with no release for your version** no longer has its "no 1.22.x release" label
  overlapping the View and Add buttons; it sits on its own line.

## Upgrading

Nothing to do. Packs, worlds and installed game versions carry over untouched.

- **macOS:** your installed game versions are renamed on first launch so windowed mode scales
  correctly. Packs that had a specific client chosen keep it.
- **Apple Silicon:** existing installs stay as the Intel build until you reinstall them.
- **Servers:** `cairn-server` is a separate download and is not part of the launcher.
