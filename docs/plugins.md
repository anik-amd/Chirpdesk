# Plugin format

Every **distributable content pack** is a plugin: a folder containing `plugin.json` plus its payload. Service **instances** — the sites you add — are settings, not plugins. See `PLAN.md` §4.1 and `docs/adr/0007-plugin-architecture.md`.

## 1. Kinds

| `kind` | Payload file | Contains code | Trust tier |
|---|---|---|---|
| `service` | `service.json` (definition: url, icon, regex, CSS, filter ref) | no | bundled / repo |
| `theme` | `theme.json` with `variants[]` (+ `preview.png`) | no | bundled / repo |
| `filter` | `filters.json` | no | bundled / repo |
| `tool` | `panel.json` (declarative, host-rendered) | no | **bundled only in v1** |
| `integration` | executable + `integration.json` | **yes** | user only, explicit consent, out-of-process |

v1 ships `service`, `theme`, `filter` and `tool`. `integration` exists in the schema from day one so the manifest does not have to change later, but nothing loads it until phase 7.

**Instances are not plugins.** A service you add by URL lives in `settings.json` as `{instanceId, definitionId|null, url, name, …}` with a `profileId` that defaults to the instance id. `definitionId: null` means a custom site; no folder is written, and the plugin loader is not involved. This is why "add any site" is a built-in flow rather than a template plugin.

## 2. Folder layout

```
plugins/whatsapp/
├─ plugin.json
├─ service.json
├─ icon.svg
└─ skin.css            # optional

plugins/theme-catppuccin/     # ONE folder per family, all variants inside
├─ plugin.json
├─ theme.json
└─ preview.png

plugins/filters-meta/
├─ plugin.json
└─ filters.json

plugins/tool-cleaner/
├─ plugin.json
├─ panel.json
└─ icon.svg
```

Bundled: `<install>\plugins\`. User: `%APPDATA%\Chirpdesk\plugins\`. Both scanned identically; user plugins override a bundled plugin with the same `id` (and the manager says so).

## 3. `plugin.json`

| Field | Type | Required | Notes |
|---|---|---|---|
| `id` | string | yes | `^[a-z0-9][a-z0-9-]{1,31}$`, unique, immutable across versions |
| `kind` | enum | yes | `service` \| `theme` \| `filter` \| `tool` \| `integration` |
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
  "id": "theme-catppuccin",
  "kind": "theme",
  "name": "Catppuccin",
  "version": "1.0.0",
  "apiVersion": 1,
  "author": "community",
  "description": "Soothing pastel themes for the high-spirited.",
  "permissions": [],
  "assets": ["preview.png"]
}
```

## 4. Payloads

### `service.json` (definition)

```json
{
  "url": "https://web.whatsapp.com/",
  "icon": "icon.svg",
  "accent": "#25D366",
  "unreadRegex": "\\((\\d+)\\)",
  "notificationMode": "native",
  "skin": "skin.css",
  "filter": "filters-meta",
  "defaultWarmAtBoot": true
}
```

`url` must be `https`. `unreadRegex` must have exactly one capture group; validated by running it against the service's own name as a smoke test. `filter` refers to a `filter` plugin id, not a path. `notificationMode` is `native` \| `shim` \| `title-only` (see `PLAN.md` §6); a definition may omit it and the host probes at first load.

A definition is shared; an instance overrides `url`, `name`, `iconUri`, `accent`, `unreadRegex`, `userAgent`, `injectCss`, `warmAtBoot`, `mute`, `blocking`, `notificationMode` and `shareProfileWith`. A custom site has `definitionId: null` and no plugin at all.

### `theme.json`

One file holds **every variant of a family**. Dark and light are variants, never separate plugins.

```json
{
  "family": "Catppuccin",
  "defaults": { "dark": "mocha", "light": "latte" },
  "variants": [
    {
      "id": "mocha",
      "name": "Mocha",
      "mode": "dark",
      "tokens": {
        "bg.window":   "#1e1e2e",
        "bg.surface":  "#181825",
        "bg.elevated": "#313244",
        "bg.scrim":    "#000000B3",
        "border.default": "#45475a",
        "border.focus":   "#cba6f7",
        "text.primary":   "#cdd6f4",
        "text.secondary": "#bac2de",
        "text.muted":     "#7f849c",
        "accent.default": "#cba6f7",
        "accent.fg":      "#1e1e2e",
        "state.danger":   "#f38ba8",
        "state.success":  "#a6e3a1",
        "state.warning":  "#f9e2af",
        "shadow.overlay": "#00000066"
      }
    },
    {
      "id": "latte",
      "name": "Latte",
      "mode": "light",
      "tokens": { "bg.window": "#eff1f5", "text.primary": "#4c4f69", "...": "..." }
    }
  ]
}
```

Validation rules:

- `id` per variant: `^[a-z0-9-]{1,24}$`, unique within the plugin.
- `mode`: `dark` or `light`. Every token in `PLAN.md` §12.1 is required in every variant.
- `defaults.dark` / `defaults.light` must reference a variant of that mode. A family with only dark variants may omit `defaults.light`.
- Contrast is validated at load (WCAG AA); failures are nudged by the minimum passing amount and reported, not rejected, so a badly-tuned community theme still works. The nudge loop is capped at 20 iterations.
- A theme plugin that fails validation is disabled; the shell falls back to its compiled **baseline theme**, which is not a plugin and cannot be removed.

### `filters.json`

```json
{
  "blockHosts": ["graph.facebook.com", "*.fbcdn.net"],
  "blockPaths": ["*/tr/", "*/ads/*"],
  "allowHosts": ["web.whatsapp.com", "*.whatsapp.net"],
  "cosmetic": ["div[aria-label='Sponsored']", "[data-testid='ad-banner']"]
}
```

`blockHosts` and `allowHosts` are host patterns only; paths belong in `blockPaths`. `allowHosts` in a plugin can only ever add to the global auth allowlist, never remove an entry, and the service's own origin is allowlisted by the host regardless of any pack.

### `panel.json` — the `tool` kind

Tools are how a bundled panel such as the desk cleaner is data instead of XAML. They are **bundled only in v1**; a user-installed tool cannot add capability, so it is not offered. A tool declares a panel; the **host** renders it from a fixed primitive vocabulary and executes a fixed action vocabulary. There is no XAML, no script, no file paths, and no arbitrary command.

```json
{
  "title": "Disk cleaner",
  "subtitle": "Reclaim space used by your chat apps.",
  "permissions": ["read-storage", "trim-cache"],
  "sections": [
    {
      "type": "table",
      "id": "usage",
      "columns": [
        { "key": "service", "label": "Service", "width": "star" },
        { "key": "data",    "label": "Data",    "width": "auto" },
        { "key": "cache",   "label": "Cache",   "width": "auto" }
      ],
      "source": { "action": "storageReport" },
      "emptyText": "No services yet."
    },
    {
      "type": "text",
      "id": "note",
      "value": "Cookies, Local Storage, IndexedDB and Service Worker are never touched by a trim."
    }
  ],
  "actions": [
    {
      "id": "trim",
      "label": "Trim caches",
      "primitive": "trimCache",
      "style": "primary",
      "args": { "budgetMb": 60, "targets": "selected" },
      "confirm": "Trim caches for the selected services? You stay signed in."
    },
    {
      "id": "scan",
      "label": "Scan for leftovers",
      "primitive": "scanOrphans",
      "style": "secondary"
    },
    {
      "id": "reveal",
      "label": "Open folder",
      "primitive": "revealFolder",
      "style": "ghost",
      "args": { "root": "profiles" }
    }
  ]
}
```

**Panel primitives** (v1, closed set): `list`, `table`, `text`, `progress`, `button`, `separator`.

**Action primitives** (v1, closed set — the host implements each one, the plugin only picks and parameterises):

| Primitive | Effect | Argument validation |
|---|---|---|
| `storageReport` | returns per-service data/cache sizes (no separate "media" total — that lives inside IndexedDB) | none |
| `trimCache` | trims cache only, never session data, only for disposed profiles | `budgetMb` 1–2000, `targets` = `selected` \| `all` \| one instance id |
| `scanOrphans` | scans allowlisted roots for leftover profiles | none |
| `deleteOrphan` | moves a scanned orphan to the Recycle Bin | `orphanId` must come from the latest scan result |
| `revealFolder` | opens Explorer at a known root | `root` ∈ {`profiles`, `plugins`, `logs`, `data`} — never a path |
| `openSettings` | navigates to a settings section | `section` from a fixed enum |

Plugin management (`enablePlugin`, `syncRepo`) is **not** a tool action: the plugin manager is built-in shell UI and a plugin cannot manage the system that loaded it.

Rules:

- A plugin never supplies a filesystem path. It names a **root**; the host resolves it from its own allowlist. This is what keeps invariant 2 ("never delete outside the allowlisted roots") true even for third-party tools.
- An unknown primitive or an argument that fails validation disables the **action**, logs it, and renders the button disabled with the reason — the panel still opens. Fail open, per invariant 8.
- `deleteOrphan` can only delete an id produced by a scan in this session; ids are not persisted or guessable.
- Actions run through the same permission check as the rest of the plugin system (§6); a missing permission greys the button and says which permission is needed.

## 5. Validation and failure behaviour

1. Parse JSON; schema errors → plugin disabled, reason logged, row flagged in the manager.
2. `apiVersion` higher than supported → disabled with "requires a newer Chirpdesk".
3. Unknown fields → ignored (forward compatibility).
4. Missing required token, bad colour, bad regex, `..` in an asset path, duplicate variant id, `defaults` pointing at a missing variant → plugin disabled.
5. `tool`: unknown primitive or an unvalidatable argument disables that action only, not the panel (§4). A user-installed `tool` is refused in v1 with "bundled tools only".
6. A disabled plugin never deletes data, never blocks startup, and never takes other plugins with it.
7. `FileSystemWatcher` on the plugin folders triggers a debounced rescan (500 ms); enabling/disabling is instant.

## 6. Permissions

| Permission | Grants | v1 |
|---|---|---|
| `css` | inject a stylesheet into the plugin's own service | yes |
| `notifications` | receive the service's notification events | yes |
| `filters` | contribute host rules | yes |
| `tools` | declare a `panel.json` and its primitives | bundle-only |
| `read-storage` | read per-service size reports | bundle-only |
| `trim-cache` | request cache trims through `trimCache` | bundle-only |
| `storage` | own a profile directory | bundle-only |
| `inject-js` | run JavaScript in the page origin | **declared only, not offered** — see below |
| `process` | start a helper executable | integration only, phase 7 |

**`inject-js` is not available in v1.** Page-context JavaScript has access to the authenticated session of that service, so a "plugin" with it is a credential-stealer by construction. It stays in the schema for planning, unimplemented, and the loader refuses to load any plugin that requires an unimplemented permission. The built-in notification shim and title capture are host code, not a plugin permission.

## 7. Security rules

- Data plugins cannot execute code. There is no reflection, no script host, no expression evaluator.
- `skin.css` and cosmetic CSS are sanitised: `@import` removed, remote `url()` removed, only local `assets` may be referenced. This prevents a theme/plugin from beaconing to a third party.
- Integrations (phase 7) run as separate processes, launched with an explicit manifest-declared command, JSON-RPC over a named pipe, per-call timeout, killed on exit or unresponsiveness. They never get a webview handle or the settings store.
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
└─ index.json.sig      # ECDSA P-256 signature over the exact bytes of index.json
```

Each `plugins[]` entry carries `id`, `kind`, `version`, `apiVersion`, `minAppVersion`, `sha256`, `size`, `url`, `permissions`. The archive at `url` is a zip of one plugin folder.

**Install, in order, aborting on any failure:**

1. `GET index.json` + `.sig` over HTTPS.
2. Verify signature against the repo's pinned key.
3. Skip entries whose `apiVersion` or `minAppVersion` do not fit the running build.
4. Download archive → verify `sha256` from the **signed** index → verify `size`.
5. Unpack to temp: reject absolute paths, `..`, reparse points and zip-slip entries; validate `plugin.json` + payload against schema; then move into `%APPDATA%\Chirpdesk\plugins\<id>\`.
6. Enable only if the user asked; otherwise installed-but-disabled.

**Pinning.** First fetch pins the key fingerprint (TOFU). If the fingerprint changes later, the repo is blocked and shown in the manager with both fingerprints; only an explicit re-trust unblocks it. This is deliberate friction: it is the only defence against a repo being taken over.

**Limits.** ≤ 5 MB and ≤ 200 files per data plugin; no executables; no absolute paths or `..`; `integration` kind is not installable in v1 and, when it arrives, needs a second consent screen listing the executable and commands.

**Offline.** Index cached for 24 h; installed plugins always work offline; sync is on demand by default and can be enabled per repo.

**Conflicts.** Two repos offering the same `id`: the manager shows both sources and requires a choice; the choice is recorded per id, not globally.

## 10. Commands

`> plugins` opens the manager (list, enable/disable, reveal folder, reasons for disabled) · `> plugin: disable <id>` · `> theme <family>` · `> theme <family> <variant|auto>` · `> add any site` · `> reload plugins` · `> repo add <url>` · `> repo list` · `> repo remove <name>` · `> repo sync`.
