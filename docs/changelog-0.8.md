# What's new in Cairn 0.8.0

Everything that changed since the 0.7 series, in one place. Nothing here needs any action
from you unless it says so.

## Keep Cairn's files on a different drive

Packs, game versions and private runtimes add up quickly — a single game version is around
600 MB, and Cairn keeps as many as your packs need. On a machine with a small system drive
and a large second one, all of that was landing on the wrong disk with no way to say so from
the launcher.

**Preferences → Move…** now asks where you would rather it lived, and puts it there.

It copies everything, checks every file arrived at its full size, points Cairn at the new
location, and only then deletes the original — so the space you were short of is actually
given back. One confirmation covers all of it, and nothing is removed until the new copy has
been verified. If a file cannot be copied, the move stops and your old files are still there,
untouched.

Worth knowing before you start: it will not run while a game or server is up, it needs the
destination to be empty, and it tells you how much it is about to copy and how much room is
free before you agree to anything. Moving back later works the same way.

**If the drive is not there next time, Cairn says so.** An external disk unplugged, or a
network share that is down, used to be indistinguishable from having lost everything — the
launcher would open on an empty list. It now stops and names the path it cannot reach, so you
can plug the disk back in and carry on, rather than wondering what happened to your packs.

## You can check that a download is really Cairn

Every release is now signed and, from this one on, carries **build provenance**: a signed
record from GitHub tying the file you downloaded to the exact source it was built from, which
nobody can forge — not even whoever cuts the release.

If you want to check a download before running it:

```
gh attestation verify cairn-0.8.0-windows-x64.zip --repo cairns-gg/cairn-app
```

Nothing is different if you would rather not. It matters mostly because Cairn installs
software on your machine, and "read the source" is a weak answer if you cannot tell whether
the program you are running came from that source.

## The source is public

Cairn's code is now readable at [github.com/cairns-gg/cairn-app](https://github.com/cairns-gg/cairn-app).

It is **source-available, not open source**: you may read it and use it, and the licence does
not permit redistributing it or publishing your own version. One exception is deliberate —
`cairn-server` may be run commercially, so a hosting provider is not shut out of the one part
written for them.

## Smaller things

- **A pack's server address is checked** before it reaches the game, so a malformed one is
  refused rather than handed over as a command-line option.
- **Mod and game downloads are checked harder.** Filenames from ModDB, the game's version
  manifest and Microsoft's runtime index all go through the same rule about what a filename
  may be, and a download whose checksum is missing is refused rather than trusted.
- **On Windows, the game's installer is only run if Windows vouches for its signature.**
- **Files holding your login are created private** rather than made private a moment later.
- **The Hotkeys tab reads a mod's files with limits**, so an unusual mod cannot make opening
  that tab take a very long time.
- **A pack you import records where it actually came from**, rather than where it was asked
  for, so a link that redirects cannot quietly change what your pack follows.

## Upgrading

Nothing to do. Packs, worlds, hotkeys and installed game versions carry over untouched, and
Cairn keeps its files exactly where it already does unless you move them yourself.
