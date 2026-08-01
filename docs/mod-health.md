# Is this mod working?

The question a person actually has, standing in front of a mod they are about to add:
does this work right now, on my game version, or is there something wrong with it that
everyone but me already knows about?

ModDB answers that badly. The information is there — 700 comments on a popular mod — but
reading it means opening a browser, scrolling past three years of support questions, and
guessing which reports still apply.

This is about surfacing that answer next to the Add button. Most of it needs no model at
all; the model earns its place on a narrow part of it.

## What this must not become

Stated first because the pressure to build it is real and the harm is not obvious.

Anything here is a statement Cairn makes about a real person's unpaid work, shown to every
user, with no recourse for the author. The VS modding community is small enough that the
author will hear about it. So:

- **No verdict.** "3 comments since v2.0.0 mention crashes on world load, most recent 4
  days ago" is a fact with a date and a link. "This mod has issues" is a judgement, and it
  is the same sentence whether the evidence is three reports or one person having a bad
  week.
- **No score, no traffic light, no health percentage.** A number gets screenshotted and
  quoted with the evidence stripped off. There is no way to make that fair.
- **No commenter identities.** The API hands over `userid`. There is no reason to pass it
  on, and a summary that names people turns a support thread into a pile-on.
- **No verbatim comment text.** Describe the theme, link to the source. See *What we are
  allowed to do with this*, below.
- **Silence is a valid output.** "28 comments since this release, nothing that reads as a
  problem" is useful. So is showing only a count. A feature that must always have something
  to say will invent something.

## Most of it needs no model

These come from data Cairn already fetches, are never wrong, and need no defending:

| signal | source | what it tells you |
|---|---|---|
| does the newest release target my game version *exactly* | `MatchQuality.Exact` vs `SameMinor` | already computed by `ModDbClient.ResolveAsync` |
| comment volume since the current release | `/api/comments` filtered by `created` | a spike after a release is the strongest cheap signal there is |
| how recent the last comment is | same | a mod with no comments in a year is either stable or abandoned |
| gap between last release and last comment | `lastreleased` + comments | separates those two cases |
| downloads, follows, trendingpoints | `/api/mod` | already in `ModDbMod` |

Build these first. They are the fallback the model layer degrades to, and they carry most
of the value on their own.

## The comment data

`/api/comments/[assetid]` — documented in the vsmoddb README, no auth, returns
`commentid`, `assetid`, `userid`, `text` (HTML), `created`, `lastmodified`. Pass the
numeric `AssetId` that `ModDbMod` already maps.

Volume is not a constraint. Measured on `carryon`, one of the busier mods:

```
last  30d:   28 comments,   7,937 chars
last  90d:  118 comments,  41,463 chars   (~10k tokens)
all time :  704 comments,  back to 2023
```

Four properties of the endpoint, from reading `lib/api/v1/logic.php`:

**It is `order by lastModified DESC`, not `created`.** Taking the first N gives the most
recently *edited* comments, which can be years old. Filter on `created`.

**Deleted comments are already excluded** (`where !deleted`), so moderation is respected
at fetch time — but a cached summary outlives the deletion of a comment that fed it. That
is the one way this feature can end up repeating something a moderator removed, which is
why summaries are re-derived rather than cached indefinitely.

**Comments are editable**, so `lastmodified` belongs in the cache key alongside the
release.

**A non-numeric assetid silently returns the site-wide latest 100.** The path is
`intval()`d, so `/api/comments/carryon` is not an error — it is 100 comments about other
mods. A wrong id produces plausible, entirely unrelated output.

## What we are allowed to do with this

The ModDB terms of use have three sections — mod uploads, mod downloads, and community
conduct rules. There is no developer section, no API terms, no rate-limit clause, and no
restriction on commercial use or derived works. vsmoddb itself is MIT. The endpoint is
documented. Nothing here is prohibited.

But the upload terms license *mods* to Anego Studios, and there is no equivalent clause
for comments. Comment authors have licensed their words to nobody. That is not a
prohibition — it is the ordinary position of any text on the web — but it is the reason
the rules above say describe-and-link rather than quote.

Two operational notes:

- **There is no rate limiting anywhere in the code.** That is not permission, it is an
  honour system. Sweeping thousands of mods from a server should cache hard, back off, and
  send a User-Agent that says what it is and who to contact.
- **Endpoints have been withdrawn before.** `/api/changelogs` now returns HTTP 410:
  *"This information was previously available, but is no longer distributed."* Assume
  `/api/comments` could follow. Losing it must degrade this feature to the deterministic
  signals above, not break anything.

## Where it runs

On cairns.gg, not in the client. The client cannot hold an API key, and per-user
summarisation would pay for the same work once per user. Server-side, the cost scales with
the number of *mods*.

Cache key: `(assetid, current releaseid, max(lastmodified))`. That regenerates on a new
release, on a new comment, and on an edit to an existing one — and not otherwise.

Re-derive on a schedule regardless, so a summary cannot outlive a deletion by more than
the interval.

Scope the input to **comments since the current release**. A crash report from 2023 against
1.18 is noise, and including it is the most likely way to produce something confidently
wrong. It also cuts the busiest mod from 704 comments to a few dozen.

## Output shape

```json
{
  "assetid": 4405,
  "release": "2.0.0-pre.8",
  "commentsSinceRelease": 28,
  "newestComment": "2026-07-31",
  "themes": [
    { "text": "crashes on world load with other carry mods", "mentions": 3,
      "newest": "2026-07-27" }
  ],
  "generated": "2026-07-31T00:00:00Z"
}
```

Themes carry a count and a date so the reader can weigh them, and the client always links
through to the mod's comment page. No overall field, deliberately — there is nowhere for a
verdict to live.

Rendered as one line on the mod row, expandable. Absent entirely when there is nothing to
say.

## Open questions

- Does the author get to see and contest a summary of their own mod? They cannot opt out
  of being discussed, and a wrong summary is worse for them than for anyone else. A
  contact link on the summary is the cheap version.
- Is this free or paid? It costs real money per mod, which argues for paid — but it is
  also the kind of thing that reads worst behind a paywall, since the people harmed by a
  bad summary are not the people paying for it.
- Should the deterministic signals ship on their own first? Probably yes: they are most of
  the value, they need no server, and they would tell us whether the model layer is worth
  building at all.
