# Integrations (phase 7 design)

**Status:** designed 2026-11-18, not implemented. ADR 0016 is the decision record. Everything
here is a contract to build against; until the phase-7 tasks land, `PluginLoader` refuses the
`process` permission and no helper is started.

An `integration` plugin is an optional helper program that talks to a protocol directly (TDLib for
Telegram is the motivating case). It never runs code in the Chirpdesk process, never receives a
WebView handle, a profile path or the settings store, and cannot add UI. It can report
notifications and unread counts, expose named commands, and own a private data directory.

## Layout

```
plugins/telegram-tdlib/
├─ plugin.json          # kind: integration, permissions, version
├─ integration.json     # executable, protocol version, commands
├─ helper.exe
├─ helper.dll           # optional, next to the executable
└─ icon.svg
```

The whole folder is verified by the signed repository index before extraction. The data-plugin
archive limits (5 MB / 200 files) do not fit native helper binaries; integrations get
64 MB / 300 files, enforced by kind at install time.

## `integration.json`

```json
{
  "protocolVersion": 1,
  "executable": "helper.exe",
  "commands": [
    {
      "id": "open",
      "title": "Open Telegram",
      "description": "Bring the helper's main window to the front",
      "argument": "none"
    },
    {
      "id": "send",
      "title": "Telegram: send",
      "description": "Send text to the active chat",
      "argument": "text"
    }
  ]
}
```

| Field | Required | Rules |
|---|---|---|
| `protocolVersion` | yes | `1`; the host refuses a different value |
| `executable` | yes | plain file name ending in `.exe`; no separators, no `..`; must exist after extraction |
| `commands` | no | at most 20; `id` matches `^[a-z0-9][a-z0-9-]{0,31}$`, unique; `title` ≤ 40 chars; `description` ≤ 120; `argument` is `none` or `text` |

The executable is resolved inside the plugin folder. Anything that is not a plain file name is a
manifest error, not a failed launch.

## Permissions

Declared in `plugin.json`; only `integration` manifests may use this set.

| Permission | Grants |
|---|---|
| `process` | required; run the helper executable |
| `notifications` | call `notify` and raise a Windows toast |
| `unread` | call `unread` and set the service badge |
| `commands` | declare command-bar entries and receive `command` |

`commands` without `process` is a manifest error. `notifications` or `unread` without `process` is
also an error: a helper exists to do the work.

## Process and transport

The host starts the executable directly with no arguments, working directory set to the plugin
folder, and these environment variables:

```
CHIRPDESK_PROTOCOL=1
CHIRPDESK_PIPE=\\.\pipe\chirpdesk-integration-<guid>
CHIRPDESK_TOKEN=<32 random bytes, base64url>
CHIRPDESK_DATA=%APPDATA%\Chirpdesk\integrations\<id>
```

The data directory is created before launch and is the only path the host supplies. The process is
placed in a Job Object with kill-on-close. Standard output and standard error are redirected to
NUL, never captured: a helper that prints a token or a message body must not leak it into the
host's log. Diagnostics go through the `log` method, which the host sanitises and caps.

### Framing

One message per frame: 4-byte little-endian unsigned length, then UTF-8 JSON. Maximum frame is
256 KiB; a larger frame stops the helper with a logged error. No newline framing, no compression.

### Handshake

Within 5 seconds of launch the helper must connect to the pipe and send:

```json
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1,"token":"...","pluginVersion":"1.0.0","appVersion":"0.2.1"}}
```

The host compares the token in constant time, checks `protocolVersion`, and answers:

```json
{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":1,"appVersion":"0.2.1","permissions":["notifications","unread"],"dataDirectory":"C:\\...\\integrations\\telegram-tdlib"}}
```

A bad token, wrong version or timeout stops the helper and records `error` with a reason. The
helper never gets a second attempt on the same pipe; a restart is a new launch and a new token.

## Methods

### Helper → host

| Method | Params | Permission | Notes |
|---|---|---|---|
| `notify` | `title` (≤64), `body` (≤512), `tag` (≤64, optional), `silent` (bool, optional), `activateCommand` (optional) | `notifications` | One toast per call. `activateCommand` must be a declared command id; otherwise the toast simply has no action. |
| `unread` | `count` (0–9999) | `unread` | Sets the integration's badge contribution. `0` clears it. |
| `status` | `state` (`ready` \| `busy` \| `error`), `detail` (≤200, optional) | none | Shown on the plugin card; `detail` must not contain message text. |
| `log` | `level` (`info` \| `warn` \| `error`), `message` (≤500) | none | One line; newlines are stripped. Notification bodies are never logged (invariant 4). |

Unknown methods are rejected with JSON-RPC `-32601`, counted, and after five in one session the
helper is stopped. `notify` without the permission is `-32603` and is not retried.

### Host → helper

| Method | Params | Notes |
|---|---|---|
| `ping` | none | Liveness; 2 s timeout |
| `command` | `id`, `argument` (≤500 when the command takes text) | Only a declared id is accepted; 10 s timeout; result `{ "ok": true, "message": "..." }` or `{ "ok": false, "message": "..." }` |
| `shutdown` | none | Sent on stop, then 3 s grace before kill |

There is no generic `invoke`, no reflection, and no host callback that exposes a path or a handle.
Every method and argument is validated before it is dispatched.

## Lifecycle

```
disabled ──enable──▶ starting ──initialize──▶ running
   ▲                    │                      │
   │                    └──timeout/error──▶ error ◀──crash (backoff)
   │                                           │
   └──────────────disable──────────────────────┘
```

- The helper starts when the plugin is enabled and the app is running. It stops on disable,
  uninstall, update and app exit.
- A crash restarts after 1 s, 5 s and 30 s, then stays in `error` with a manual **Start** button.
- Update stops the helper before the folder is replaced; the new version starts only after the
  user confirms the new consent screen when executable, permissions or commands changed.
- The app never waits more than 2 s for a helper during exit.
- One instance per integration in this phase. Multiple accounts need an account id in the
  contract and an additive protocol version.

## Consent

Installing an integration shows the normal plugin details sheet, then a second screen:

- executable name and SHA-256, repository key fingerprint, plugin version;
- the permissions in plain words, and the declared commands;
- the data folder path, and that the helper can use the network;
- the statement that it runs with the user's own file access and is not sandboxed by Chirpdesk;
- a checkbox that must be ticked before **Install** enables.

Updates that change the executable hash, the permission set or the command list require the
second screen again. Uninstall offers to move the helper's data directory to the Recycle Bin,
default on, because it may contain session tokens.

## Storage and logging

- Helper data: `%APPDATA%\Chirpdesk\integrations\<id>\`, created by the host, owned by the helper.
- The host never passes profile, settings, log or plugin-root paths.
- Logs record lifecycle transitions, `status`, and helper `log` lines. Toast titles and bodies are
  never written to the log.
- The executable hash recorded at install is re-checked before every launch; a mismatch stops the
  plugin with "helper changed on disk".

## Testing

- **Core unit tests:** frame codec (round trip, oversized, truncated), JSON-RPC id matching,
  method allowlist, permission gating, `integration.json` validation, executable name rules,
  kind-aware archive limits, consent-change comparison.
- **Integration category:** a stub helper process exercises handshake, notify to a fake toast
  sink, unread, command round-trip, timeout, kill, and restart backoff. This is the only place a
  real helper process is launched in tests.
- **Manual matrix:** install from a test repo, consent, run, toast and badge, crash/restart,
  stop/start, disable/enable, update with changed permissions, tampered executable refusal,
  uninstall and data deletion.

## First integration (TDLib)

The helper owns TDLib, its native DLLs, the API id/hash configuration, the session database and
the authentication flow. Chirpdesk only receives `notify`, `unread`, `status` and `log`, and sends
declared commands. The helper is user-installed from a repository, never bundled, and its license
and redistribution terms must be checked before publishing.

## Non-goals

- In-process execution of any plugin code.
- Host filesystem, registry, profile or settings access for a helper.
- Plugin-defined UI beyond command-bar entries and toasts.
- Inline toast reply and message history in the host (possible later, as an additive method).
- Bundled or official-registry integrations until one is real and tested.
