# ADR 0006 — Self-update only with ECDSA P-256 + SHA-256 verification

**Status:** accepted (2026-11-16), crypto choice corrected after review

## Context

The app is a background process with access to the user's logged-in chat sessions. An update mechanism that can be tricked into running an arbitrary installer is a remote code execution path that inherits every session the user has. The v1 build is unsigned (no certificate budget) and distributed through GitHub Releases.

Ed25519 was the first choice and is wrong for this stack: .NET 8 (and 9/10) ship no Ed25519 implementation. Using it would require BouncyCastle (a third runtime package, over the two-package budget) or NSec (native libsodium, a native dependency). Since signature verification is the one thing that must be boring, the scheme has to fit the platform.

## Decision

- Installer: Inno Setup, per-user, no elevation; portable `.zip` alternative that never self-updates.
- Update check: GitHub Releases, stable channel only, opt-in.
- Verification: manifest is **ECDSA P-256 signed (SHA-256)**; payload SHA-256 must match the manifest.
- Key formats: public key is SPKI PEM pinned in code; signature is fixed 64-byte IEEE P1363, base64 in the manifest's `signature` field. Private key stays off-repo, on a machine not used for browsing.
- On any mismatch, malformed manifest, missing asset, or a version that is not newer than the running build: refuse, log, keep running the current version. No silent retry loop and no signed-downgrade replay.
- Installer is run only after verification, with the app's own AUMID shortcut re-created on success.

## Consequences

- A compromised GitHub account cannot ship a working malicious update without the signing key.
- A valid but older signed release cannot be replayed as a downgrade: the manifest version must be newer than the running build.
- ECDSA P-256 is in-box (`System.Security.Cryptography.ECDsa`), FIPS-friendly, and adds no dependency; signatures are ~64 bytes versus Ed25519's 64, so there is no practical cost.
- We lose Ed25519's deterministic signatures and modern profile. That is a deliberate, documented downgrade; revisit if .NET ships Ed25519 or a crypto dependency is approved by ADR.
- Key loss means manual reinstall for users; the public key is therefore pinned in code and in the release workflow, with a documented rotation procedure.
- Until a code-signing certificate exists, SmartScreen will warn on first install; the README states this plainly rather than telling users to ignore warnings.
