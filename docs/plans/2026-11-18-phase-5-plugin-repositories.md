# Phase 5 - Plugin repositories

**Status:** substantially complete (2026-11-18). The core policy is in place: signed index model and parser,
P-256 signature verification, key fingerprints, compatibility filtering, archive limits and
zip-slip/executable rejection all have unit tests. The official registry key is pinned in
`RepositoryKeys`. A first repository slice is wired: settings rows, HTTPS sync with first-use
`key.pem` pinning, archive verification/installation into the user plugin root, and a
Repositories section on the Plugins page. Installs write a source sidecar, keep the previous
version for rollback, and expose Update, Roll back and Recycle-Bin Uninstall. A successful
install is disabled by default and the catalogue reloads immediately. Done live: the public
official registry is preseeded, and add/sync/install/update/rollback/uninstall were exercised
against it. Still open: the remaining manual refusal rows (changed key, corrupt index, zip-slip,
executable entry) beyond their unit coverage.

**ADR:** 0007 (data plugins), 0008 (signed static repositories)

## Goal

A user can add a static HTTPS repository, see its compatible plugins, install one after seeing
the key fingerprint and permissions, and later update or remove it. A changed repository key
blocks the repo until the user explicitly re-trusts it.

## Tasks

1. **Index and signature core** - complete.
   - `RepositoryIndex` / `PluginArchive` / `RepositorySignature` / compatibility tests.
2. **Repository settings** - next.
   - `Repositories` list in settings: URL, display name, public key PEM, fingerprint,
     auto-update, last sync. Official repo uses the pinned key, never a settings key.
   - Add/remove/re-trust commands with fingerprint comparison.
3. **Sync service**
   - Fetch `index.json` + `index.json.sig` over HTTPS, verify exact bytes, enforce 4 h TTL.
   - Cache the verified index; unreachable/invalid repo is logged and skipped.
   - Filter `apiVersion` and `minAppVersion`; show unsupported rows, never install them.
4. **Install chain**
   - Download archive, verify signed `sha256` and `size`, inspect zip, extract to temp, validate
     `plugin.json`, then move into `%APPDATA%\Chirpdesk\plugins\<id>\`.
   - Install disabled unless the user chose enable; never install `integration` in v1.
   - Atomic-ish install: temp folder then move; a failed move leaves no half plugin.
5. **Plugins page UI**
   - Repository section: add URL, fingerprint, trust state, sync, last check, auto-update.
   - Available plugin rows: name, version, kind, permissions, compatible badge, Install/Update.
   - Installed user plugins show their source repo and an Uninstall action.
6. **Release tooling**
   - Build `registry/index.json`, sort deterministically, sign exact bytes into
     `index.json.sig`, include plugin zip hashes/sizes, fingerprint.
   - Validate every archive with the same `PluginArchive` rules before publishing.
7. **Docs and manual matrix**
   - `docs/plugins.md` repository section, key rotation procedure, TOFU re-trust flow.
   - Manual rows: add repo, install, update, remove, changed key blocks, corrupt index is skipped,
     zip-slip and executable entries are refused.

## Definition of done

A signed repository can be added and synced without a new build; a compatible plugin installs
only after signature, hash, size, archive and manifest checks; a changed key blocks the repo with
both fingerprints visible; and the app still starts with every repository unreachable.
