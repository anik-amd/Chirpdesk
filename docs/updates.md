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
`prerelease: false`.

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
  -Installer dist/Chirpdesk-Setup.exe `
  -Version 1.3.0 `
  -Url https://github.com/anik-amd/Chirpdesk/releases/download/v1.3.0/Chirpdesk-Setup.exe `
  -KeyPath D:\keys\chirpdesk-update.pem `
  -Out dist/update.json
```

The script hashes the installer, signs the canonical bytes, writes `update.json`, then reads it
back and verifies both the signature and the payload before exiting non-zero on any mismatch.

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
