# Building and shipping Cairn

Everything about turning this source into the files people download: building it, cutting a
release, signing it, and where it goes. None of it is needed to *use* Cairn — that is the
README.

## Build

```bash
dotnet build
dotnet test tests/Cairn.Core.Tests/Cairn.Core.Tests.csproj   # 428 tests, 432 with the game
dotnet tests/Cairn.App.Tests/bin/Debug/net10.0/Cairn.App.Tests.dll   # 201 UI tests
```

Building to test, on whatever machine you are on:

```bash
./dev.sh              # build for this host only  (~5s)
./dev.sh --run        # build, then launch it
./dev.sh --no-sign    # skip code signing         (~4s)
./dev.sh --cli        # CLI only                  (~2s)
```

Prefer this over `dotnet run` on macOS. `dotnet run` uses whatever SDK is on `PATH`, and
if that SDK is x64 the launcher runs under Rosetta and feels sluggish; publishing for the
host rid is what produces a native build. On macOS `dev.sh` produces the `.app` bundle.

### Testing against a local cairns

```bash
cd ../cairns && ./dev.sh          # the server, in its own terminal
./dev.sh --local                  # a launcher pointed at it
```

`--local` sets `CAIRNS_SERVER=http://localhost:5080` *and* `CAIRN_DEFAULT_HOME=~/.cairn-dev`,
because the second half is not optional: publishing writes a `cairns.json` into the pack
recording where it went, and doing that to a real pack leaves it claiming to live at a
localhost URL that stops existing when the server does. `--server URL` and `--home DIR`
set them separately.

Sign-in mail is printed to the server's terminal rather than sent — see the cairns README.

Import refuses plain `http://` — a pack names the mods, their download URLs *and* their
hashes, so anyone able to rewrite one in flight picks what gets installed and writes
hashes to match. **Loopback is exempt**, because those packets never leave the machine:
`http://localhost:5080/you/pack.json` imports, `http://cairns.gg/…` does not. The check
is `PackSources`, in Core, so both front-ends answer it the same way.

## Cutting a release

Push a tag. `.github/workflows/release.yml` runs the tests, builds all four artifacts and
publishes a release with them attached. Not a draft: it was one so somebody could look
before the world saw an unnotarised build, but nobody downloads from here. The gate that
matters is on promoting `latest.json`, which is what people actually fetch.

```bash
git tag -a v0.2.0 -m "v0.2.0" && git push origin v0.2.0
```

| platform | artifact | built on |
|---|---|---|
| macOS (Apple silicon) | `cairn-<v>-macos-arm64.zip` | `macos-latest` |
| macOS (Intel) | `cairn-<v>-macos-x64.zip` | `macos-latest` |
| Windows | `cairn-<v>-windows-x64.zip` | `ubuntu-latest`, cross-published |
| Linux | `cairn-<v>-linux-x64.tar.gz` | `ubuntu-latest`, cross-published |
| Linux (server) | `cairn-<v>-linux-x64-server.tar.gz` | `ubuntu-latest`, cross-published |

The server is a separate artifact rather than a second file in the Linux tarball: somebody
putting a server in a container wants that binary and not a desktop launcher, and the
reverse is just as true. It is the only artifact `cairn-server` ships in — see
[running a server](#running-a-server-cairn-server) for why Linux alone.

Only macOS needs its own runner, because the `.app` bundle needs `codesign` and `plutil`;
the others are single-file binaries with no platform tooling behind them. The tag becomes
`CFBundleShortVersionString`, which is what Finder shows and what macOS compares to decide
whether an install is an upgrade.

Three details that are load-bearing:

- **`ditto`, not `zip`,** for the bundle. A `.app` holds symlinks and extended attributes,
  and plain `zip` flattens them into something macOS calls damaged.
- **`.tar.gz` for Linux**, because zip does not carry the executable bit and a download
  that needs `chmod +x` before it runs is a download that gets reported as broken.
- **Promotion is conditional, publishing is not.** Downloads come from R2, not from
  GitHub — the release here is a record of what was built. Uploading a version reaches
  nobody, because the files sit at a path nothing links to; moving `releases/latest.json`
  is what ships them, and that only happens when the macOS builds were notarised. An
  unnotarised build still uploads, still gets a URL, and simply is not made the download.

  Promote one anyway with a single `aws s3 cp latest.json`, if that is deliberate.

`workflow_dispatch` builds everything without publishing, which is how to find out a build
is broken before there is a tag claiming otherwise.

## Publishing to Cloudflare R2

**This is the distribution channel.** GitHub holds the source and a copy of each build;
people download from `download.cairns.gg`. Two secrets and three variables; with
`R2_ACCESS_KEY_ID` unset the job says so and does nothing.

| name | kind | what it is |
|---|---|---|
| `R2_ACCESS_KEY_ID` | secret | from an R2 API token with Object Read & Write |
| `R2_SECRET_ACCESS_KEY` | secret | the other half of it |
| `R2_ENDPOINT` | variable | `https://<account-id>.r2.cloudflarestorage.com` |
| `R2_BUCKET` | variable | the bucket name |
| `R2_PUBLIC_URL` | variable | the custom domain, e.g. `https://download.cairns.gg` |

The endpoint and bucket are variables rather than secrets so they appear in the logs. A
masked bucket name makes a failed upload much harder to read, and neither is a secret.

R2 speaks S3, so the client is the AWS CLI that runs on the runner already — with three
differences from a typical S3 provider, each of which is a way this quietly breaks:

- **No `--acl`.** R2 does not implement per-object ACLs and rejects one rather than
  ignoring it. What makes a file readable is the bucket's custom domain, which is a
  property of the bucket rather than of each object.
- **`AWS_DEFAULT_REGION=auto`.** R2 has one region, and the first label of the endpoint is
  the account id — so deriving the region from the endpoint, which is right for providers
  whose endpoint names their region, would sign requests for a region that does not exist.
- **Checksums only when required.** Recent AWS CLI versions add integrity checksums by
  default that not every S3-compatible provider accepts; asking for them only when needed
  survives CLI updates instead of breaking on one.

```
releases/1.2.3/cairn-1.2.3-macos-arm64.zip     immutable, cached for a year
releases/1.2.3/…                               every other artifact, plus SHA256SUMS
releases/1.2.3/manifest.json                   what that version was
releases/latest.json                           what to offer, cached for 5 minutes
```

**Versioned paths, never overwritten.** Somebody who linked a build a year ago should still
get that build, byte for byte — which is also what makes it safe to cache them forever,
since a URL cannot come to mean something else.

That includes the manifest. `latest.json` is rewritten every release, so a version's own
manifest is kept beside its artifacts — otherwise the sizes and checksums of 1.2.3 stop
existing the moment 1.2.4 ships, while the files they describe are still up. It is written
whether or not the version is promoted: a version nothing points at is still a version that
happened.

`latest.json` is the only mutable object, and holds the same bytes as the promoted
version's manifest rather than a pointer to it — one request for a reader, and no window in
which it names a manifest that is not up yet:

```json
{
  "version": "1.2.3",
  "publishedAt": "2026-08-01T17:50:51Z",
  "files": [
    { "platform": "macos-arm64", "name": "cairn-1.2.3-macos-arm64.zip",
      "url": "https://…/releases/1.2.3/cairn-1.2.3-macos-arm64.zip",
      "size": 48291043, "sha256": "f813d49e…" }
  ]
}
```

That is what a downloads page on the site should read, rather than a hardcoded list that
goes stale the release after somebody remembers to update it.

## Signing and notarising the macOS builds

This is the **direct-download** path, not the App Store one: somebody downloads a zip and
it opens. Nothing here submits an app anywhere, and none of it requires the sandboxing the
App Store insists on.

Two names in the table below suggest otherwise and are worth reading past. *Developer ID
Application* is the certificate Apple provides **for distribution outside the App Store** —
the store uses a different one. And an *App Store Connect API key* is just Apple's
credential system for their APIs; `notarytool` authenticates with it whether or not the
App Store is ever involved.

Five repository secrets turn it on. With none of them the workflow ad-hoc signs exactly as
it did before, and the release notes say so — there is no flag to remember.

| secret | what it is |
|---|---|
| `MACOS_CERTIFICATE` | the **Developer ID Application** certificate and key, exported as `.p12`, then `base64 -i cert.p12 \| pbcopy` |
| `MACOS_CERTIFICATE_PASSWORD` | the password set when exporting the `.p12` |
| `APPLE_NOTARY_KEY` | an App Store Connect API key (`.p8`), base64-encoded the same way |
| `APPLE_NOTARY_KEY_ID` | the key's ID, e.g. `ABCD123456` |
| `APPLE_NOTARY_ISSUER` | the issuer UUID from App Store Connect → Users and Access → Integrations |

**Developer ID Application**, not "Apple Development" or "Mac App Distribution" — those
cannot sign software distributed outside the App Store, and the difference is not visible
until notarisation refuses. Create it in the developer portal or Xcode → Settings →
Accounts → Manage Certificates, then export it *with its private key* from Keychain Access.

An **API key** rather than an app-specific password because it can be revoked on its own
and does not stop working when the Apple ID password changes.

The workflow imports the certificate into a keychain of its own, unlocked for that job
only, and calls `security set-key-partition-list` — without which `codesign` waits on a GUI
prompt nobody is there to answer and the job hangs until it times out.

All three steps are needed for a download that simply opens, and each covers a different
refusal:

| step | what it gets past |
|---|---|
| sign | "cannot be opened because the developer cannot be verified" |
| notarise | the quarantine warning macOS attaches to anything downloaded |
| staple | the same warning, for somebody whose first launch is offline |

They happen in that order, and stapling happens before packaging — staple afterwards and
the archive people download contains an app without its ticket.

### The macOS bundle must stay non-single-file

`build-macos-app.sh` publishes a directory rather than a single file, and while the reason
written there is startup — a single-file build self-extracts before the window can appear —
it is also what makes notarisation possible at all. A single-file .NET app unpacks its
native libraries to `~/.net/<app>` on first run, so the binaries that actually execute do
not exist at signing time and cannot be notarised. Apple has nothing to inspect and the
extracted copies carry no signature.

The Windows and Linux artifacts are single-file, which is fine: neither platform checks.

### Why `--deep`, which Apple discourages

.NET's apphost requires `cairn.runtimeconfig.json` and `cairn.deps.json` to sit beside the
executable, and `codesign` treats every non-code file in `Contents/MacOS` as nested code
that must carry its own signature. A `.json` cannot. Signing each nested binary and then
the bundle — the arrangement Apple actually recommends — fails at the last step, every
time, on a clean tree:

```
code object is not signed at all
In subcomponent: .../Contents/MacOS/cairn.runtimeconfig.json
```

Moving the payload out of `MacOS/` would mean replacing the apphost. The cost of `--deep`
is that the entitlements below reach nested code as well as the app; they are narrow, and
the notary service is the real arbiter of whether Apple minds.

What `--deep` does get right, checked rather than assumed: the hardened runtime reaches
every nested binary too, which is what notarisation requires.

```
libcoreclr.dylib     flags=0x10002(adhoc,runtime)
libSkiaSharp.dylib   flags=0x10002(adhoc,runtime)
cairn-cli            flags=0x10002(adhoc,runtime)
createdump           flags=0x10002(adhoc,runtime)
```

No Mach-O in the bundle is left unsigned, and `get-task-allow` — the debug entitlement that
guarantees rejection — is absent. `spctl -a` rejects an ad-hoc build, which is the expected
answer and the thing a real certificate changes.

### Entitlements

`macos-entitlements.plist`, and each line is a hole in the hardened runtime, so each has a
reason written next to it. `allow-jit` and `allow-unsigned-executable-memory` are what
CoreCLR needs to compile IL at runtime — without them the app dies on launch rather than
degrading. `disable-library-validation` is the one worth trying to remove once notarisation
is working.

The hardened runtime is applied to ad-hoc builds too, so a local build fails the way a
released one would rather than saving the surprise. Verified by launching one: it starts.

## macOS application bundle

```bash
./build-macos-app.sh                       # artifacts/osx-arm64/Cairn.app
ICON=path/to/icon.png ./build-macos-app.sh # with an icon
SIGN_IDENTITY="Developer ID Application: …" ./build-macos-app.sh
```

Produces a real bundle — `Contents/MacOS`, `Contents/Info.plist`, `Contents/_CodeSignature`
— so it gets a Dock tile, proper foreground activation and its own name in the menu bar.
The launcher binary is `Contents/MacOS/cairn`, and it is the only program in there. The
CLI used to ship beside it; it is a development tool with no documentation aimed at
anybody downloading a launcher, so releases carry the launcher alone and `cairn-cli` is
run from the source tree.

Deliberately **not** single-file: measured on an M-series machine, warmed, ten runs each,

| packaging | startup |
|---|---|
| plain directory (what the bundle uses) | **38 ms** |
| single-file, compressed | 78 ms |

Signing costs nothing measurable; the difference is single-file self-extraction. Larger on
disk as a result (113 MB vs 47 MB) — that is the trade.

Two macOS details worth knowing:

- `Application.Name` must be set in `App.axaml`. Without it Avalonia reports itself to
  LaunchServices as "Avalonia Application" regardless of `CFBundleName`.
- The bundle is signed with `codesign --deep` because macOS classifies managed `.dll`
  files as nested code by extension; signing only the bundle leaves them unsigned and
  `--verify --strict` fails. Apple discourages `--deep` for Developer ID submissions, so
  notarising would mean signing each nested binary explicitly instead.

Trimming is deliberately off — Avalonia leans on reflection, so it would need testing
per release rather than being assumed safe.

The UI tests render the real window on Avalonia's headless platform and assert on the
visual tree. That is deliberate: Avalonia resolves bindings at runtime, so a stale
binding path fails silently and the launcher would start looking fine and do nothing.
Note that a `TabControl` only realises the selected tab, so a test asserting on controls
in another tab has to select it first.

`Cairn.App.Tests` uses **xunit v3** because `Avalonia.Headless.XUnit` 12.x requires it;
pairing it with xunit v2 compiles and then discovers zero tests. xunit v3 projects are
self-hosting executables, hence running the dll directly rather than `dotnet test`.

Requires .NET 10. The game is a framework-dependent apphost, so it needs a .NET matching
*its* architecture: on Apple Silicon that is arm64 for a 1.22-or-later client and x64 —
installed via Microsoft's `.pkg`, which writes `/etc/dotnet/install_location_x64` — for an
older one. Cairn itself is architecture-agnostic: it only spawns the game, and reads
`VintagestoryAPI.dll` metadata without loading it.

## Registering the scheme

| platform | how | state |
|---|---|---|
| macOS | `CFBundleURLTypes` in the bundle, written by `build-macos-app.sh` | **works** — verified cold and with the app already running |
| Windows | `HKCU\Software\Classes\cairn`, written on startup | **works** — verified by clicking a link |
| Linux | `~/.local/share/applications/cairn-url-handler.desktop`, written on startup | **works** — verified by clicking a link |

macOS gets this free from the bundle format: LaunchServices reads the plist the first time
it sees the `.app`, so shipping a bundle *is* the registration. Windows and Linux have no
equivalent — registering there is an explicit act of installation, and Cairn ships as one
binary in an archive with no installer to perform one. So `PackLinkHandler` does it for the
app on startup, off the critical path, and never fails a launch over it.

On every start rather than once, because both mechanisms record an absolute path: somebody
who moves the binary would otherwise be left with a scheme pointing at where it used to be.
Nothing is written when the recorded value already matches, so the usual case costs a read.

**Windows still wants single-instance handling**, which this does not add. With no installer
it launches a *new* copy per click, and two launchers sharing one `~/.cairn` can race.
Registering the scheme is what makes the link arrive at all; making a second click reach the
window already open is a separate job.

On macOS the scheme binds once LaunchServices has seen the bundle somewhere it scans, so a
freshly built `artifacts/` copy may need `lsregister -f` before a link finds it.

When a click seems to do nothing, the app writes one line to stderr saying whether the link
arrived and whether it was refused — the three causes (never delivered, delivered and
refused, worked but the window is behind something) otherwise look identical:

```bash
open --stdout /tmp/cairn.log --stderr /tmp/cairn.log -n artifacts/osx-arm64/Cairn.app
```

Release artifacts, all platforms at once:

```bash
./build-release.sh                 # osx-arm64, osx-x64, win-x64, linux-x64
./build-release.sh linux-x64       # or just one
```

Self-contained, single-file, compressed — roughly **36 MB** for the CLI and **47 MB** for
the launcher per platform. Cross-publishing works from any host. Note the RIDs here are
Cairn's *own* binary: the arm64 build exists so the launcher runs natively on Apple
Silicon, and it still resolves an x64 runtime for the x64 game.
