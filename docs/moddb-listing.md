# Cairn on the ModDB

Notes for the [Vintage Story ModDB](https://mods.vintagestory.at/cairn) entry, kept here
because a description pasted into a web form and forgotten is one that quietly stops being
true.

The description itself is [`moddb-description.html`](moddb-description.html) — HTML in a
`.html` file rather than fenced inside this one, because the ModDB stores descriptions as
HTML and a paste-ready file can be diffed, opened in a browser and copied whole. This file
is the reasoning around it.

**Type:** External Tool · **Tag:** Utility · **Side:** Client

There is no modpack type on the ModDB — the types are Client side, Server side, Game Mod,
External Tool and Other — which is the same gap Cairn exists to fill. A launcher fits
cleanly, though: [VS Mod Pack Launcher](https://mods.vintagestory.at/vsmodpacklauncher) is
listed under Utility and is the closest precedent.

## Summary (required field)

A modpack manager for Vintage Story: complete, reproducible mod sets, each pack with its
own worlds and its own game version.

## Pasting the description

Paste it as HTML, not as Markdown: `###` and `**` arrive as literal characters. The
editor is TinyMCE with the `code` plugin in its toolbar, so use the source-code button —
pasting rendered HTML into the WYSIWYG view mangles the download box.

Each paragraph is one long line on purpose. Editors that turn newlines into `<br>` shatter
a wrapped paragraph into a dozen short lines, and unwrapped source cannot be misread that
way. Headings are bold paragraphs rather than `<h3>`, which is what the listings on the
site actually use.

The download block leads, in a bordered box, because the page's own **Download** button
serves the stub zip rather than the app: a visitor who trusts the obvious button gets a zip
full of HTML. The box has to be prominent enough that it is read first, since it is the
only thing steering anyone away from that button — the description does not explain the
button, on the grounds that a listing is a poor place to apologise for the site it is on.
`<div>` and inline `style` both survive the site's sanitiser — htmLawed, `elements => '*'`
minus scripts and forms — and mods across the site already use `font-size` and
`background-color`.

## Why the download is a link and not a file

The ModDB is open source ([anegostudios/vsmoddb](https://github.com/anegostudios/vsmoddb)),
so this is settled rather than inferred. `lib/upload-limits.php` allows `dll`, `zip` and
`cs` for a release, one file per release, at **40 MB — where `MB = 1024 * KB`**
(`lib/core.php`), so 41,943,040 bytes. Cairn's artifacts are 42.5–51.1 MB. Every one of
them misses, Linux by about half a megabyte and macOS x64 by nine.

That is the whole reason the entry carries a stub zip. It is not a choice anyone would make
otherwise, and [VS Launcher](https://mods.vintagestory.at/vslauncher) ships a
`READ_ME_HOW_TO_DOWNLOAD.zip` for the same reason. Shrinking the builds is not a way out:
trimming 1.2% would rescue Linux alone while macOS stays 6–9 MB over.

Two things follow that are worth knowing before anyone tries again:

- **Nothing inspects a zip's contents** — `lib/fileupload.php` checks size, extension,
  count and permission, and stops. A zip need not contain a mod.
- **`mods.uploadLimitOverwrite` is a per-mod override**, applied in `lib/fileupload.php`
  and set by moderators through `PUT /api/mods/{modid}/releases/upload-limit`, bounded only
  by the server's PHP `post_max_size`. **Asking Anego Studios to raise Cairn's limit is the
  unblock**, and it is a feature they built for this rather than a favour. Ask for 64–80 MB,
  not 48: macOS x64 is already 51.1 MB and these grow with Avalonia and .NET.

If the limit is raised, the real artifacts go up as releases and most of this section comes
out, along with the reason the download box has to lead at all. Several files can
share one `modversion` — 73 mods on the site do it, Murple's server manager shipping
`_Win64_` and `_Linux64_` zips under 1.1.1 — so one release per platform per version is the
shape to use, rather than separate mod pages per platform the way Rustique and ModsUpdater
split theirs.

## Uploads cannot be automated

Worth recording so it is not re-investigated. Authentication is a session cookie issued by
redirecting through `account.vintagestory.at`, plus a per-user `actionToken` echoed as `at`
on every mutating request. There are no API keys, and file upload is not in the
authenticated API at all — it is `edit-uploadfile.php`, a multipart form POST. Automating a
release upload means scripting a login against the game account server with real
credentials in CI, which is not worth it for four uploads a release.

## Keeping this true

The download links are `cairns.gg/download/<platform>`, which redirect to whatever
`latest.json` currently names — so they do not carry a version and never need editing after
a release. Do not paste `download.cairns.gg` artifact URLs in their place: those are
versioned and immutable by design, and one pasted here goes stale the next time a release
is cut. The slugs are `windows`, `linux`, `macos-arm64` and `macos-x64`; `macos-intel` is
not one of them.

The description carries no version numbers and no file sizes, which is what lets it survive
a release untouched. Keep it that way: a size or a version in the copy is a line that goes
stale silently, and nothing on the page will point at it when it does.

The SmartScreen paragraph goes when the Windows build is signed, and the release notes in
`.github/workflows/release.yml` carry the same sentence for the same reason — both stop
being true on the same day. See *Registering the scheme* and *Signing and
notarising the macOS builds* in [building.md](building.md).

## Still outstanding

- **A source link, once the repository is public.** There is a licence now — PolyForm
  Strict, so the listing must say *source-available* and never *open source*; claiming the
  latter to a modding audience would be read as a promise the terms do not make. Worth a
  line either way, because "you can read what it does" is the point of publishing it.
- **A logo image.** Nothing here supplies one.

## Screenshots

Captured by a tool rather than by hand, so they can be retaken after a UI change instead of
going quietly stale:

```
CAIRN_SHOT_DIR=$PWD/artifacts/screenshots \
  dotnet tests/Cairn.App.Tests/bin/Debug/net10.0/Cairn.App.Tests.dll -method '*listing*'
```

It builds a library worth photographing — several packs, a full mod set, dependencies
underneath the mods that pulled them in — and reaches the real ModDB for names and icons,
because rows reading `carryon` against a blank square look like a mock-up rather than the
app. It writes:

| | |
|---|---|
| `01-a-pack-and-its-mods` | what the thing is |
| `02-adding-a-mod-from-moddb` | search, in the app |
| `03-a-pack-that-joins-a-server` | the auto-join case |
| `04-a-pack-on-an-older-game-version` | packs on different versions, side by side |
| `05-what-is-on-disk` | Preferences → Storage |
| `06-build-an-optimised-client` | the offer, on a pack Optimum is for |
| `07-what-it-will-cost` | the warning, before anything starts |
| `08-watching-it-build` | the live log |
| `09-running-with-optimum` | what the pack says afterwards |
| `10-pinning-a-version` | choosing which release a mod is held at |
| `11-a-pinned-mod` | the pin in both states, in a pack |

The last four are a sequence and read best in order: what is offered, what it costs, what
it looks like while it happens, and what the pack says at the end. They are the ones that
sell the feature no other launcher has.

Two things about them are deliberate. The demo home is `/tmp/cairn-demo-xxxx` rather than a
real temp directory, because the Settings tab prints the pack's paths and sixty characters
of machine noise across the bottom of a store image helps nobody. And the build window is
photographed from a view model that shows a build without running one — a real build takes
twenty minutes, so the alternative was a picture of a failure.
