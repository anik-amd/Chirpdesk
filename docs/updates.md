# Updates

**ADR:** 0006. The app only runs an installer it can verify. Nothing in this document changes the
rule that a dev build or a build with no pinned key refuses every update.

## Release assets

Every GitHub Release in the stable channel ships:

| Asset | Purpose |
|---|---|
| `Chirpdesk-Setup.exe` | Per-user installer, no elevation for the app itself |
| `update.json` | Signed manifest: version, URL, size, SHA-256, signature |
| `Chirpdesk-<version>-portable.zip` | Portable build; never self-updates |

The manifest URL is the release asset's `browser_download_url`, so it is absolute `https`.
`update.json` is fetched only after the release JSON has been checked for `draft: false` and
`prerelease: false`. The release repository must be public: an unauthenticated client cannot
read a private repo's releases, and embedding a token in the app is forbidden.

## Manifest

```json
{
  "version": "1.3.0",
  "channel": "stable",
  "url": "https://github.com/anik-amd/Chirpdesk/releases/download/v1.3.0/Chirpdesk-Setup.exe",
  "size": 48291840,
  "sha256": "fef6689acd9011dc45034ad2bc7570f06536086f220cd9aacbfba73170814cc9",
  "signature": "kD/WLn4pVJiXp7W/g15o5g1QASS9xRhPZ4kYB5n56nYiFhFCzGdqSM/pINinLnbb5XcpzzWFwn5/TsXJsM7M8A=="
}
```

All fields are required and are read case-insensitively. Unknown fields are ignored so a future
manifest can add notes without breaking older clients.

## What is signed

The signature covers exactly these five fields, in this order, as compact JSON with relaxed
escaping and no trailing newline:

```json
{"version":"1.3.0","channel":"stable","url":"https://…/Chirpdesk-Setup.exe","size":48291840,"sha256":"fef6…"}
```

`signature` is deliberately absent. `UpdateManifestCodec.CanonicalBytes` is the reference
implementation; release tooling must not reserialize the manifest with a different key order or
indentation.

Signature format: ECDSA P-256 over SHA-256, fixed 64-byte IEEE P1363, base64. The public key is
SPKI PEM pinned in the app. The private key is PKCS#8 PEM, kept off-repo and off the browsing
machine.

## Signing a release

```powershell
# once, on the release machine
pwsh scripts/new-update-key.ps1 -Path D:\keys\chirpdesk-update.pem

# per release, after the installer exists
pwsh scripts/sign-update.ps1 `
  -Installer dist/Chirpdesk-Setup-1.3.0.exe `
  -Version 1.3.0 `
  -Url https://github.com/anik-amd/Chirpdesk/releases/download/v1.3.0/Chirpdesk-Setup-1.3.0.exe `
  -KeyPath D:\keys\chirpdesk-update.pem `
  -Out dist/update.json
```

The script hashes the installer, signs the canonical bytes, writes `update.json`, then reads it
back and verifies both the signature and the payload before exiting non-zero on any mismatch.

## Release automation

`.github/workflows/release.yml` runs on a `v*` tag (or manual dispatch with a version) and:

1. refuses a tag whose version does not match `src/Chirpdesk/Chirpdesk.csproj`;
2. restores, builds and runs the unit tests;
3. packs the installer and portable zip;
4. creates a **draft** GitHub release with those two assets.

Signing stays on the release machine because the private key is offline by design. Download the
draft's installer, sign it, upload the manifest, then publish:

```powershell
gh release download v1.3.0 --pattern Chirpdesk-Setup-1.3.0.exe -D dist
pwsh scripts/sign-update.ps1 -Installer dist/Chirpdesk-Setup-1.3.0.exe -Version 1.3.0 `
  -Url https://github.com/anik-amd/Chirpdesk/releases/download/v1.3.0/Chirpdesk-Setup-1.3.0.exe `
  -KeyPath D:\keys\chirpdesk-update.pem -Out dist/update.json
gh release upload v1.3.0 dist/update.json
gh release edit v1.3.0 --draft=false --notes-file notes.md
```

A draft is invisible to `releases/latest`, so an unfinished release cannot reach the updater.
Do not create the release locally first: the tag push has to reach the workflow so the draft owns
the build.

## Client policy

`Chirpdesk.Core.Updates.UpdateVerifier` is the single decision point:

1. The URL is absolute `https`.
2. The channel is `stable` and the version is a plain numeric version.
3. The version is strictly newer than the running build (replay of an older signed release is
   refused).
4. The manifest signature verifies against the pinned P-256 key.
5. After download, the installer's size and SHA-256 match the manifest.
6. Only then does the app dispose its WebView2 environments and run the installer silently.

Any failure logs a reason and keeps the current build running. There is no retry loop. Portable
builds and builds without a pinned key never check or apply updates.

## Key rotation

1. Generate a new key; do not delete the old one yet.
2. Ship one release signed by the old key whose notes announce the rotation and whose build pins
   the **new** public key.
3. From the next release onward, sign with the new key. Keep the old key until that release is
   widely installed, then destroy it.

A lost key is a manual-reinstall event: there is no recovery path in the client, by design.


## Client wiring

Settings -> Updates has an opt-in **Check for updates automatically** switch, **Check now**, and
**Install and restart** when a signed release is newer. The app never installs from a URL in the
release body: it fetches the signed `update.json`, verifies it against the pinned P-256 key,
downloads the installer, verifies its exact size and SHA-256, disposes every WebView2 runtime,
then runs the installer with:

```text
/SILENT /SUPPRESSMSGBOXES /NORESTART /SP- /RELAUNCH
```

The installer's `AppMutex` waits for Chirpdesk to exit. `/RELAUNCH` makes the silent installer
start the new build after it finishes. Partial downloads are deleted on failure and on the next
successful boot; a mismatch never reaches `Process.Start`.

The pinned key is in `src/Chirpdesk/Updates/UpdateService.cs`. Its fingerprint is
`ecdsa-p256:bcecc91f49a68b2a`. The private half is not in the repository.
