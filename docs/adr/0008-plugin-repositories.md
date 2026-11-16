# ADR 0008 — Plugin repos are static, signed indexes with pinned keys

**Status:** accepted (2026-11-16)

## Context

ADR 0007 made services, themes and adblock packs plugins. Users should be able to get new ones without a new build, and the project should be able to maintain its own directory — including filter packs, which age fastest and are the most security-sensitive data we ship. If plugins can come from the network, the app needs an integrity model: a repo host compromise must not become arbitrary plugin content, and a plugin archive must not be swap-able in transit.

## Decision

- A repository is a **static HTTPS directory**: `index.json` plus an ECDSA P-256 signature, and versioned plugin archives verified by SHA-256 from that signed index.
- The official repo is `registry/` in this repository, built and signed in CI. Its key is pinned in the app at build time.
- User-added repos are trusted on first use (TOFU): fingerprint pinned on first successful fetch, and a later change blocks the repo until the user explicitly re-trusts it, showing both fingerprints.
- Integrity chain: signed index → `sha256` + `size` of the archive → re-validation of the unpacked manifest against the schema. Any mismatch aborts the install.
- Installs are never silent; uninstalls are always available; a repo can never touch the global auth/WebSocket allowlist.
- Data plugins only. `integration` (code) is not installable until phase 7 and will require a separate consent screen.

## Consequences

- New themes and fresh filter packs ship without a release, which matters most for adblock data.
- No server infrastructure: any static host, GitHub Pages or raw file host works.
- Key management becomes an operational duty: the official key is pinned in code, must never enter the repo, and needs a documented rotation path (new key co-signed by the old one).
- TOFU friction will annoy someone; that is accepted — silently accepting a changed key would make the signature pointless.
- Users who add a malicious repo can still get a malicious *theme* or *filter pack*. The blast radius is bounded by design: CSS is sanitised, host rules cannot override auth allowlists, and no code is executed.
