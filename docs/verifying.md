# Verifying a download against this source

Cairn is published so it can be read. This is how you check that the binary you have is
built from what you read — and what each of the three mechanisms does and does not prove.

The source is published so it can be read. That is worth very little on its own: reading it
tells you what Cairn *would* do, and the thing on your disk is a binary somebody else built.
Three separate mechanisms close that gap, and they are worth keeping distinct, because each
answers a question the other two cannot.

**1. The manifest says what the bytes should be, and is signed.** `manifest.json` carries a
SHA-256 for every artifact, and `manifest.json.minisig` is a detached signature over it made
in the `manifest` job — which holds the signing key and no credential that can write to
object storage. The public half is [`cairn.pub`](../cairn.pub), committed here.

```bash
minisign -Vm manifest.json -p cairn.pub
sha256sum -c SHA256SUMS
```

That proves the download is intact and is what the key holder meant to ship. It proves
nothing about where it came from, and a reader who has no reason to trust the key holder
gains nothing from it at all.

**2. The build attestation says which commit it was built from, and GitHub signs that.**
Every release artifact is attested with `actions/attest`: GitHub mints a Sigstore
certificate against the workflow's own OIDC identity and signs a SLSA statement binding the
artifact's digest to this repository, this workflow file, this commit and this run.

```bash
gh attestation verify cairn-1.2.3-linux-x64.tar.gz --repo dizzyd/cairn-app
```

This is the one that answers the inspector's question, and it is the only one here that
does not rest on trusting whoever cut the release. Nobody can forge it by hand — not
whoever holds the R2 credentials, not whoever holds the minisign key, not the account
owner. The only thing that produces one is that workflow actually running on that commit.
The bundle is published beside the downloads as `cairn-<version>.intoto.jsonl` and named by
the manifest, so it also verifies offline, for somebody who took the file from
`download.cairns.gg` and never touched GitHub:

```bash
gh attestation verify cairn-1.2.3-linux-x64.tar.gz \
  --repo dizzyd/cairn-app --bundle cairn-1.2.3.intoto.jsonl
```

Public repositories only, on every plan; a private one needs Enterprise Cloud. The step is
skipped while this repository is private, and the manifest then carries no `attestation`
field rather than naming a file nobody made.

**3. The checksums exist in two places reached by different credentials.** `SHA256SUMS`
goes to R2 with the artifacts and is also written into the GitHub release, which the R2
token cannot touch. That is detection rather than prevention: whoever holds the R2 keys can
still replace a download, but not without the two copies disagreeing.

Three smaller things make the chain legible end to end:

- **The commit is inside the binary.** CI sets `SourceRevisionId` from `GITHUB_SHA`, so the
  informational version is `1.2.3+<sha>`, and the diagnostics report — *Copy diagnostics* in
  the launcher, `cairn-cli diagnostics` on the command line — prints it beside the version.
  The manifest and the attestation name that same commit, so a bug report identifies the
  source that produced it, and the three either agree or visibly do not.
- **The dependency graph is pinned.** Every project has a `packages.lock.json` and CI
  restores in locked mode, so "built from this commit" also fixes the 33 resolved packages,
  including the native payloads that end up inside the signed artifact and that no `.csproj`
  names. Without that, the same commit could build from different code.
- **The build log is public.** Once this repository is, the run named in the manifest is
  readable by anyone, including everything the workflow did to produce the artifacts.

**Tag releases with a signed tag**, so that tag → commit is attributable the same way the
attestation makes commit → artifact attributable. It is the one link in the chain the
workflow cannot enforce: the attestation faithfully records whatever commit the tag pointed
at, including the wrong one.

Both the tags and the commits under them are signed. What that gets you as a reader is two
checks you can run yourself:

```bash
git verify-tag v1.2.3            # Good "git" signature ...
git log --show-signature -1      # or --format='%h %G?' — G for good, N for unsigned
```

GitHub shows the same thing as a **Verified** badge on the commit and tag. Verifying locally
needs an allowed-signers file naming who you are willing to trust — without one, git will
report a signature as good and then decline to say whose it is.

Commits being signed is a smaller claim than the tag's, and worth keeping distinct: it says
an author's key stood behind each commit, while the tag is what a release is cut from and
the attestation is what binds that commit to a binary. Anything committed before this was
set up is unsigned and stays that way — re-signing history would move every commit to a new
hash, breaking the tags and the attestation that name them, to assert something about the
past that was not true at the time.

**GitHub keeps authentication keys and signing keys in separate lists**, and being in the
first does not put a key in the second — so a tag signed by the key that pushed it still
shows as unverified until the same public key is added again with type *signing*:

```bash
gh auth refresh -h github.com -s admin:ssh_signing_key
gh ssh-key add --type signing --title "release signing" ~/.ssh/id_for_signing.pub
```

What none of this offers is a **reproducible build**. You cannot rebuild a commit and get a
byte-identical artifact: the single-file bundle is compressed, and the macOS bundle carries
a signature with a timestamp in it and a stapled notarisation ticket that only Apple can
issue. `ContinuousIntegrationBuild` is set under CI so the *managed assemblies* are
deterministic, which is enough to rebuild a commit and diff the DLLs inside the bundle —
useful, and short of a guarantee. The attestation is what stands in for one, and the
difference is worth being plain about: it says GitHub watched this workflow build these
bytes from this commit, not that anyone else can produce them again.

And the obvious limit, since the whole section is about trust: none of it says the source is
*good*. It says the binary is that source. Reading it is still your job.
