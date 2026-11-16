# Plugin format

Everything extensible in Chirpdesk is a plugin: a folder containing `plugin.json` plus its payload. There is no second mechanism.

See `PLAN.md` §4.1 for the reasoning and `docs/adr/0007-plugin-architecture.md` for the decision record.

## 1. Kinds

| `kind` | Payload file | Contains code | Trust tier |
|---|---|---|---|
| `service` | `service.json` (+ `icon.*`, optional `skin.css`) | no | bundled / user data |
| `theme` | `theme.json` (+ `preview.png`) | no | bundled / user data |
| `filter` | `filters.json` | no | bundled / user data |
| `integration` | executable + `integration.json` | **yes** | user only, explicit consent, out-of-process |

v1 ships `service`, `theme`, `filter`. `integration` exists in the schema from day one so the manifest does not have to change later, but nothing loads it until phase 6.

## 2. Folder layout

```
plugins/whatsapp/
├─ plugin.json
├─ service.json
├─ icon.svg
└─ skin.css            # optional

plugins/theme-catppuccin-mocha/
├─ plugin.json
├─ theme.json
└─ preview.png

plugins/filters-meta/
├─ plugin.json
└─ filters.json
```

Bundled: `<install>\plugins\`. User: `%APPDATA%\Chirpdesk\plugins\`. Both scanned identically; user plugins override a bundled plugin with the same `id` (and the manager says so).

## 3. `plugin.json`

| Field | Type | Required | Notes |
|---|---|---|---|
| `id` | string | yes | `^[a-z0-9][a-z0-9-]{1,31}$`, unique, immutable across versions |
| `kind` | enum | yes | `service` \| `theme` \| `filter` \| `integration` |
| `name` | string | yes | display name, 1–40 chars |
| `version` | semver | yes | plugin's own version |
| `apiVersion` | integer | yes | `1` for this document |
| `author` | string | no | shown in the manager |
| `homepage` | string | no | https only |
| `description` | string | no | one line, 1–160 chars |
| `permissions` | string[] | yes | see §6; empty array allowed |
| `assets` | string[] | no | relative paths only, no `..`, no absolute |

```json
{
  "id": "theme-catppuccin-mocha",
  "kind": "theme",
  "name": "Catppuccin Mocha",
  "version": "1.0.0",
  "apiVersion": 1,
  "author": "community",
  "description": "Soothing pastel theme for the high-spirited.",
  "permissions": [],
  "assets": ["preview.png"]
}
```

## 4. Payloads

### `service.json`

```json
{
  "url": "https://web.whatsapp.com/",
  "icon": "icon.svg",
  "accent": "#25D366",
  "unreadRegex": "\\((\\d+)\\)",
  "userAgent": null,
  "skin": "skin.css",
  "filter": "filters-meta",
  "shareProfileWith": null,
  "warmAtBoot": true
}
```

`url` must be `https`. `unreadRegex` must have exactly one capture group; validated by running it against the service's own name as a smoke test. `filter` refers to a `filter` plugin id, not a path.

### `theme.json`

```json
{
  "kind": "dark",
  "tokens": {
    "bg.window":   "#241c22",
    "bg.surface":  "#2b2129",
    "bg.elevated": "#342832",
    "bg.scrim":    "#000000B3",
    "border.default": "#423440",
    "border.focus":   "#f5a7d0",
    "text.primary":   "#f2e9f0",
    "text.secondary": "#c6b6c4",
    "text.muted":     "#8d7d8b",
    "accent.default": "#f5a7d0",
    "accent.fg":      "#241c22",
    "state.danger":   "#f28b82",
    "state.success":  "#8fd694",
    "state.warning":  "#f2c66d",
    "shadow.overlay": "#00000066"
  }
}
```

Every token in `PLAN.md` §12.1 is required. Contrast is validated at load (WCAG AA); failures are nudged and reported, not rejected, so a badly-tuned community theme still works.

### `filters.json`

```json
{
  "blockHosts": ["graph.facebook.com", "*.fbcdn.net/tr"],
  "allowHosts": ["web.whatsapp.com", "*.whatsapp.net"],
  "cosmetic": ["div[aria-label='Sponsored']", "[data-testid='ad-banner']"]
}
```

`allowHosts` in a plugin can only ever add to the global auth allowlist, never remove from it.

## 5. Validation and failure behaviour

1. Parse JSON; schema errors → plugin disabled, reason logged, row flagged in the manager.
2. `apiVersion` higher than supported → disabled with "requires a newer Chirpdesk".
3. Unknown fields → ignored (forward compatibility).
4. Missing required token, bad colour, bad regex, `..` in an asset path → plugin disabled.
5. A disabled plugin never deletes data, never blocks startup, and never takes other plugins with it.
6. `FileSystemWatcher` on the plugin folders triggers a debounced rescan (500 ms); enabling/disabling is instant.

## 6. Permissions

| Permission | Grants | v1 |
|---|---|---|
| `css` | inject a stylesheet into the plugin's own service | yes |
| `notifications` | receive the service's notification events | yes |
| `filters` | contribute host rules | yes |
| `storage` | own a profile directory | bundle-only |
| `inject-js` | run JavaScript in the page origin | **declared only, not offered** — see below |
| `process` | start a helper executable | integration only, phase 6 |

**`inject-js` is not available in v1.** Page-context JavaScript has access to the authenticated session of that service, so a "plugin" with it is a credential-stealer by construction. It stays in the schema for planning, unimplemented, and the loader refuses to load any plugin that requires an unimplemented permission.

## 7. Security rules

- Data plugins cannot execute code. There is no reflection, no script host, no expression evaluator.
- `skin.css` and cosmetic CSS are sanitised: `@import` removed, remote `url()` removed, only local `assets` may be referenced. This prevents a theme/plugin from beaconing to a third party.
- Integrations (phase 6) run as separate processes, launched with an explicit manifest-declared command, JSON-RPC over a named pipe, per-call timeout, killed on exit or unresponsiveness. They never get a webview handle or the settings store.
- A plugin id may not shadow a reserved id (`core`, `settings`, `command-bar`).

## 8. Versioning

- `apiVersion` is an integer, bumped only for breaking changes.
- Within an `apiVersion`, changes are additive; unknown fields are ignored.
- `schemas/*.schema.json` is the source of truth and is what the loader validates against; the docs follow the schema, not the other way round.

## 9. Plugin repositories

A repo is a static HTTPS directory. Ours is `registry/` in this repository; a community repo is the same two files anywhere on the web.

```
registry/
├─ index.json          # schema:1, name, generated, keyFingerprint, plugins[]
└─ index.json.sig      # Ed25519 signature over the exact bytes of index.json
```

Each `plugins[]` entry carries `id`, `kind`, `version`, `apiVersion`, `minAppVersion`, `sha256`, `size`, `url`, `permissions`. The archive at `url` is a zip of one plugin folder.

**Install, in order, aborting on any failure:**

1. `GET index.json` + `.sig` over HTTPS.
2. Verify signature against the repo's pinned key.
3. Skip entries whose `apiVersion` or `minAppVersion` do not fit the running build.
4. Download archive → verify `sha256` from the **signed** index → verify `size`.
5. Unpack to temp, validate `plugin.json` + payload against schema, then move into `%APPDATA%\Chirpdesk\plugins\<id>\`.
6. Enable only if the user asked; otherwise installed-but-disabled.

**Pinning.** First fetch pins the key fingerprint (TOFU). If the fingerprint changes later, the repo is blocked and shown in the manager with both fingerprints; only an explicit re-trust unblocks it. This is deliberate friction: it is the only defence against a repo being taken over.

**Limits.** ≤ 5 MB and ≤ 200 files per data plugin; no executables; no absolute paths or `..`; `integration` kind is not installable in v1 and, when it arrives, needs a second consent screen listing the executable and commands.

**Offline.** Index cached for 24 h; installed plugins always work offline; sync is on demand by default and can be enabled per repo.

**Conflicts.** Two repos offering the same `id`: the manager shows both sources and requires a choice; the choice is recorded per id, not globally.

## 10. Commands

`> plugins` opens the manager (list, enable/disable, reveal folder, reasons for disabled) · `> plugin: disable <id>` · `> theme <id>` · `> reload plugins` · `> repo add <url>` · `> repo list` · `> repo remove <name>` · `> repo sync`.
