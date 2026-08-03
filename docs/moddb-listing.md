# Cairn on the ModDB

Copy for the [Vintage Story ModDB](https://mods.vintagestory.at/) entry, kept here because
a description pasted into a web form and forgotten is one that quietly stops being true.

**Type:** External Tool · **Tag:** Utility · **Side:** Client

There is no modpack type on the ModDB — the types are Client side, Server side, Game Mod,
External Tool and Other — which is the same gap Cairn exists to fill. A launcher fits
cleanly, though: [VS Mod Pack Launcher](https://mods.vintagestory.at/vsmodpacklauncher) is
listed under Utility and is the closest precedent.

---

## Summary (required field)

A modpack manager for Vintage Story: complete, reproducible mod sets, each pack with its
own worlds and its own game version.

---

## Description

**Paste this as HTML, not as Markdown.** The ModDB stores descriptions as HTML — the API
hands back `<p>…</p>` for every mod on the site — so `###` and `**` arrive as literal
characters. If the editor shows no HTML view, look for a source or `<>` button; failing
that, paste from a rendered preview, which carries the formatting as rich text.

Each paragraph is one long line on purpose. Editors that turn newlines into `<br>` shatter
a wrapped paragraph into a dozen short lines, and unwrapped source cannot be misread that
way. Headings are bold paragraphs rather than `<h3>`, which is what the listings on the
site actually use.

```html
<p>Vintage Story downloads the mods a server tells you about. What it has no concept of is a <strong>pack</strong>: a mod set you put together yourself, pinned to exact versions and shareable as one thing.</p>

<p>Put anything in a pack — content, worldgen, tools, quality-of-life. Cairn installs what the pack names and hands the lot to the game, so a pack works as well for a world of your own as for the set everyone on a server agrees to run.</p>

<p><strong>Packs</strong></p>

<p>A pack records exactly which mod versions you got, with a checksum for each. Hand it to somebody and they get your versions — the same bytes, not roughly the same mods. Mods that other mods depend on come along automatically.</p>

<p><strong>Nothing moves unless you ask</strong></p>

<p>Mods break saves, so pressing Play never updates one. <strong>Check for updates</strong> shows what has moved and offers them one at a time, and a mod you pinned to a version is never offered an update.</p>

<p><strong>Every pack is its own game</strong></p>

<p>Each pack has its own worlds, mod configs and settings, so a world built under one mod set cannot be opened by accident under another. You still sign in once — your login follows you between packs.</p>

<p><strong>It installs the game too</strong></p>

<p>Point one pack at 1.21 and another at 1.22 and Cairn fetches both, launching whichever one a pack asks for. It installs the right .NET for each as well, which is the part that usually goes wrong.</p>

<p><strong>Sharing</strong></p>

<p>This is the part worth having when you play with other people. Send a pack as a single file, or publish it and pass the link around — a published pack's page has an <strong>Open in Cairn</strong> button. Everyone ends up on the same mods at the same versions, which is usually the difference between an evening playing and an evening working out whose install is wrong.</p>

<p>Nobody is agreeing to anything blind: the link shows every mod and the exact version it would install, and waits.</p>

<p><strong>Getting it</strong></p>

<p>One download and nothing to install first. You still need a Vintage Story account to play.</p>

<p><strong>Download:</strong> <a href="https://cairns.gg/download/windows">Windows</a> · <a href="https://cairns.gg/download/macos-arm64">macOS (Apple silicon)</a> · <a href="https://cairns.gg/download/macos-x64">macOS (Intel)</a> · <a href="https://cairns.gg/download/linux">Linux</a></p>

<p>On Windows the first run shows "Windows protected your PC" — the build is not signed yet, so choose <strong>More info</strong> → <strong>Run anyway</strong>. It does not come back.</p>

<p><em>Not to be confused with Cairns, the mod that adds piles of rocks — no relation.</em></p>
```

---

## Notes before submitting

- **The file upload is the awkward part.** The ModDB's flow expects a release with a
  zipped mod file, and Cairn's builds are 42–51 MB and are not mods. VSMPL solved this by
  linking out to their own site "due to its file size" and putting only a small downloader
  zip on the ModDB. Cairn has no such downloader, so either link out and see whether an
  entry can stand without a release attached, or build one.
- **No licence and no public source link.** The repo is private and has no LICENSE file,
  so nothing here claims open source or points at GitHub.
- **Still wanted by the submission form:** a logo image and screenshots. The pack list
  with a couple of real packs in it, the mod list showing pins and dependencies, and the
  version-change preview dialog are the three that show what it actually does.

## Keeping this true

The download links are `cairns.gg/download/<platform>`, which redirect to whatever
`latest.json` currently names — so they do not carry a version and never need editing after
a release. Do not paste `download.cairns.gg` artifact URLs in their place: those are
versioned and immutable by design, and one pasted here goes stale the next time a release
is cut. The slugs are `windows`, `linux`, `macos-arm64` and `macos-x64`; `macos-intel` is
not one of them.

The SmartScreen paragraph goes when the Windows build is signed, and the release notes in
`.github/workflows/release.yml` carry the same sentence for the same reason — both stop
being true on the same day. See the README's *Registering the scheme* and *Signing and
notarising the macOS builds*.
