# ADR 0016 - Integration plugins run as a separate process behind a closed JSON-RPC surface

**Status:** accepted (2026-11-18)

## Context

Some services expose a real protocol instead of a usable web client (Telegram via TDLib is the
canonical case). Chirpdesk's data-only plugin model cannot express that, and loading a .NET
assembly in-process is not acceptable: the app holds every signed-in session, so an in-process
assembly is full trust and `AssemblyLoadContext` is not a sandbox (ADR 0007).

The remaining question is what the boundary actually buys and how to enforce it without inventing
a general-purpose plugin platform. A same-user helper process can still read the user's files,
including the app's data. It cannot, however, be trusted with handles, cannot corrupt the app's
state, and can be killed when it hangs. The boundary is a capability and crash boundary, not an
OS sandbox, and the design must say so rather than imply more.

## Decision

1. **Separate process only.** An `integration` plugin ships an executable plus
   `integration.json`. The host resolves the executable to a plain file name inside the plugin
   folder, never a path, and launches it directly. No shell, no user-supplied arguments, no
   `cmd.exe`, no in-process loading.
2. **Named pipe, JSON-RPC 2.0.** One pipe per launch
   (`\\.\pipe\chirpdesk-integration-<guid>`), one client, length-prefixed UTF-8 JSON frames
   (4-byte little-endian length, 256 KiB cap). No TCP port, so no firewall prompt and no network
   exposure. A per-launch token in the environment authenticates the handshake; the pipe name
   alone is not enough.
3. **Kill-safe lifecycle.** The process is assigned to a Job Object with
   `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, so it dies with the host. Stop sends `shutdown`, waits
   3 s, then kills the process tree. A crash restarts with 1 s, 5 s, 30 s backoff, then stays
   stopped until the user starts it. Closing the app never waits more than 2 s for a helper.
4. **Closed method surface.** The host accepts only `notify`, `unread`, `status` and `log` from a
   helper; the helper accepts only `initialize`, `shutdown`, `ping` and `command`. There is no
   "invoke anything" method and no reflection bridge. Each accepted method has a permission:
   `notifications`, `unread`, `commands`. `process` is required to run at all, and only
   `integration` manifests may declare it.
5. **No host paths, no host state.** The helper receives its own data directory
   (`%APPDATA%\Chirpdesk\integrations\<id>\`), the protocol version and the nonce, and nothing
   else. It never gets a webview handle, the settings file, profile paths, the log path or the
   toast sink itself.
6. **Install-time hash pin, launch-time re-check.** The signed index already covers the archive.
   After extraction the host records the executable's SHA-256 in the integration record and
   refuses to launch if the file changes on disk. This catches local tampering, not a compromised
   publisher; the repository key remains the publisher trust anchor.
7. **Consent is a second, explicit screen.** The normal plugin details sheet is not enough. It
   names the executable, the publisher, the declared permissions and commands, the data folder,
   and states that the helper can use the network and runs with the user's own file access. A
   checkbox gates the install; updates that change the executable or permissions ask again.
8. **Data lifecycle is explicit.** Uninstall moves the plugin folder to the Recycle Bin and offers,
   default on, to move the helper's data directory there too, because it may hold session tokens.
9. **No Authenticode claim.** v1 has no code-signing certificate. The UI shows the repository key
   fingerprint and the archive/executable hashes, and says the signature is the repository's, not
   a Windows publisher signature.
10. **Optional commands only.** A helper may declare command-bar entries in `integration.json`.
    Invoking one sends `command { id, argument }` where `argument` is text the user typed in the
    host. The host never composes or sends messages on its own; reply UI is a later, additive
    method, not part of this contract.

## Consequences

- A hung or malicious helper costs one process and a pipe. It cannot block the tray, the webviews,
  the settings store or an update.
- TDLib-class dependencies (tens of MB, native DLLs) need integration-specific archive limits
  (64 MB / 300 files) rather than the 5 MB data-plugin limit. That is a deliberate exception and
  must be enforced by kind, not by raising the limit for everyone.
- The boundary does not stop a hostile helper from reading user files. Only consent and the
  published hash gate that, which is why the consent screen must say it plainly.
- The closed method set will feel restrictive. That is the same trade as `tool` primitives and the
  auth allowlist: adding a primitive is a reviewed code change with a test, not a manifest field.
- One helper instance per integration in this phase; multiple accounts return when the contract
  carries an account id, and that will be an additive `protocolVersion` change.
- Integration plugins stay out of the official registry until one is real, tested and documented;
  the registry builder gains kind-aware limits but no default integration entry.
