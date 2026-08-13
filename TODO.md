# TODO

Things worth doing, with enough of the reasoning to still make sense in a month. Not a
backlog to be burned down in order — the top of a list is not a priority claim.

## Moving ~/.cairn off the system drive

Asked for by a Windows user out of C: drive space, which is the common shape of it — a
small system SSD beside a large second disk. Not a Windows feature though, and scoping it
that way would mean writing a platform check to withhold something that already works
everywhere: `CAIRN_HOME` moves the root today, on every platform, and `CairnPaths.Root`
re-reads it on every access rather than caching. A Steam Deck user wanting packs on an SD
card has the same problem, and the README already claims Bazzite and SteamOS support.

What is missing is not the indirection. It is a way to set it that survives how people
actually start Cairn: an environment variable set in a shell does not reach a Start-menu
launch, a `.desktop` entry, an `.app` bundle or a `cairn://` activation. That gap is
identical on all three platforms. Windows users are simply the ones with no discoverable
alternative — `ln -s` is folklore on macOS and Linux, and `mklink /J` is not.

### The setting cannot live in settings.json

`SettingsPath` is `Path.Combine(Root, "settings.json")`, so the setting would live inside
the thing it configures. It also could not survive there: `UiScale.Save` serialises every
key it knows and moves the result into place, which is why `LastUpdateCheckPath` is its own
file already.

So: a `home` file at the *default* location, `~/.cairn/home`, holding one absolute path and
nothing else. One mechanism on all three platforms rather than a registry value here and a
plist there, and it leaves a stub where people and support requests already look. Plain
text, not JSON, because it is read before anything else works and has to be repairable by
hand when somebody's data is stranded.

**Resolution order is `CAIRN_HOME` → pointer file → default, and the env var must keep
winning.** `ServerUnit` writes `Environment=CAIRN_HOME=` into systemd units, so a pointer
file that outranked it would silently redirect a running server to somewhere else.

### A pointer at something absent must not be papered over

An external disk unplugged, a network share down, a drive letter that moved. Falling back
to the default starts Cairn with an empty root, which reads as "everything is gone" and
invites re-downloading six hundred megabytes beside data that is fine. Refuse instead, name
the path, and offer to repoint or quit.

This cannot be a property that throws — `Root` is used everywhere. It wants an explicit
check at startup in each front-end, before anything creates a directory. Note that
`Directory.CreateDirectory` is not the check: on Windows `D:\cairn` with no D: fails
cleanly, while on macOS `/Volumes/Gone/cairn` cheerfully creates a directory on the boot
volume, which is the silent-empty-root failure wearing a different hat.

### Moving what is already there is the actual work

- **Copy, never rename.** A move across volumes is the whole point, so `Directory.Move`
  degrades to a copy. `OptimumProvisioner` already hits this and says so; the other fifteen
  `Move` sites stage inside the root, and none of them stage through the system temp
  directory, which was worth checking.
- **`PackLocalState.InstallDirectory` is an absolute path** naming a variant install under
  the old root, and every one of them dangles after a move. Null in the ordinary case, so
  the blast radius is small — but somebody running a pinned Optimum client is exactly the
  person who ran out of C: drive.
- **Verify before deleting, and do not delete automatically.** Copy, check, write the
  pointer, and leave the old tree with an offer to remove it. Tens of gigabytes are not
  worth trusting to one unverified pass.
- **Refuse while a game or server is running.** Open files cannot be moved on Windows, and
  mods break saves.
- **Disable the whole thing when `CAIRN_HOME` is set**, and say why — the pointer would be
  written and then ignored, which is worse than not offering it.

### Wanted, in an order where each part stands alone

1. ~~**Resolution and the pointer file**, plus `cairn-cli home` to show and set it.~~ Done —
   `CairnHome` owns the order, `CairnPaths.Root` is its wiring, and the rules take what they
   read as arguments so they can be tested without a real home directory to stand in.
2. ~~**Migration as a CLI command**~~ Done — `cairn-cli home move`, over `HomeMigration` in
   Core so the launcher can drive the same engine. Copies rather than renames, keeps links
   as links, verifies every file arrived at its full length, moves each pack's recorded
   install path with it, and repoints last so a failure anywhere leaves the old root live.
   Deletes nothing: what to do with the old copy is a decision made after this one is known
   to have worked.
3. **A Preferences affordance** wrapping it, next to `CairnHome` and the per-directory
   sizes already displayed there — the numbers that make somebody want this are on that
   screen already. `MovePlan` answers what it would cost and `MoveProgress` reports as it
   goes, so what is left is a folder picker, a progress bar and the two refusals a window
   can show better than a terminal.

   **The old copy needs an answer there that the CLI ducks.** It leaves the old root in
   place and says so, which is right for a tool run by somebody watching it. A launcher that
   moves 40 GB and leaves 40 GB behind has not solved the problem the user came with — they
   are out of disk space. Offering to delete it after the new root has been used once, from
   a screen that already shows what everything costs, is probably the shape. Deleting it in
   the same breath as the copy is not.

**The launcher does not run the preflight yet, and must before 3 ships.** `cairn-cli`
refuses to run on a pointer at a directory that is not there, and exempts `home` itself so
the repair tool is not gated on the thing it repairs. `Cairn.App` does neither: it would
start on the fallback root and look empty, which is the failure this was all written to
avoid. It is not reachable today — only `cairn-cli` writes a pointer, and it is not shipped
in releases — so this is a gap and not a bug, but it stops being that the moment a GUI
button can write one. Refusing needs somewhere to say so, which is why it waits for the UI
rather than going in blind.

## Taking over an imported pack

An imported pack follows its author, and that now closes off nearly everything: it cannot
be published, and it cannot be exported. Both are right on their own — publishing would
re-issue somebody else's curation under your name, and a `.cairn` file carries no author,
so handing one over launders the pack into an unowned copy.

What is missing is the way out. Someone who imports a pack, then swaps half its mods and
adds three of their own, is holding something that is theirs in every sense the word means
— and there is no action that says so. They are stuck with a pack they can neither share
nor hand to a friend.

The state machine already has the slot. `PackLink.Following` is documented as "cleared by
Take over, which keeps `Url` so the pack can still say what it diverged from", and
`ShareStatus.Following` says "the button is not offered at all — Take over comes first".
Nothing implements it.

Wanted:

- **A Take over action** on a followed pack. It clears `Following`, keeps `Url` as the
  record of what this came from, and switches `Role` to Author. Publishing then mints a
  new pack at the taker's own URL — never a revision of the original.
- **A confirmation that is honest about the trade**: after taking over, the author's
  updates stop arriving. That is the actual cost and the only reason to hesitate.
- Probably **surface the ancestry** afterwards — "forked from cairns.gg/dizzyd/anego" —
  both as courtesy to the original author and because it is genuinely useful to know.

Worth deciding before building: whether taking over is reversible (going back to following
means discarding local changes, so it is really "re-import"), and whether it should offer
to rename the pack, since keeping the original's name and id is how two different packs end
up looking like the same one.

An earlier note from the same conversation, still unbuilt: an imported pack should also
resist casual mod edits by default, with the same explicit action to signal you are taking
it on rather than following it. Take over is the one gesture both of these want.
