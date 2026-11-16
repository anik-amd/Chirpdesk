# ADR 0006 — Self-update only with Ed25519 + SHA-256 verification

**Status:** accepted (2026-11-16)

## Context

The app is a background process with access to the user's logged-in chat sessions. An update mechanism that can be tricked into running an arbitrary installer is a remote code execution path that inherits every session the user has. The v1 build is unsigned (no certificate budget) and distributed through GitHub Releases.

## Decision

- Installer: Inno Setup, per-user, no elevation; portable `.zip` alternative that never self-updates.
- Update check: GitHub Releases, stable channel only, opt-in.
- Verification: manifest is Ed25519-signed; payload SHA-256 must match the manifest. Private key stays off-repo, on a machine not used for browsing; only the public key ships in the app.
- On any mismatch, malformed manifest or missing asset: refuse, log, keep running the current version. No silent retry loop.
- Installer is run only after verification, with the app's own AUMID shortcut re-created on success.

## Consequences

- A compromised GitHub account cannot ship a working malicious update without the signing key.
- Key loss means manual reinstall for users; the public key is therefore pinned in code and in the release workflow, with a documented rotation procedure.
- Until a code-signing certificate exists, SmartScreen will warn on first install; the README states this plainly rather than telling users to ignore warnings.
