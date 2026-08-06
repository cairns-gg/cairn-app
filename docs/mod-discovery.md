# What's new that I might want?

The behaviour this replaces is concrete: opening ModDB's *All mods* page, sorting by
recent, and scrolling until you reach something you have already seen — then working out,
per row, whether it runs on your game version and whether you already have it.

Every part of that is work a launcher is better placed to do. Cairn knows which packs you
have, which version each targets, and which mods are already in them. ModDB's website
knows none of it, and cannot.

## What this must not become

The same line `mod-health.md` draws, for the same reason: anything here is Cairn making a
statement about a real person's unpaid work, shown to every user, with no recourse for the
author. Ranking is a statement.

- **No recommendations Cairn invented.** Sorting by *date* is a fact. Sorting by "mods like
  the ones you use" is an opinion about which authors deserve attention, computed from tags
  nobody wrote for that purpose.
- **Popularity numbers only as ModDB's own.** `downloads`, `follows` and `trendingpoints`
  are safe to show because they are the source's figures, attributed. A blended "relevance
  score" of Cairn's own devising is not, and it is the same number whether it is built from
  evidence or from a weighting somebody picked on a Tuesday.
- **No hiding the small.** The obvious way to thin the feed is to drop mods with few
  downloads. That is a new-author filter with a different name, and it makes the feed worse
  at the one thing it is for.
- **No engagement pressure.** A badge that says how many new mods exist is information. A
  daily alert about it is a habit being built for somebody else's benefit.

## What the API gives, measured

Measured 2026-08-06 against `https://mods.vintagestory.at/api/mods`:

| | |
|---|---|
| one request | **7,941 mods, 3.4 MB, ~47 seconds** |
| `lastreleased` | present on **100%** of entries, to the second |
| `downloads`, `follows`, `comments`, `trendingpoints` | ranking data, free |
| `tags`, `type`, `side`, `summary`, `logo`, `author` | rows and filtering, free |
| `gameversions` | **absent** — per-mod detail only |

So freshness and popularity for the whole catalogue cost one cached request. Compatibility
does not, and that shapes everything below.

## The volume problem

The reason a raw "what's new" feed does not work, and the number to design against:

| window | mods released or updated |
|---|---|
| last 24h | **89** |
| last 3 days | 222 |
| last 7 days | 496 |
| last 30 days | 1,325 |

Filtering by game version barely helps. In a sample of the 30 most recent releases, **all
30 already supported the newest game version** — people update mods for the version people
are playing. The filter earns its place for a pack on an older version, and does almost
nothing for a pack on the current one. Anyone expecting it to thin the feed will be
disappointed; say so rather than shipping it as the answer.

What *does* split the feed is the kind of change. In the same 30-mod sample, **11 were
brand-new mods and 19 were updates to existing ones**.

## Diff the snapshots

The classification looks like it needs a detail call per candidate — 89 a day — and it does
not. Keep yesterday's index and compare:

- a `modid` **absent before, present now** → a brand-new mod
- a `modid` whose **`lastreleased` moved** → an update
- anything else → unchanged

Zero extra requests. The snapshot is already worth keeping for search, and the diff falls
out of having two of them.

This also removes the reason to poll per mod, which matters more here than the request
count suggests: ModDB publishes no rate limit and sends no headers about one, so the only
safe reading is that somebody is paying for the bandwidth. See `tools/moddb-audit.cs`,
which is deliberately serial at one request per second for the same reason.

A pleasant side effect: with a local snapshot, **search stops going to the network per
keystroke**. Discovery done this way can plausibly *reduce* Cairn's load on ModDB rather
than add to it.

## Three signals, not one

The firehose is three things mixed together, with very different volumes and worth:

| signal | volume | verdict |
|---|---|---|
| updates to mods **in your pack** | a handful | **already built** — `update --check` and *Check for mod updates* |
| **brand-new** mods | ~33/day | this is the discovery feature |
| updates to mods you do not have | ~56/day | most of the noise, least of the value |

That third row is the important one. It is over half the feed, and you had already decided
against those mods — a bugfix does not change that. Dropping it is free once the diff
exists, and it is the single biggest improvement over refreshing the website, where the
three are inseparable.

The first row is a warning, not a feature request: it exists, and building a second thing
that answers it would give two places to check and two chances to disagree.

## Shape

A view on a pack, not a section of the app. The pack is what supplies the game version,
the already-have list, and the reason to care.

- **A watermark, not a window.** "Since you last looked", stored per pack, moved when the
  view is opened — not "the last 7 days", which is a different question and answers it
  worse every time you check twice in a day.
- **Already-in-this-pack marked**, so the same rows stop being re-evaluated.
- **Add in one click**, into the pack being viewed. The current path — read the modid, go
  to search, type it — is most of the friction.
- **Newest first by default**, with ModDB's own trending and download figures shown but not
  used to reorder anything by default.

Where it runs: entirely in the client. Unlike `mod-health.md` there is no model, no API key
and nothing per-user to amortise — it is one public JSON document and a diff, so a server
would add a dependency and buy nothing.

Storage: snapshots under `CairnPaths.CacheRoot`, which is already the directory documented
as safe to delete because everything in it comes back. Two snapshots are enough for the
diff; keeping more is what the deferred idea below would need.

Refresh: daily, in the background, never blocking a view. 47 seconds is far too slow to sit
in front of, and 3.4 MB times every user times the frequency is the only real cost this
feature has. Daily is neighbourly. Hourly is not.

## Signalling

A count on the pack — **"12 new since Tuesday"** — and nothing more.

At ~33 new mods a day, an alert is a daily interruption that gets muted within a week, and
a muted channel is worse than no channel, because the pack-update alert that is genuinely
worth reading lives in the same one.

The existing update check is the precedent worth copying rather than the notification
behaviour: interval-gated, backed by a timestamp in `local.json`, silent when there is
nothing to say. See `PackUpdateCheck`.

## Deferred, deliberately

**"Now works with your game version."** A mod that has just gained support for the version
your pack targets is arguably the most interesting row that could appear — it was not an
option yesterday and is today. Detecting it means diffing *version support* across
snapshots, which needs history the first version will not have. The snapshot store makes
room for it; build it once the store has proved itself.

**Tag similarity.** Computable locally, tempting, and the point at which Cairn starts
deciding whose mod is worth your attention. A chronological list with a watermark solves
most of the same problem without making that claim.

## Open questions

- **Is the watermark per pack or global?** Per pack is right for "what can I add to this",
  but somebody with five packs on the same game version then sees the same new mod five
  times and dismisses it five times. A global seen-set with per-pack filtering may be the
  better model, and it is a harder thing to change later than to decide now.
- **What happens on the first run?** There is no previous snapshot, so nothing can be
  classified as new. Showing the last 7 days by `lastreleased` is the obvious fallback, at
  the cost of the new-versus-updated split until the second snapshot lands.
- **Does the 3.4 MB daily fetch want to be opt-in?** It is not large by download standards
  and it is one request, but it is bandwidth somebody else pays for, spent on behalf of a
  user who may never open the view.
- **Is 33 a day genuinely browsable?** It is roughly a screen. That seems fine daily and
  poor after a fortnight away, which argues the watermark needs a ceiling and an honest
  "and 240 older" rather than an endless list.
