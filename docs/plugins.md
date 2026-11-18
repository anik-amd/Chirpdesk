# Plugin format and authoring guide

Every **distributable content pack** is a plugin: a folder containing `plugin.json` plus its payload. A **site** is a `service` plugin (a definition); running it is a settings **instance**. There is no URL add flow — plugins are the only way a site enters the app (ADR 0011).

This document is the authoring contract for humans and AI agents. It describes v1, which is **data only**: no plugin executes code in the app process.

> **Implementation status (2026-11-17).** Service plugins load from folders: the five bundled definitions live in the repo's `plugins/` directory and are copied beside the executable; user plugins live in `%APPDATA%\Chirpdesk\plugins\<id>\`. Startup scanning, manifest validation, fail-open skipping and user-over-bundled shadowing are implemented. Not built yet: the plugin manager, repo install/update, and the `FileSystemWatcher` rescan. `PluginLoader` and `DefinitionValidator` are the source of truth.

## 1. Model: plugin vs instance

| Thing | Lives in | Shared? | Created by |
|---|---|---|---|
| Definition (site behaviour) | `plugins/<id>/` (bundled or user) | yes, versioned | plugin install |
| Instance (one account) | `settings.json` | no | selecting an installed definition |

- A definition knows the site: URL, unread regex, notification mode, whether the page has its own dark theme.
- An instance is one profile, one unread count, one warm/cold state. Two accounts of one site are two instances of one definition.
- A row with `"definitionId": null` and an inline `url` is **legacy**: it still resolves, but nothing creates new ones.
- Bundled plugins live in `<install>\plugins\`; user plugins in `%APPDATA%\Chirpdesk\plugins\<id>\`. Both are scanned identically; a user plugin with the same `id` shadows the bundled one.
- Invalid plugins are skipped and logged, never fatal (invariant 8).

### 1.1 Multiple accounts and sibling sites

Multiple accounts are a first-class case, not a workaround:

- Install one definition once; create **one instance per account**. Each instance gets its own `instanceId`, its own profile directory and therefore its own cookies, session, unread count and warm/cold state. Two WhatsApp accounts never see each other's data.
- Instances share the definition, so a definition update (a fixed regex, a dark-mode correction) reaches every account without duplicating plugin folders.
- Profiles are per **instance**, not per browser: signing into a second account does not disturb the first one's socket.

**Sibling sites are separate definitions.** Messenger and Facebook belong to the same vendor but are different entry points, so they ship as `messenger` (`https://www.messenger.com/`) and `facebook` (`https://www.facebook.com/`) — never one plugin with a "mode". Opening Messenger opens the messenger client; opening Facebook opens the Facebook client. Each supports multiple accounts the same way, because accounts are instances of the definition.

Current bundled service plugins: `whatsapp`, `messenger`, `telegram`, `facebook`, `instagram`.

> Creating instances is still a settings/Command Bar action to be built in phase 2. Until then, instances exist as rows in `settings.json`; a definition alone never creates one.

## 2. Kinds

| `kind` | Payload file | Code | Trust tier | v1 |
|---|---|---|---|---|
| `service` | `service.json` (definition) | no | bundled / repo | yes |
| `theme` | `theme.json` with `variants[]` (+ `preview.png`) | no | bundled / repo | after v1 (ADR 0010) |
| `filter` | `filters.json` | no | bundled / repo | phase 3 |
| `tool` | `panel.json` (host-rendered primitives) | no | **bundled only** | phase 2 |
| `integration` | executable + `integration.json` | **yes** | explicit consent, out-of-process | phase 7, never in-process |

## 3. Folder layout

```
plugins/example-chat/
├─ plugin.json
├─ service.json
├─ icon.svg            # optional, file name only
└─ README.md           # optional, shown in the manager

plugins/theme-catppuccin/     # one folder per family, all variants inside
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

No absolute paths, no `..`, no path separators in asset fields. A plugin folder is flat unless `assets` names a relative file in it.

## 4. `plugin.json` (manifest)

Every kind has one.

| Field | Type | Required | Rules |
|---|---|---|---|
| `id` | string | yes | `^[a-z0-9][a-z0-9-]{0,31}$`; unique; immutable across versions; the definition id must match |
| `kind` | enum | yes | `service` \| `theme` \| `filter` \| `tool` \| `integration` |
| `name` | string | yes | display name, 1–40 chars |
| `version` | semver | yes | the plugin's own version, e.g. `1.0.0` |
| `apiVersion` | integer | yes | `1`; the host refuses a higher number |
| `author` | string | no | shown in the manager |
| `homepage` | string | no | absolute `https` |
| `description` | string | no | one line, ≤ 160 chars; shown as the plugin's usage hint on the Plugins page |
| `accent` | string | no | brand hex colour used by the service icon and palette row |
| `permissions` | string[] | no | empty when omitted; see §9 |
| `assets` | string[] | no | relative file names only, no `..` |

```json
{
  "id": "example-chat",
  "kind": "service",
  "name": "Example Chat",
  "version": "1.0.0",
  "apiVersion": 1,
  "author": "you",
  "description": "Example Chat web client.",
  "permissions": []
}
```

## 5. `service.json` — the site definition

This is the file to write when adding a site. Field names are camelCase; JSON comments and trailing commas are tolerated by the loader, but ship plain JSON. The manifest is canonical: `id` and `name` may be omitted here and are filled from `plugin.json`; if present, `id` must match and `name` wins.

```json
{
  "url": "https://chat.example.com/",
  "icon": "icon.svg",
  "unreadRegex": "\\((\\d+)\\)",
  "aliases": ["ec"],
  "notificationMode": "native",
  "pageDarkMode": "auto",
  "apiVersion": 1,
  "minApiVersion": 1
}
```

| Field | Required | Rules | Applied today |
|---|---|---|---|
| `id` | no | if present, must equal the manifest id; otherwise the manifest id is used | yes |
| `name` | no | if present it wins; otherwise the manifest name is used | yes |
| `description` | no | if present it wins; otherwise the manifest description is the Plugins-page usage hint | plugins page |
| `url` | yes | absolute `https`, no credentials (`user:pass@`) | yes |
| `icon` | no | bare file name; no `/`, `\` or `..` | validated, not rendered yet |
| `unreadRegex` | no | valid .NET regex; first capture group is the count; default `\((\d+)\)` | yes |
| `aliases` | no | at most 16 short search aliases, each ≤ 32 chars (e.g. `wa`) | yes, Ctrl+K |
| `notificationMode` | no | `native` \| `shim` \| `title-only`; see §5.2 | yes |
| `pageDarkMode` | no | `off` \| `auto`; see §5.1 | yes |
| `userAgent` | no | user-agent override string | accepted, not applied yet |
| `activation` | no | notification click routing, see §5.3 | validated, routing later |
| `apiVersion` | no | must be `1` | yes |
| `minApiVersion` | no | `1` … `apiVersion` | yes |

**Typos fail open, silently.** `notificationMode` and `pageDarkMode` are lower-cased and an unknown value is normalised to the default (`native`, `off`) before validation. If your setting "did nothing", check spelling first.

`unreadRegex` is compiled with a 100 ms timeout; a regex that does not compile rejects the whole definition. There is no group-count check, so a pattern with no capture group simply yields 0.

### 5.1 Page dark mode (definitions own this)

The app sets the profile's `prefers-color-scheme` to match the app theme before navigation. On top of that, the definition chooses whether the host may darken a page that ignores it:

- `off` — never touch the page. Use this when the site has its own dark theme (WhatsApp, Telegram). The site reacts to `prefers-color-scheme` itself.
- `auto` — let the host measure the rendered page and apply an inverting filter **only if it is still light**. The measurement waits for an opaque background, re-checks for ~10 s, and yields to a theme that arrives after hydration.

Darkening only ever happens while the app itself is dark. The bundled `page-dark` **theme plugin** is the master switch: deactivate it and no page is inverted, whatever a definition or instance says. Bundled plugins are active by default; the Plugins page toggles it. A per-instance override of `on` or `off` is allowed, but `on` still obeys the app theme.

Do not set `auto` for a site known to follow the OS scheme: the host would only be second-guessing it. Never try to express dark mode as JavaScript; page-context JS is not a plugin permission (§9).

The app never guesses from a URL: `pageDarkMode` is explicit data, and the user may override it per instance.

### 5.2 Notification mode

| Mode | What it means |
|---|---|
| `native` | the page's `new Notification()` reaches WebView2's `NotificationReceived` |
| `shim` | host script routes `ServiceWorkerRegistration.showNotification` through the page's `Notification` constructor |
| `title-only` | no page notification is expected; the unread title regex raises a generic toast |

Omit it ⇒ `native`. The runtime also logs a capability probe (`permission=… serviceWorker=…`) and always keeps the unread-title fallback as a safety net. Do not invent other values; an unknown one becomes `native`.

### 5.3 Notification activation (optional)

```json
{
  "activation": {
    "urlTemplate": "https://chat.example.com/dm/{tag}",
    "tagPattern": "^[a-z0-9_-]{1,64}$"
  }
}
```

- `urlTemplate` must be absolute `https` and contain the `{tag}` placeholder.
- `tagPattern`, when present, must be a valid regex; a notification tag that does not match opens the site without routing.
- The host substitutes only the tag, never arbitrary notification text. This is what stops a hostile page from steering the resulting URL.

### 5.4 Complete minimal plugin (copy this)

`plugins/example-chat/plugin.json`:

```json
{
  "id": "example-chat",
  "kind": "service",
  "name": "Example Chat",
  "version": "1.0.0",
  "apiVersion": 1,
  "permissions": []
}
```

`plugins/example-chat/service.json`:

```json
{
  "url": "https://chat.example.com/",
  "unreadRegex": "\\((\\d+)\\)",
  "notificationMode": "native",
  "pageDarkMode": "auto",
  "apiVersion": 1,
  "minApiVersion": 1
}
```

That is a complete site plugin. No code, no assets required.

### 5.5 Authoring checklist — for humans and AI agents

1. **Pick an id** matching `^[a-z0-9][a-z0-9-]{0,31}$` and put it in `plugin.json`. If `service.json` also names an id, it must be identical; otherwise omit it.
2. **URL**: absolute `https`, no credentials. No `http`, `file:`, `javascript:` or relative paths.
3. **Unread regex**: find the pattern the site puts in the document title, e.g. `(3) Telegram`. Default `\((\d+)\)` is correct for most sites; write a different one only with evidence, and keep the count in the first group.
4. **Notification mode**: `native` unless you know the site notifies from a service worker (`shim`) or you cannot observe page notifications (`title-only`).
5. **Dark mode**: `off` if the site has its own dark theme (test it: set Windows to dark and look). Otherwise `auto`.
6. **Assets**: bare file names only. Do not reference `../`, a drive letter or a URL.
7. **Validate the JSON** (no comments, no trailing commas is safest) and check every field against §5.
8. **Expect fail-open**: an invalid plugin is skipped and logged (`plugin skipped: …`), the app starts, and existing instances keep their profiles.

**Never** in a plugin: executable code, page-context JavaScript, filesystem paths, host-action names you did not find in §8, or changes to the auth/WebSocket allowlist. There is no permission that grants any of these in v1.

**Done means**: both files parse; the definition id is unique; URL is https; the regex compiles; and a site with its own dark theme is `off`. A useful sanity check before submitting:

```powershell
$json = Get-Content plugins/example-chat/service.json -Raw | ConvertFrom-Json
[regex]::Matches('(3) Example Chat', $json.unreadRegex) | ForEach-Object { $_.Groups[1].Value }
```

## 6. `theme.json`

Theme plugins are deferred until after v1 (ADR 0010); the format is kept here so the schema stays stable. One plugin per family; dark and light are variants of the same plugin.

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
        "bg.window": "#1e1e2e",
        "bg.surface": "#181825",
        "bg.elevated": "#313244",
        "bg.scrim": "#000000B3",
        "border.default": "#45475a",
        "border.focus": "#cba6f7",
        "text.primary": "#cdd6f4",
        "text.secondary": "#bac2de",
        "text.muted": "#7f849c",
        "accent.default": "#cba6f7",
        "accent.fg": "#1e1e2e",
        "state.danger": "#f38ba8",
        "state.success": "#a6e3a1",
        "state.warning": "#f9e2af",
        "shadow.overlay": "#00000066"
      }
    }
  ]
}
```

Validation: variant ids `^[a-z0-9-]{1,24}$`, unique; `mode` is `dark` or `light`; every token in `PLAN.md` §12.1 is present; `defaults.*` reference an existing variant of that mode. Contrast failures are nudged toward AA and reported, not rejected. A broken theme falls back to the compiled baseline.

## 7. `filters.json`

```json
{
  "blockHosts": ["graph.facebook.com", "*.fbcdn.net"],
  "blockPaths": ["*/tr/", "*/ads/*"],
  "allowHosts": ["web.whatsapp.com", "*.whatsapp.net"],
  "cosmetic": ["div[aria-label='Sponsored']", "[data-testid='ad-banner']"]
}
```

`blockHosts` / `allowHosts` are host patterns; paths belong in `blockPaths`. A pack may only **add** to the auth/WebSocket allowlist, never remove from it, and the service's own origin is allowlisted by the host regardless.

Bundled filter packs are active by default. Deactivating one on the Plugins page removes its rules from every warm tab immediately; re-activating restores them. The permanent allowlist and the per-tab `blocking: off` command are independent of any pack.

## 7.1 `themes.json` — palette flavours

A `theme` plugin may ship named flavours. The host maps the known Catppuccin ids to token
dictionaries; an unknown id is simply unavailable and falls back to the compiled palette. A theme
plugin without `themes.json` is a capability plugin such as `page-dark`.

```json
{
  "flavors": [
    { "id": "latte", "name": "Latte", "mode": "light", "swatch": "#EFF1F5" },
    { "id": "mocha", "name": "Mocha", "mode": "dark", "swatch": "#1E1E2E" }
  ]
}
```

`id` is `^[a-z0-9-]{1,24}$`, `mode` is `light` or `dark`, and `swatch` is a six-digit hex colour
used only for the Settings card. Light and dark flavours are chosen separately.

## 8. `panel.json` - the `tool` kind

Tools are bundled only in v1 and cannot add capability. The host renders a closed set of primitives and executes a closed set of actions; there is no XAML, script, path or arbitrary command.

**Panel primitives:** `list`, `table`, `text`, `progress`, `button`, `separator`.

**Action primitives:** `storageReport`, `trimCache`, `scanOrphans`, `deleteOrphan`, `revealFolder`, `openSettings`.

```json
{
  "title": "Disk cleaner",
  "sections": [
    {
      "type": "table",
      "id": "usage",
      "columns": [
        { "key": "service", "label": "Service", "width": "star" },
        { "key": "cache", "label": "Cache", "width": "auto" }
      ],
      "source": { "action": "storageReport" },
      "emptyText": "No services yet."
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
    }
  ]
}
```

Argument rules: `trimCache` takes `budgetMb` 1–2000 and `targets` = `selected` \| `all` \| instance id; `deleteOrphan` only accepts an id from the latest scan; `revealFolder` takes `root` ∈ {`profiles`, `plugins`, `logs`, `data`}, never a path. A plugin never supplies a filesystem path (invariant 2). An unknown primitive or bad argument disables that action, not the panel.

Plugin management is built-in shell UI, never a tool action.

## 9. Permissions

| Permission | Grants | v1 |
|---|---|---|
| `css` | inject a stylesheet into the plugin's own service | yes |
| `notifications` | receive the service's notification events | yes |
| `filters` | contribute host rules | yes |
| `tools` | declare a `panel.json` | bundle-only |
| `read-storage` | read per-service size reports | bundle-only |
| `trim-cache` | request cache trims | bundle-only |
| `storage` | own a profile directory | bundle-only |
| `inject-js` | run JavaScript in the page origin | **declared, never granted** |
| `process` | start a helper executable | integration only, phase 7 |

`inject-js` is not available in v1: page JS can read the authenticated session. The built-in notification shim and theme script are host code, not a plugin permission.

## 10. Validation, lifecycle and failure

1. At startup, scan `<install>\plugins\` then `%APPDATA%\Chirpdesk\plugins\`. A missing folder is fine.
2. Parse `plugin.json`; a schema error disables that plugin, logs the reason and (with the manager) flags it.
3. `apiVersion` above the host's is disabled with "requires a newer Chirpdesk".
4. Unknown fields are ignored (forward compatibility). Unknown kinds and unknown permissions are errors; known non-service kinds are ignored by the service loader.
5. A user plugin shadows a bundled plugin with the same id. Two plugins with one id in the same tier keep the first and report the second.
6. A bad field, bad regex, `..` in an asset path, a missing `service.json` or a definition/manifest id mismatch disables only that plugin.
7. A disabled plugin never deletes data, never blocks startup and never takes other plugins with it.
8. Disabling a service definition leaves existing instances and profiles on disk; they report a missing definition until it is re-enabled.
9. A `FileSystemWatcher` rescan (500 ms debounce) and per-plugin enable/disable arrive with the plugin manager; today changes are picked up at the next start.

## 11. Security rules

- Data plugins cannot execute code. No reflection, no script host, no expression evaluator.
- `skin.css` and cosmetic CSS are sanitised: `@import` removed, remote `url()` removed, only local `assets` may be referenced.
- Integrations (phase 7) run as separate processes, JSON-RPC over a named pipe, with a timeout/`kill`, and never get a webview handle or the settings store.
- A plugin id may not shadow a reserved id (`core`, `settings`, `command-bar`).

## 12. Versioning

- `apiVersion` is an integer, bumped only for breaking changes.
- Within an `apiVersion`, changes are additive and unknown fields are ignored.
- In v1 the C# validators (`PluginLoader`, `DefinitionValidator`) are authoritative. JSON Schema files under `schemas/` ship with repo install (phase 5); until then the docs follow the validator.

## 13. Plugin repositories

A repo is a static HTTPS directory with `index.json`, `index.json.sig` (ECDSA P-256 over the exact index bytes), and `key.pem` (the public SPKI key used for first-use pinning). Each entry carries `id`, `kind`, `version`, `apiVersion`, `minAppVersion`, `sha256`, `size`, `url`, `permissions`. On first sync the app fetches `key.pem`, checks that the index `keyFingerprint` matches it, and stores the key; later syncs use the stored key and block on a mismatch.

Install order, aborting on any failure: fetch index + signature → verify against the pinned repo key → skip incompatible entries → download archive, verify `sha256` **from the signed index** and `size` → unpack rejecting absolute paths, `..`, reparse points and zip-slip → validate `plugin.json` + payload → move into `%APPDATA%\Chirpdesk\plugins\<id>\` → enable only if the user asked.

TOFU pins the repo key on first use; a changed key blocks the repo until the user re-trusts it by fingerprint. The Plugins page has a Repositories section for adding, syncing and removing a repository; installed repository plugins are installed disabled and need an explicit enable.

Build and sign the official registry with:

```powershell
pwsh scripts/build-registry.ps1 `
  -KeyPath D:\keys\chirpdesk-registry.pem `
  -PublicKeyPath D:\keys\chirpdesk-registry.pub.pem `
  -OutDir dist/registry
```

The output contains `index.json`, `index.json.sig`, `key.pem` and one zip per plugin. Publish that folder on any static HTTPS host. Limits: ≤ 5 MB / ≤ 200 files, no executables. Installed plugins work offline; the index is cached for 4 h so a machine that is not always on still refreshes promptly. Two repos offering one id require an explicit choice per id.

## 14. Commands

`> plugins` (manager: list, enable/disable, reveal folder, reasons) · `> plugin: disable <id>` · `> theme <family>` · `> theme <family> <variant|auto>` · `> add service <definition>` · `> reload plugins` · `> repo add <url>` · `> repo list` · `> repo remove <name>` · `> repo sync`.
