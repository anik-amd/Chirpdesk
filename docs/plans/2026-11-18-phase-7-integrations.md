# Phase 7 - Out-of-process integrations

**Status:** designed (2026-11-18); tasks 1, 2, the Core half of task 3 and the install half of
task 6 are implemented. ADR 0016 and `docs/integrations.md` are the contract. Task 1 landed the
integration manifest/codec/loader, the integration-only permission matrix, kind-aware archive
policies and the missing `TryInspect` enforcement before extraction. Task 2 landed
length-prefixed framing, the JSON-RPC 2.0 envelope parser and writer, the closed method allowlist
with permission mapping, and bounded payload readers. Task 3 has the Core session engine
(handshake, dispatch, timeouts, restart backoff, crash handling) with 12 tests, plus the Windows
launcher with a kill-on-close job object, first-instance named pipe and NUL-drained stdout. Task 6
has install-time integration validation and the executable hash recorded in the source sidecar.
The stub helper lives at `tests/helpers/StubIntegration`; `--integration-smoke=<folder>` launches it
through the real Windows launcher and proved handshake, notify, unread, command and stop live with
no orphan process. The app runtime is wired: sessions start for enabled integrations at boot and after a
catalogue reload, notifications go through the gated toast sink, unread counts add to the tray,
the plugin card exposes Start/Stop/Restart, the second consent screen gates installation, and
argument-free declared commands appear in the command bar. Text commands still need an argument
surface. Manual rows and the TDLib helper remain.

**ADR:** 0007 (data plugins, code out-of-process), 0016 (this boundary)
**Plan:** `PLAN.md` §4.1, phase 7 row

## Goal

A user can install a signed `integration` plugin, read a consent screen that names the executable
and its capabilities, and run a helper process that delivers notifications and unread counts
without any plugin code entering the Chirpdesk process. The helper can be stopped, crashes and
restarted, updated, and uninstalled without touching the app's sessions.

## Guardrails

- No in-process loading, ever. A helper is a child process speaking a closed JSON-RPC surface.
- No path, handle or settings access is handed to a helper. It gets its own data directory only.
- `process` remains refused for every kind except `integration`.
- The host never logs notification titles or bodies.
- The boundary is capability and crash isolation, not a sandbox; the consent screen says so.

## Tasks

1. **Contract core** (`Chirpdesk.Core/Integrations`)
   - `IntegrationManifest`: `protocolVersion`, `executable`, `commands[]`; validation identical in
     spirit to `PanelValidator` (plain file name, `.exe`, command ids, argument mode, limits).
   - Permission matrix: `process` required; `notifications`, `unread`, `commands` only with it;
     reject the set on every other kind.
   - Kind-aware archive limits (integration: 64 MB / 300 files) without raising data-plugin limits.
   - Tests: valid/invalid manifests, bad executable names, duplicate command ids, permission
     combinations, limit enforcement.

2. **Protocol codec** (`Chirpdesk.Core/Integrations`)
   - 4-byte little-endian length framing, UTF-8 JSON, 256 KiB cap, partial reads and clean
     rejection of oversized or truncated frames.
   - JSON-RPC 2.0 request/response/notification models with id matching and typed errors.
   - Method allowlist and argument validation for both directions.
   - Tests: round trip, split and coalesced frames, oversized frame, unknown method, wrong id,
     permission-denied `notify`, argument caps.

3. **Process host** (`src/Chirpdesk/Integrations`, P/Invoke in `Interop/`)
   - Launch a resolved executable with the environment contract; redirect stdout/stderr to NUL.
   - Job Object kill-on-close and process-tree kill; pipe server with `FirstPipeInstance`,
     a random GUID name and a 32-byte token compared in constant time.
   - Handshake timeout (5 s), `command` timeout (10 s), `ping` (2 s), `shutdown` grace (3 s).
   - State machine: disabled, starting, running, stopping, error; crash backoff 1 s / 5 s / 30 s.
   - Exit never waits more than 2 s; no helper survives the host.
   - Tests (integration category): stub helper handshake, command round trip, hang then kill,
     crash then restart, token mismatch, no orphan after host exit.

4. **Bridge to the app**
   - `notify` → existing toast pipeline with an integration tag; `activateCommand` focuses the
     declared command.
   - `unread` → tray badge contribution, cleared on stop/disable/uninstall.
   - `status` → plugin card state; `log` → capped, sanitised log lines.
   - Notification bodies never enter the log (invariant 4); add a test that greps the log sink.

5. **Consent and lifecycle UI**
   - Integration card on the Installed tab: state, Start/Stop/Restart, data folder, last status.
   - Second consent screen (executable + SHA-256, publisher, permissions, commands, data folder,
     network and same-user-access statement, required checkbox).
   - Re-consent when executable hash, permissions or commands change on update.
   - Uninstall: plugin folder to Recycle Bin, offer helper data deletion (default on).
   - Tests: changed-consent comparison, install refused without checkbox, card states.

6. **Install and registry**
   - Repository install accepts `integration` with the larger limits, records the executable
     SHA-256, and refuses launch if the file changed.
   - Update stops the helper before replacing the folder and starts it after re-consent.
   - `scripts/build-registry.ps1` may package integrations for a test repo; the official registry
     stays without one until a real helper ships.
   - Tests: archive with exe accepted at the integration limit and refused at the data limit;
     hash mismatch blocks launch.

7. **Commands**
   - Declared commands appear in the Command Bar as `> integration: <title>`.
   - Invoking sends `command { id, argument }`; text commands prompt for an argument in the bar.
   - A command that is undeclared or has a bad argument is rejected before it reaches the helper.
   - Tests: allowlist and argument validation; disabled/stopped helper starts on demand.

8. **Stub helper and end-to-end**
   - `tests/helpers/stub-integration` console project: initializes, emits one notification, one
     unread count, answers a command, exits on `shutdown`. Built only for tests and manual runs.
   - A local static repo containing the stub proves install → consent → run → toast → command →
     update → uninstall without a real messenger.
   - TDLib is deliberately not part of this phase; it is a separate helper deliverable once the
     host is proven.

9. **Documentation and review**
   - ADR 0016 and `docs/integrations.md` (done); update `docs/plugins.md` links and the manual
     matrix.
   - Security review checklist: token handling, pipe permissions, path resolution, log contents,
     uninstall data handling, `process` gating.

## Definition of done

- A signed stub integration installs only after the second consent, runs as its own process, and
  produces a toast and a badge count.
- Killing the helper leaves the app and every webview untouched; a hang is killed at the timeout.
- A helper whose executable changed on disk refuses to launch.
- An update that changes permissions or commands re-prompts.
- Uninstall removes the executable and offers to remove the helper's data.
- No code path loads plugin code in-process; `process` still fails validation for non-integration
  plugins.
- Automated build, tests and gate stay green; the manual rows are added to
  `docs/manual-verification.md`.

## Manual matrix

- Install the stub integration from a local test repo; the second consent names the exe, hashes,
  permissions and commands; Install stays disabled until the checkbox is ticked.
- Toast appears with the helper's title/body; the log contains lifecycle lines but no body text.
- Badge shows the unread count; Stop clears it.
- Kill the helper in Task Manager: the card goes to error, then restart backoff brings it back.
- Hang the helper: the host kills it at the timeout and logs the reason.
- Tamper with `helper.exe` on disk: launch refuses with "helper changed on disk".
- Publish a stub update with one extra permission: updating asks for the second consent again.
- Uninstall: plugin folder and (default) helper data go to the Recycle Bin.
- Disable and re-enable: no orphan process, no duplicate process.

## Risks

| Risk | Mitigation |
|---|---|
| A hostile helper reads the user's files | Cannot be prevented at the same user; the consent screen says it plainly and shows hashes and publisher key |
| Users read "signed plugin" as "signed Windows program" | No Authenticode claim; UI states the signature is the repository's |
| Large TDLib payloads | Kind-aware 64 MB limit; streamed download already exists |
| Antivirus flags a downloaded helper | Document it; keep the repository and hash visible before install |
| Pipe squatter or token leak | Random GUID, `FirstPipeInstance`, 32-byte token in the environment, constant-time compare |
| Helper stdout leaks secrets | stdout/stderr go to NUL; diagnostics only through sanitised `log` |
| Orphans on crash | Job Object kill-on-close plus process-tree kill |
