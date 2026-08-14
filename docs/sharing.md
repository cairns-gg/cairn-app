# Sharing packs on cairns.gg

Cairn already makes a pack shareable — `PackBundle` is one JSON file carrying the
manifest and optionally the lock, and `cairn-cli import` already accepts an `https://`
URL. What's missing is somewhere to put it that doesn't rot.

That is the whole of v1: **a stable URL for a pack bundle, and a page a human can read
before importing it.** Everything else here exists to serve that.

## Non-goals

Stated up front because each of these is a plausible-sounding feature that would sink v1:

- **No hosting of mod zips.** Packs reference ModDB by `modid` and the client downloads
  from there. This is the legal and bandwidth firewall — we redistribute nobody's mod,
  and a pack is a few KB of JSON rather than 300 MB of archives.
- **No pack version history.** A URL serves the current bundle. Recipients who want
  reproducibility get it from the lockfile, not from server-side revisions.
- **No comments, ratings, or downloads-per-day leaderboards.** These are a moderation
  burden with no bearing on whether a pack installs.
- **No teams or shared editing.** One pack, one owner. Revisit when there is evidence
  that real packs have two maintainers.
- **No account required to import.** Reading and importing are anonymous, always.

## Identity

The account is **ours**, keyed on a verified email, signed in by magic link. Discord and
GitHub attach as optional linked identities and may also be used to sign in.

Discord is deliberately *not* the primary provider. It's where the VS community lives and
it's tempting for that reason, but making it the identity provider means inheriting its
account lifecycle: a ban, a deletion, or a blocked network takes your ability to publish
with it, and a meaningful slice of the VS audience avoids Discord on purpose. Linking
Discord is required only to receive Discord notifications, which needs no justification.

Magic links have one real failure mode — spam foldering — and a publish flow that
silently fails is worse than one that asks for a click. Use a real sender (Postmark or
Resend), set SPF and DKIM on `cairns.gg` before launch, and make "resend" prominent
rather than buried.

**A username is claimed at first sign-in**, because it is the URL namespace and has to
exist before the first publish lands. Same character rules as `PackId` — letters, digits,
`-` and `_` — since these become path segments and we already refuse `../../etc` on pack
ids for exactly this reason.

## URLs

```
cairns.gg/<user>/<pack>          human-readable page
cairns.gg/<user>/<pack>.json     the bundle, exactly what PackBundle.Parse eats
```

An explicit `.json` suffix rather than content negotiation alone: it survives being
pasted into a browser, a curl, and a Discord embed without behaving differently in each.
`cairn-cli import` accepts either form and appends the suffix itself, so what a user
copies off the page always works.

The slug defaults to the pack's `id` and is editable at first publish. It is **immutable
afterwards** — these URLs end up committed in `pack.json` files and pasted into Discord,
so renaming would break them silently. Renaming means unshare and re-share, and says so.

## Versions: followed and pinned

There is no "track newest" mode for what is installed. Every mod on disk sits at an exact
version with a recorded SHA-256, and `sync` installs what the lock says — it reaches for
ModDB only when it has no choice: a mod never installed, a pin that moved, or a retarget.
Versions cannot drift under a working pack.

What varies is only whether a mod is *offered* a move:

| state | manifest | Update offers it? |
|---|---|---|
| **followed** (default) | no `version` | yes |
| **pinned** | `version: "1.2.0"` | never — a pin means stay put |

Both are exact. The distinction is intent, not installation.

Two consequences for sharing:

**A published pack always carries the lock.** There is no "include lockfile" choice at
publish, unlike `export --no-lock`, which stays as a way to hand someone a deliberate
starting point. Publishing makes a stronger claim than exporting does — the recipient
should get what the author tested, not something merely similar.

**Import reproduces without freezing.** The author's manifest travels unchanged, so their
pins arrive as pins, and everything else arrives followed at their exact version. The lock
is what reproduces the set; `PackSyncer` applies a lock entry whenever the manifest asks
for no particular version, and verifies the download against the author's hash regardless.

This last point was a real bug until recently: `PackBundle.PinToLock()` wrote the lock's
versions into the manifest for *every* mod on import, which pinned them all — and a pinned
mod is never offered an update. Every shared pack arrived frozen for good, which is the
exact opposite of what this site is for. Reproduction never needed it.

The standing limitation: authors delete releases from ModDB, and a pinned version that no
longer exists cannot be installed. Sync fails loudly rather than substituting something
else, which is right, but the pack stays broken until someone edits it. That is the cost
of referencing rather than hosting, and it is worth paying.

## Linked packs

A pack imported from cairns.gg **follows its author**. It is not frozen — when the author
publishes a new revision, mods are added, removed and moved — but the set is theirs, and
changing it locally is an explicit act rather than a side effect of clicking Add.

Without this, following a server pack quietly stops working. "Check for updates" asks
ModDB for the newest compatible release of each mod, so a player on a linked pack would be
offered `glassview 1.3.1` when the admin published and tested `1.3.0`, accept it, and
diverge from the set the pack exists to reproduce — while the pack still looks correct.

### What Update means

| | update source | Update means |
|---|---|---|
| **linked** — imported from cairns.gg | the author's published revisions | the author shipped a new set; take all of it |
| **unlinked** — imported from a file, or your own | ModDB, per mod | there is a newer release of this mod |

A linked pack applies a revision **as a set**, because that is the unit the author tested;
offering its mods one at a time would rebuild the divergence this exists to prevent.

### The link lives outside the manifest

`packs/<id>/cairns.json`, beside the lock. One file describes a pack's whole relationship
to the site, from either end — the alternative was two files saying the same thing in
opposite directions, and one place to look when asking "where did this pack come from, and
where does it go".

A pack you follow:

```json
{
  "role":     "follower",
  "url":      "https://cairns.gg/dizzyd/anego",
  "revision": 4,
  "following": true
}
```

A pack you published:

```json
{
  "role":     "author",
  "url":      "https://cairns.gg/dizzyd/anego",
  "revision": 4,
  "published": {
    "fingerprint": "sha256:…",
    "visibility":  "unlisted",
    "connect":     "stripped"
  }
}
```

Deliberately **not** in `pack.json`. That file is shareable intent, so a link stored there
would travel: re-exporting a pack you imported would hand the next person a manifest
pointing at somebody else's canonical URL. Where you got your copy is a property of *your
copy*.

The fingerprint is a hash of **what was published**, not of the local pack. If a pack was
published with its server address stripped, the local manifest differs from the published
document permanently, and comparing the two directly would report unpublished changes
forever. So the publish options are stored with the hash and reapplied before comparing.

### Who owns what

Blocking every local change would be wrong — a public pack ships with `connect` stripped,
so the player often has to set the server address themselves, and that cannot require
taking the pack over.

| | owner | while followed |
|---|---|---|
| mods, pins, game version | the author | locked; changing any of it detaches |
| display name | you | freely editable |
| server address (`connect`) | you | freely editable |

Retargeting the game version is deliberately on the author's side of the line. It
invalidates the lock for every mod at once, which is the largest divergence available and
the least recoverable.

### A retarget arriving from upstream is not just another revision

Because the game version is the author's, a follower can be handed one, and it must not
ride in on a normal update. It is worth being precise about what is actually at risk,
because the game is more forgiving than it looks:

| event | when | reversible | what the game does |
|---|---|---|---|
| version mismatch | any downgrade, patch included | yes — the world still loads | warns: *"Was opened in a newer version of the game, might not load correctly"* |
| file format upgrade | rare — `GameVersion.DatabaseVersion` is still `2` and has not moved across 1.20 → 1.22 | **no** | prompts *"This world uses an old file format that needs upgrading … suggested to first back up your savegame"* |

So nothing is moved and nothing needs moving: `packs/<id>/data/Saves` is not versioned, and
a retarget changes the mod set and a JSON field while the worlds sit still. The retarget
itself stays reversible — the point of no return is the first launch afterwards, and only
if that launch performs a format upgrade.

An incoming game-version change is therefore presented with the same machinery as the local
retarget: the per-mod preview (`keeps` / `updates` / `untested` / `breaks` / `pin fails`,
worst first) and an explicit accept. Declining leaves the pack on the revision it is on,
still followed, and says the author has moved on without it — a state the pack list needs
to show, because a follower stuck a version behind is otherwise indistinguishable from one
that is current.

### Downgrades across a minor, and backups

Two rules, both keyed on the same boundary — a change that crosses the minor (`1.22.x` →
`1.21.x`), where a format difference is plausible. Movement within a minor is left alone;
people do move back and forth on patch levels, and no format bump has ever ridden on one.

**A cross-minor downgrade is refused when the pack has worlds.** Not because the game
cannot open them — it can, with a warning — but because a world that a newer build has
already migrated is being handed to an older one, and the failure is quiet. A pack with an
empty `Saves/` is unaffected: building a 1.21 pack from a 1.22 one risks nothing, and
blocking it would be gratuitous. Cairn can already enumerate worlds (`PackData`,
`PackContents`).

**A cross-minor upgrade backs up the worlds first.** Vintage Story already provides the
destination — `GamePaths.BackupSaves` is `<DataPath>/BackupSaves`, created eagerly, and
each pack has its own data path, so every pack already has one. Copying the `.vcdbs` files
there is a few lines rather than a subsystem, and it lands somewhere the user recognises.
`PackContents.WorldsBytes` already measures the cost, so the dialog can state it, and
Preferences → Storage → Clean up is where it gets reclaimed.

Only on a cross-minor change: worlds run to gigabytes, and copying them on every
`1.22.5 → 1.22.6` would spend that disk routinely against a risk that is not there.

### Taking over

One action — **Take over** in Settings — sets `following: false` and hands you the pack.
The `url` and `revision` are kept rather than cleared, so the pack can still say what it
diverged from, and **Revert to the author's set** remains available (discarding local
changes and re-following).

The `role` stays `follower`: the pack came from someone else and always did. Publishing it
afterwards is a separate act that rewrites the file as an `author` record pointing at a URL
of your own — which is also when the Share button reappears.

Attempting a locked edit while followed prompts once with what it will do, and taking over
is the confirm. Nothing detaches silently.

### Enforcement, and its limit

This is enforced in `PackStore`, not in the launcher. Both front-ends mutate packs only
through it — the same invariant that stops a hostile `id` on import — so a greyed-out
button in the UI would be no protection at all against `cairn-cli add`.

But `pack.json` is documented as hand-editable and meant to be committed, and no guard in
`PackStore` reaches a text editor. So sync must **detect** a followed pack whose manifest
no longer matches its source revision and ask, rather than silently overwriting:

```
$ cairn-cli sync anego
  ! this pack follows cairns.gg/dizzyd/anego but its mods have been edited locally
    keep your changes and stop following   cairn-cli takeover anego
    discard them and re-follow             cairn-cli sync anego --revert
```

Refusing to guess is the point: one branch throws away the author's set, the other throws
away the user's edits, and neither is safe to pick on their behalf.

### When the source goes away

An unshared pack tombstones (HTTP 410). A linked pack pointing at one keeps working — the
lock is local and the mods are already downloaded — and is offered a take-over, since
there is no longer an author to follow.

## Publishing

### Where the button lives

Opposite **Play**, on the pack's own row, above the tabs — not in the Settings tab beside
Export, where nothing would ever find it. Discoverability is the whole argument: a sharing
site nobody notices they can publish to is a sharing site with no packs on it.

It is deliberately **not** styled as an accent button. Play is what you do every session
and Share is roughly once per pack; giving them equal weight would make the row read as two
equal choices and slow down the thing people opened the app for.

```
Anego Server
game 1.22.5 · 6 mods
anego.example.com:42420
cairns.gg/dizzyd/anego                              [Copy]     <- only once shared
───────────────────────────────────────────────────────────
 ▶ Play                                        [ Share… ]
───────────────────────────────────────────────────────────
 Mods │ Add mods │ Settings │ Log
```

The published URL sits in the header beside the server line rather than on the button. It
is persistent state about the pack and the thing people copy repeatedly, and keeping it
there leaves the button free to be an action rather than a status readout.

Export stays in Settings. It is the offline sibling, and it is not what this button does.

### One button, one window, and the label carries the state

| pack state | label | styling |
|---|---|---|
| never shared | `Share…` | secondary |
| shared, nothing pending | `Shared` | secondary |
| shared, local changes since publishing | `Publish changes` | accent — something is outstanding |
| following someone else's pack | hidden | — |

Hidden while following, because publishing a pack you follow is republishing someone else's
curation under your own name. That needs **Take over** first, after which the pack is yours
and the button appears normally. The header says *following dizzyd* in its place.

### First time, per machine

1. **Share…**
2. Not signed in, so the app runs a **device-code flow**: it shows a short code, opens
   `cairns.gg/link` in the system browser, and polls. The user signs in there and
   approves the code. The app stores the resulting token in `~/.cairn/auth.json` with
   `0600` permissions.

   This is the pattern `gh` uses, and it's chosen over an embedded browser (no webview
   dependency in an Avalonia app, no credential handling in-process) and over
   copy-pasting a token (which people paste into the wrong field). It also works
   unchanged for headless `cairn-cli publish`.
3. Claim a username, if this is a new account.

### The window

Sign-in folds into the same window rather than arriving as a separate dialog first — one
window opened, one window closed, whatever state the account is in:

```
┌ Share "Anego Server" ──────────────────────────┐
│  Sharing a pack needs a cairns.gg account.     │
│                                                │
│           Your code:  WDJB-MJHT                │
│                                                │
│  A browser has opened at cairns.gg/link —      │
│  enter the code there to continue.             │
│                        Waiting…     [ Cancel ] │
└────────────────────────────────────────────────┘
```

Then, and on every share after that. Mostly disclosure rather than configuration:

```
┌ Share "Anego Server" ──────────────────────────┐
│  Signed in as dizzyd               [Sign out]  │
│                                                │
│  URL         cairns.gg/dizzyd/[ anego       ]  │
│  Visibility  ( ) Public — listed in browse     │
│              (•) Unlisted — anyone with link   │
│                                                │
│  Publishing 6 mods at these exact versions     │
│  glassview 1.3.0 · unchisel 1.2.0 (pinned) · … │
│                                                │
│  ! Server address anego.example.com:42420      │
│    (•) Strip   ( ) Include                     │
│  ! olla 1.1.0 is not on ModDB — recipients     │
│    cannot install it                           │
│                                                │
│                     [ Cancel ]   [ Publish ]   │
└────────────────────────────────────────────────┘
```

Once published, the same window becomes the management view: the URL with Copy and Open,
**Publish changes** when the fingerprint differs, and **Unshare**.

Its own window rather than an inline panel, consistent with `ConfirmWindow` and
`VersionChangeWindow` — both already exist for this same shape of "read this before it
happens".

### The three checks that must run before upload

Each of these produces a pack that works for the author and is broken for everyone else,
which is the worst failure mode a sharing site has.

**`connect` is a privacy leak.** `PackManifest.Connect` carries a real host and port.
Publishing publicly publishes your server address. Surface it every time; default to
**strip for public, include for unlisted** — an unlisted pack handed to your own players
is precisely when you want the connect field, and a public pack almost never is.

**Mods that aren't on ModDB cannot be reproduced.** A locally built zip resolves fine on
the author's machine and is a dead entry on the recipient's. Cross-check every `modid`
against ModDB at publish time and list the ones that don't resolve. Do not block on it —
a pack may legitimately reference something published later — but the author must see it.

**A stale lockfile publishes a lie.** If the manifest names mods the lock doesn't cover,
or the lock's `gameVersion` disagrees with the manifest's, offer to sync and refuse
until it's clean. Including the lock is the entire reproducibility claim; shipping a
partial one is worse than shipping none.

### Updating and unsharing

After the first publish the button becomes **Update shared pack**, and it previews what
moves before committing — the same shape as the existing game-version change dialog:

```
glassview   1.3.0 → 1.3.1
unchisel    added
keylock     removed
```

**Unshare** removes the listing and serves a **tombstone** at the URL rather than a 404,
because these links live in Discord scrollback and committed `pack.json` files
indefinitely. The tombstone says the pack was withdrawn and by whom, and returns HTTP 410
so the client can say something better than "not found".

## The published document

The bytes served at `.json` are a `PackBundle` with a server-supplied envelope:

```json
{
  "formatVersion": 1,
  "pack":     { "id": "anego", "name": "Anego Server", "gameVersion": "1.22.5", "mods": [...] },
  "lock":     { "gameVersion": "1.22.5", "mods": [...] },
  "publishedBy":  "dizzyd",
  "publishedAt":  "2026-07-31T00:00:00Z",
  "canonicalUrl": "https://cairns.gg/dizzyd/anego"
}
```

`System.Text.Json` ignores unknown members by default, so **already-shipped clients parse
this correctly** and simply don't see the envelope. That property only holds if the server
never bumps `formatVersion` for additive fields — `PackBundle.Parse` rejects anything
newer than `CurrentFormat` outright. Additive envelope fields are not a format change.

The server validates the same way the client does before accepting an upload: bad
`gameVersion`, duplicate `modid`, or a malformed `id` is rejected at the door, so the
site never serves a bundle that `PackBundle.Parse` would throw on.

## Importing

The pack page carries an **Import into Cairn** button backed by a `cairn://` URL scheme
handler registered by the desktop app — one click opens Cairn with the import dialog
primed. Below it, always, the copyable fallback:

```
cairn-cli import cairns.gg/dizzyd/anego
```

The fallback matters twice over: it works for people who don't have Cairn installed yet,
and that line is the install pitch. Someone arriving from a Discord link should be able
to tell what this is and what to do about it without an account.

## API

```
POST   /api/auth/device                  start device flow -> code, verification URL
POST   /api/auth/device/token            poll -> token when approved
GET    /api/me                           username, plan, linked identities
POST   /api/packs                        publish; body is a PackBundle
PUT    /api/packs/<user>/<pack>          update
DELETE /api/packs/<user>/<pack>          unshare -> tombstone
GET    /api/packs/<user>/<pack>          the published document
GET    /api/search?q=                    public packs only
```

Everything under `/api/packs` that mutates requires a bearer token; the two `GET`s are
anonymous and cacheable.

## Open questions

- Does a public pack need review before it's listed, or is report-and-remove enough?
  Report-and-remove for v1; the abuse surface is a name, a description, and a list of
  modids.
- How does a linked pack learn there is a new revision — polled on launch, or pushed?
  Polling `GET /api/packs/<user>/<pack>` for a revision number is a few bytes and needs no
  infrastructure, but it only notices while Cairn is open. (The question of *whether*
  followers track the author is settled: that is what a linked pack is.)
- Does a public pack need review before it's listed, or is report-and-remove enough?
  Report-and-remove for v1; the abuse surface is a name, a description, and a list of
  modids.
- Should the pack page show which revision a visitor already has? It would need the client
  to say, which means sending something about a local install to the server. Probably not
  worth what it costs.

## Publishing the same thing twice


A revision that differs from its predecessor in nothing but its number tells every follower
there is an update and then has none for them, so Publish is refused when nothing has
changed — in the Share window, where the button dims and says which revision it matches,
and in `cairn-cli publish`.

"Changed" is not only the bytes. Visibility and whether the server address is included are
part of what was published, so flipping a pack from unlisted to public is a real change
with nothing to show for it in the document. That is also why the window still opens on an
unchanged pack: those choices are the reason to come back to one. `PublishRecord.WouldChange`
is the whole rule, and both front-ends ask it.

**The address is fixed once published.** On cairns the URL *is* the pack, so publishing the
same one under a different slug does not move it — it creates a second pack and leaves the
first live under the same name, which is how you end up with two identical-looking packs
and no idea which is which. The Share window makes the field read-only after the first
publish; `cairn-cli publish --slug` refuses and points at `unpublish`.

**Withdrawing is not deleting, and it is not permanent.** `cairn-cli unpublish` takes the
pack down; the row survives on the site and the URL answers 410 with a tombstone rather
than 404, because these links live in chat scrollback and committed `pack.json` files
indefinitely. Publishing again revives the pack at the same address — that is what
withdrawing means for an author, as against an administrator withdrawing one, which the
server refuses to let a republish undo and says so.

Coming back has to survive the unchanged-check above, which would otherwise refuse the one
publish that matters: the pack is down, and republishing it byte-for-byte is exactly how it
returns. So a withdrawal clears the local publish record and keeps the URL, and the pack
reads as **Withdrawn** rather than as one never shared — the launcher says the address is
still yours and offers **Publish again**. The slug is editable once more, which is also how
a pack gets renamed: unshare, then re-share under the new name.

**A withdrawal made on the site never reaches your machine**, and that is the case the
refusal got wrong for longer. Nothing pushes to a launcher, and share state is a local
projection on purpose — asking the server whether a pack has changed, on every pack, to
draw a button would be a great deal of network for a question that is almost always "no".
So the belief is checked at the one moment it is about to block somebody: publishing a pack
the machine thinks is unchanged first asks whether it is still being served. A 410 there
clears the record and the publish goes through. Anything else — including a server that
cannot be reached — leaves the refusal standing, because not knowing is not the same as
knowing it is gone, and inventing a withdrawal would throw away the record on a flaky
connection.

An **unlisted** pack is marked as such beside its URL, and on its page on the site. The two
are indistinguishable from outside, and which one a pack is decides whether passing the
link around is sharing it or publishing it.

That dialog, and not the scheme, is what makes a link from a stranger safe to click: the
answer to "this could be anything" is to say plainly what it turned out to be. An address
pasted into the import box gets the same treatment, since a URL from a chat message tells
you no more about its contents than one on a page. Text or a file you are holding imports
directly.

A name already in use is caught on the form rather than after agreeing — it is the one
thing on that dialog that was fixable, and finding out afterwards means the dialog is gone
and an error is in its place.

The link reaches the app two ways, and both are wired: macOS hands a *running* instance the
URL through an activation event, while Windows and Linux launch the handler afresh with it
in `argv`. Handling either alone leaves half the platforms dead.
