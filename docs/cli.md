# cairn-cli

A headless front-end over the same `Cairn.Core` engine the launcher uses. Everything the
launcher can do is scriptable and testable here first, which is what it is for.

**It is not shipped in releases.** Putting a second program in front of people who were not
told it was coming, and have no documentation for it, costs more than it gives — so the
download is the launcher and nothing else. Build it yourself:

```bash
./dev.sh --cli          # artifacts/<rid>/cairn-cli, about two seconds
```

No command reference lives here on purpose. `cairn-cli --help` prints one, and a second copy
in a file would be wrong within a release or two.

```bash
cairn-cli --help
```

## What it is useful for

**Seeing what Cairn thinks it is looking at.** `info` reports the detected install, its
architecture, the runtime that would be chosen for it and the data path; `diagnostics <id>`
prints what a bug report needs, with home directories redacted.

**Doing something to a lot of packs.** The launcher is one pack at a time by design. A loop
over `cairn-cli sync` is not.

**Watching a sync decide.** `sync` prints what it resolved and why, including the checksum
refusals a lockfile produces:

```
$ cairn-cli sync anego
  x glassview   1.3.0 does not match the locked checksum — refusing it
```

**Trying something without a window.** A headless server, an SSH session, a CI job — all of
which is also why the server front-end (`cairn-server`, which *is* shipped) exists.

## Where the rules live

Nowhere in here. Every policy question — may this be published, may this URL be imported, is
this id safe, is this version string plausible — is answered by a type in `Cairn.Core` that
both front-ends call. A check implemented in one of them is a check the other does not make,
so the CLI cannot be more permissive than the launcher, or less.
