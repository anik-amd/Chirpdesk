# Chirpdesk — Technical Plan

**All your conversations.**

Status: **planning complete, implementation not started.**
License: MIT. Repo: private until v1 works.
Target: Windows 10 1809+ / Windows 11, x64 and arm64.

**Document map**

| Doc | Purpose |
|---|---|
| `PLAN.md` (this file) | requirements, architecture, phases, risks |
| `docs/plans/2026-11-16-phase-0-spine.md` | bite-sized TDD task list for phase 0 |
| `docs/testing-strategy.md` | what gets tested, how, and what is deliberately manual |
| `docs/adr/` | one file per irreversible-ish decision |
| `docs/engineering.md` | conventions, commit style, definition of done |

---

## 1. What this is

A tray application that hosts the web versions of chat services in a single window with no permanent chrome, keeps chosen services loaded (and therefore notifying) in the background, turns their web notifications into real Windows toasts, blocks the ads and trackers those sites drag in, and gives you a place to see and reclaim the disk they consume.

It exists because every alternative is an Electron app that ships its own Chromium copy (MultiMessenger: 325 MB, Nexus/Ferdium: ~250–300 MB), and because the rest of the "lightweight" options can't do notifications without a page open.

Design benchmark: **Limusic** (`limusic.app`) — a Tauri/Rust/Svelte desktop client with a near-black neutral base, restrained single-accent palette, no gradients, generous spacing, and themes shared across platforms. We adopt its *design language, theme schema, installer model and CI model*; we do **not** adopt its stack (see §4).

---

## 2. Locked requirements

| # | Requirement | Implication |
|---|---|---|
| R1 | Starts at boot, **tray only** — no window, no taskbar entry | Hidden host window + login-time autostart |
| R2 | Selected services are **warm** at boot and notify even if never opened | Real loaded webviews, background throttling disabled |
| R3 | Services not selected stay **cold** until invoked | Webview created on demand, spinner while loading |
| R4 | Notifications = native Windows toasts, no backlog storm at login | `NotificationReceived` → toast, dedupe, warm-up suppression |
| R5 | **One** command surface for everything; no sidebar, tab strip or menu | Unified Command Bar, zero permanent chrome |
| R6 | `Ctrl+Tab` cycles open services; `Ctrl+P` opens the same bar for search/commands | Single overlay component, two entry modes |
| R7 | Add **any** chat site by URL | Generic service definition, favicon fetch, no recipe catalogue |
| R8 | Isolated session per service (multiple accounts) | Per-service `WebView2` user-data folder |
| R9 | Theme system: popular families, each with dark **and** light variants built in, live switch | Theme plugins with `variants[]`, token pipeline, instant `DynamicResource` swap |
| R10 | Small on disk and RAM | WPF + WebView2, framework-dependent, cache trimming |
| R11 | **Block ads, trackers and junk scripts** in the web clients | Request-level filter engine + cosmetic CSS, fail-open |
| R12 | **See and reclaim disk** used by these chat apps | Per-service size breakdown, safe cleanup, orphan scan |
| R13 | Services, themes, templates, tools and filter packs are **plugins** with one manifest format | Data-only in v1; code plugins out-of-process, later |
| R14 | Ship a **plugin directory** in the repo; users may add **their own plugin repos** | Signed indexes, per-repo trust consent, data-only installs |
| R15 | Shell first, catalogue later | Core ships with: loader, Command Bar, webview host, notification pipeline, storage primitives. Themes/services/tools arrive as plugins |

## 2.1 Non-functional targets and constraints

| Dimension | Target | Why this number |
|---|---|---|
| Command Bar open | < 50 ms perceived, < 120 ms before the row list paints | it is the only UI; latency here is the product |
| Service switch | < 100 ms warm, no reload, no scroll reset | warm webviews are the whole point |
| Toast latency | < 1 s from the site calling `new Notification()` | slower than the site's own toast feels broken |
| Boot to all-warm | < 40 s after login, staggered | earlier than that fights Explorer/AV and loses |
| Idle CPU | < 0.5 % average with 3 warm services | background app must not be felt |
| RAM | shell < 70 MB, 150–400 MB per warm service | per-service cost is the site's, not ours |
| Disk | app < 10 MB, total < 150 MB after a week of 3 warm services | the original complaint |
| Soak | 8 h tray-hidden with sockets alive, zero missed test notifications | R2 is unprovable otherwise |
| Dependencies | ≤ 2 NuGet packages, 0 native libraries | security surface + auditability |
| Build | one command from clean clone on a machine with .NET 8 SDK only | no VS, no SDK installs, no MSIX |

Hard constraints: single developer, ~13 GB free disk, Windows 11 dev machine and Windows 10 compatibility target, no code-signing certificate in v1 (unsigned installer, documented), no backend service ever.

Testability constraint that shapes the code: everything with logic must be injectable and headless — `IClock`, `IToastSink`, `IServiceStore`, `IProcessProbe` — so the test suite never needs a WebView2 or a toast to run. UI and WebView2 code is thin by design and verified manually.

## 3. Non-goals (v1)

- No message sync, no server, no account of any kind. Nothing leaves the machine.
- No native protocol clients in v1 (Telegram on TDLib is a later optional swap).
- No chat-level search inside a service (fragile DOM scraping).
- No general-purpose adblocker for your browser — blocking applies inside Chirpdesk's webviews only.
- No marketing site / landing page.
- No inbound call guarantee (WebRTC works only if the embedded permission flow allows it — see risks).
- No MSIX / Store distribution.

---

## 4. Stack decision

| Candidate | Verdict | Reason |
|---|---|---|
| **WPF + WebView2 (chosen)** | ✅ | Both runtimes already on Windows; ~5 MB app; `CoreWebView2.NotificationReceived` gives page **and** service-worker notifications; direct control of profiles, permissions, background flags and request filtering. One language. ~1.5–2 GB dev disk. |
| Tauri v2 (Limusic's stack) | ✗ | Same WebView2 engine underneath, so no runtime advantage. Its two weakest points are exactly our two critical ones: notification interception and `WebResourceRequested` require raw `webview2-com` COM interop in Rust, and multi-webview-in-one-window sits behind an unstable flag. Costs 3–5 GB dev disk and three languages. |
| WinUI 3 | ✗ | Windows App SDK dependency (~50–80 MB) for no feature we need |
| Electron | ✗ | Defeats the premise (bundles Chromium, 200 MB+) |
| Thunderbird add-on | ✗ | `tabhide` unsupported, one shared cookie jar, no media permissions, no request filtering, Meta "browser unsupported" |

Target framework: `net8.0-windows10.0.19041.0` (WinRT toast projections without extra packages), `UseWPF=true`, published **framework-dependent**.

Third-party dependencies, deliberately few: `Hardcodet.NotifyIcon.Wpf` (MIT, tray) and `Inno Setup` (build-time installer only, not shipped in the app). Filter lists are parsed by our own code.

---

## 4.1 Plugin architecture (cross-cutting)

Everything the app can be extended with is a **plugin**: a folder with a `plugin.json` manifest. This costs little because the payloads were already data.

| Kind | Payload | Code? | Trust tier | Ships in v1 |
|---|---|---|---|---|
| `service` | url, icon, unread regex, optional CSS, filter refs, accent | no | bundled | WhatsApp, Messenger, Telegram, Facebook |
| `template` | a `service` plugin flagged `"template": true` plus `urlTemplate`, `suggestedName` — the "add any site" flow | no | bundled | Any-site, Any-site (mobile) |
| `theme` | `variants[]` with `mode: dark\|light`, `tokens{}`, optional `preview` | no | bundled | Dracula, Catppuccin, Nord, … |
| `filter` | curated host list, cosmetic CSS | no | bundled | tier 1 + cosmetic packs |
| `tool` | declarative `panel.json` (list/table + actions from host primitives) | no | bundled | Disk cleaner, plugin manager, diagnostics |
| `integration` | optional helper executable + JSON-RPC contract (e.g. TDLib for Telegram) | **yes** | user-installed, explicit consent | phase 7 only |

A `template` is not a separate kind in the schema — it is a `service` plugin with `"template": true`, so the add-service flow, the profile machinery and the repo format are shared. Selecting a template instantiates a normal service plugin in the user's plugin folder with a fresh instance id.

**Layout**

```
plugins/<id>/
├─ plugin.json          # id, kind, name, version, apiVersion, author, permissions[]
├─ icon.png|svg
├─ service.json         # kind: service / template
├─ theme.json           # kind: theme    (variants[], each with mode + tokens)
├─ filters.json         # kind: filter   (hosts, cosmetic selectors)
├─ panel.json           # kind: tool     (list/table + host action primitives)
└─ README.md            # optional, shown in the plugin manager
```

Bundled plugins live in the app's install directory; user plugins in `%APPDATA%\Chirpdesk\plugins\<id>\`. Both are scanned the same way, so shipped and user content follow identical rules.

**Lifecycle**

- Scan on startup and on `FileSystemWatcher` change; parse and validate each manifest; a broken plugin is skipped, logged, and marked in the manager — it never prevents the app from starting.
- `apiVersion` mismatch → plugin disabled with a clear message, not a crash.
- Enable/disable per plugin, persisted in settings; disabling a theme falls back to the compiled baseline, disabling a service leaves its profile on disk intact.
- Commands: `> plugins`, `> plugin: disable <id>`, `> theme <id>` (themes are addressable as plugins without exposing the word "plugin" in normal use).

**Why no in-process code plugins**

The app holds every logged-in session. A .NET assembly loaded in-process is full trust: it can read cookies, intercept keystrokes, exfiltrate anything. Assembly-level isolation (`AssemblyLoadContext`) does not sandbox that. Therefore code extensions are out-of-process only: a helper process the app starts, talking JSON-RPC over a named pipe, with a manifest-declared command surface, a timeout, and no shared memory. A hung or malicious helper can be killed and cannot touch the tray, the webviews or the settings store.

**What "plugin" does not buy**

Plugins do not invent UI. A `tool` plugin declares a panel in `panel.json` using **host primitives only** — `list`, `table`, `progress`, `text`, `button` — and may invoke a fixed set of **host actions** (`scanOrphans`, `trim`, `revealFolder`, `openSettings`, `enablePlugin`, `syncRepo`). There is no XAML, no code-behind, no arbitrary command execution; the host renders the panel and validates every action and every argument against its allowlist. A `tool` cannot open a window, draw chrome, or place anything outside the settings surface. That keeps "everything is a plugin" from eroding the zero-chrome design: the desk cleaner is a plugin, and it still looks and behaves like the rest of the app.

**Shell first, catalogue later**

The v1 shell ships the loader, Command Bar, webview host, notification pipeline and storage primitives, with a minimal bundled catalogue (4 services, 1 any-site template, 2-3 themes, 1 cleaner tool). Everything else — the other 10 theme families, more services, more filter packs — arrives through the plugin repo, not through a rebuild. That ordering is what makes the extensibility pay off: adding Catppuccin Frappé is a data change, not a release.

**Cost accepted**

A public manifest schema is an API we must keep stable; every field is a compatibility promise. Mitigation: `apiVersion` is validated, unknown fields are ignored, and the v1 schema is documented in `docs/plugins.md` with a JSON Schema file the loader enforces.

## 4.2 Plugin repositories

A repository is a static HTTPS folder — no server logic. The repo's own `registry/` directory is exactly that, so our directory and a third party's follow the same rules.

```
registry/
├─ index.json          # catalogue: plugins, versions, hashes
└─ index.json.sig      # Ed25519 signature over index.json
```

```jsonc
{
  "schema": 1,
  "name": "Chirpdesk Official",
  "generated": "2026-11-16T10:00:00Z",
  "keyFingerprint": "ed25519:9f3c…",
  "plugins": [
    {
      "id": "filters-meta",
      "kind": "filter",
      "name": "Meta ad & tracker filters",
      "version": "1.4.0",
      "apiVersion": 1,
      "minAppVersion": "0.3.0",
      "sha256": "b1946ac9…",
      "size": 18432,
      "url": "plugins/filters-meta-1.4.0.zip",
      "permissions": ["filters", "css"]
    }
  ]
}
```

**Install chain, in order — any failure aborts:**

1. Fetch `index.json` + `index.json.sig` over HTTPS.
2. Verify the index signature against the pinned key for that repo.
3. Filter entries by `apiVersion` and `minAppVersion`; anything incompatible is shown as unsupported, never installed.
4. Download the plugin archive; verify `sha256` **from the signed index**, then `size`.
5. Unpack to a temp dir, validate `plugin.json` and payload against schema, then move into `%APPDATA%\Chirpdesk\plugins\<id>\`.
6. Enable only if the user toggled it on; otherwise install disabled.

**Trust tiers and pinning**

| Tier | Source | Key | Consent |
|---|---|---|---|
| Bundled | `<install>\plugins\` | app build signature | implicit |
| Official | repo URL shipped in settings | key pinned at build | one prompt on first install |
| Community | URL the user adds | TOFU — fingerprint pinned on first fetch | prompt showing fingerprint + what it may do |

If a pinned key changes, the repo is **blocked** until the user re-trusts it by explicitly comparing the new fingerprint. That is the only protection against a repo being taken over, so it must never be silent.

**Limits and refusals**

- Data plugin archive ≤ 5 MB, ≤ 200 files, no executables, no absolute paths, no `..`.
- A repo can never modify the global auth/WebSocket allowlist, and can never install silently.
- `integration` kind requires a second, separate consent screen naming the executable and its declared commands.
- Unreachable or invalid repo → skipped and logged; the app starts and keeps working with what is installed.
- Index cached with a 24 h TTL; updates are **check-on-demand** by default, auto-update only if the user turns it on per repo.

Commands: `> repo add <url>` · `> repo list` · `> repo remove <name>` · `> repo sync` · `> plugins` (manager) · `> plugin: disable <id>`.

## 5. Architecture

```
Chirpdesk.exe (single instance, mutex-guarded)
│
├─ Host window (HWND always alive, never in taskbar)
│   ├─ WS_EX_TOOLWINDOW, ShowInTaskbar=false, ShowActivated=false
│   ├─ starts off-screen at (-32000, -32000), 2×2 px
│   ├─ Service X webview  (warm: visible when active, 1×1 when parked)
│   ├─ Service Y webview
│   └─ Command Bar overlay (cycle + search modes) and its backdrop
│
├─ Tray icon (unread state, menu, DND)
├─ Toast pipeline (WinRT, AUMID, click → activate service)
├─ Request filter engine (shared matcher, per-webview hooks)
├─ Storage service (size scan, trim, orphan scan)
├─ Updater (GitHub Releases, signature-verified, opt-in)
├─ Scheduler (warm-up, sleep, trim, keepalive tick)
└─ Settings store (%APPDATA%\Chirpdesk)
```

### 5.1 Hidden-but-warm host

The window is `Show()`n off-screen at startup so Chromium creates its composition target, then never activated. Each warm service is a `WebView2` in the same visual tree, sized 1×1 while parked. No reparenting, so no renderer restarts.

Chromium treats off-screen/occluded content as backgrounded, which throttles JS timers and therefore kills WebSocket heartbeats. Countermeasures (both mandatory):

```
--disable-background-timer-throttling
--disable-renderer-backgrounding
--disable-backgrounding-occluded-windows
```

plus a 30 s host tick that re-asserts `Controller.IsVisible` and calls `TrySuspend(false)`.

> **Phase 0 gate:** this must be proven. 3 hours tray-hidden with a real WhatsApp login, then an inbound message must toast. If it fails, the fallback is a 1×1 window parked at screen edge (behind everything) with `WS_EX_NOACTIVATE`, which is still invisible in practice.

### 5.2 Profiles

One `CoreWebView2Environment` per service, each with `userDataFolder = %LOCALAPPDATA%\Chirpdesk\profiles\<serviceId>`. Cookies, storage and logins never mix. Messenger and Facebook **may** optionally share a profile so one sign-in serves both (setting, default off).

### 5.3 Lifecycle

| State | Webview | Notification | Unread | Wake cost |
|---|---|---|---|---|
| Warm | loaded | yes | yes | 0 |
| Asleep | disposed | no | `asleep` | full reload (~3 s) |
| Cold | never created | no | `cold` | full load (~3 s) |

Warm-up order at boot: staggered 1.5 s apart, in user-defined priority. Selecting a cold service promotes it to warm **for the session only**; persisting is an explicit command.

**Two axes that must not be confused:**

- **Profile** is not optional. Every service instance always owns its own isolated profile directory. Two accounts = two service entries = two profiles. There is no "shared profile" toggle except the deliberate Messenger↔Facebook pairing option.
- **Warm at boot** is one persisted boolean per service (`warmAtBoot`). Runtime state can differ from it: a service pinned warm can be manually slept until the next launch, and a cold service cannot be asleep (it has no webview to dispose).

Cold never means signed out — the profile stays on disk, so pairing/session survive indefinitely.

### 5.4 Cache trimming

On sleep and every 6 h, delete per profile: `Cache`, `Code Cache`, `GPUCache`, `DawnCache`, `ShaderCache`. **Keep** `Cookies`, `Local Storage`, `IndexedDB`, `Service Worker` — that's the session and the push registration. See §8.

---

## 6. Notifications pipeline

```
site calls new Notification(...)  ──►  CoreWebView2.NotificationReceived
                                        │
                                        ├─ Handled = true (suppress WebView2 default UI)
                                        ├─ dedupe: tag + SHA1(body) for 3 s
                                        ├─ warm-up gate: drop until service ready + 5 s stable title
                                        ├─ per-service mute / global DND filter
                                        └─► ToastNotification via AUMID
                                              click ──► open Chirpdesk's Command Bar on that service
                                              dismiss ──► ReportClosed
```

- **AUMID:** `Chirpdesk` + a Start-menu shortcut carrying `System.AppUserModel.ID`, created on first run (IShellLink). Without it Windows shows "(unknown app)".
- **Permissions:** auto-grant `Notifications` for configured chat origins on `PermissionRequested`; prompt once (custom dialog, remembered) for `Camera`/`Microphone`; deny `Geolocation` silently.
- **Sound:** per-service mute respected; otherwise the Windows toast sound plays.
- **DND:** toast suppressed, badge keeps counting; optional schedule.
- **Inline reply:** out of scope for v1 (COM activation + toast input). Click-to-open only.

---

## 7. Ad, tracker and script blocking (R11)

WebView2 gives us `WebResourceRequested` / `AddWebResourceRequestedFilter`, i.e. every request is inspectable **before** it leaves the process and can be cancelled. That is enough for a real blocker without a browser extension.

### 7.1 Three tiers, default is the safe one

| Tier | What it does | Default | Breakage risk |
|---|---|---|---|
| **1 — Curated** | Blocks known ad/telemetry/tracker hosts and `sponsored`/`ads` endpoints (~300–600 entries, shipped, per-service overrides) | **on** | very low; whitelisted endpoints can't be caught |
| **2 — Cosmetic** | Injected CSS hides promoted/sponsored/upsell containers per service (`filters/cosmetic/<service>.json`), conservative selectors only | on, off per service | low; a bad selector hides the wrong element, never blocks a request |
| **3 — List import** | Opt-in import of ABP-syntax lists (EasyList Lite, EasyPrivacy), parsed offline into a compact domain trie | off | medium — opt-in, per-service, easily disabled |

Tier 3 syntax supported: `||host^`, host/path patterns, `@@` exceptions, `$domain=`, `$third-party`, `$script|image|xmlhttprequest`. Explicitly **unsupported and skipped**: `$csp`, `$redirect`, `$removeparam`, `##`/`#@#` cosmetic syntax (we use our own cosmetic format). Unsupported rules are counted and shown, never silently mangled.

### 7.2 Engine

- One compiled matcher in the host process: hostname trie for `||host^` rules + a small ordered regex list for path rules.
- Every decision cached per host in a dictionary, so steady-state cost is a hash lookup: **O(1) per request**.
- Registered per webview with `AddWebResourceRequestedFilter("*", All)`; resource context is consulted, so "block trackers' images and XHR but not their fonts on the app's own domain" is expressible.
- Counters per service (blocked requests, estimated bytes) shown in Settings and the Command Bar's `> storage`/`> filters` rows — transparency and the first debugging tool.

### 7.3 Fail-open, always

Three hard rules, because a chat client that can't log in is worse than an ad:

1. If the matcher throws, a filter file is invalid, or anything is ambiguous → **allow the request**.
2. A permanent allowlist covers auth, WebSocket and update endpoints (`*/ws`, `*/ws/*`, `login`, `oauth`, `accounts`, service root domains). Nothing on the allowlist can be blocked by any tier.
3. `> blocking: off` for a single service, one command away, plus a tray hint on failure pointing at it.

### 7.4 Scripts

We do not strip site JavaScript — that's how WhatsApp's encryption init breaks. Instead:

- Block the **third-party script requests** that carry analytics and ad SDKs (tier 1/3).
- Optionally stub `fbq`, `gtag`, `dataLayer`, `_fbq` as no-ops (off by default; dead stubs can throw, and network blocking is cleaner).
- Optional per-service `injectCss` for clutter, separate from cosmetic filters, so a user tweak is never wiped by a filter-pack update.

---

## 8. Disk cleaning (R12)

The point is not only that Chirpdesk is small — it's that the chat apps **already on your machine** are hoarding space, and you should see and reclaim it from one place.

### 8.1 Per-service storage view

Settings → Storage, and the Command Bar command `> storage`:

| Column | Meaning |
|---|---|
| Service | WhatsApp, Messenger, … |
| Data | cookies, `Local Storage`, `IndexedDB` — **never touched by a trim** |
| Cache | `Cache`, `Code Cache`, `GPUCache`, `DawnCache`, `ShaderCache` |
| Media | cached images/video blobs inside `IndexedDB` (WhatsApp is the big one) |
| Last trim | timestamp |
| Blocked | requests blocked (from §7) |

Actions per service: **Trim cache** (safe) · **Clear media cache** (downloads re-fetch) · **Sign out & reset** (clears everything, requires re-pairing) · **Remove service + delete data**. Every action shows the exact path and byte count in a confirmation dialog first.

### 8.2 Automatic policy

- Trim caches on sleep and every 6 h; keep sessions.
- Per-service cache budget (default 60 MB) and global budget (default 500 MB); when exceeded, evict oldest-first and log what was evicted.
- Never trims while a service is warm and mid-transaction; the webview is parked or disposed first.

### 8.3 Orphan scan ("desk space")

Finds chat-app data left behind by things installed before or no longer used:

- Chirpdesk profiles belonging to removed services.
- Official **WhatsApp desktop** WebView2 data (`%APPDATA%\WhatsApp`, `%LOCALAPPDATA%\WhatsApp`), **Telegram Desktop**, and Electron wrappers (Ferdium / Rambox / Station / MultiMessenger profile dirs) if present.
- Reports size, last-modified and owning app — per item, with a checkbox. Nothing pre-selected.

Safety rules, non-negotiable:

- Only paths from a **fixed allowlist of known roots**; resolved and re-verified as absolute, inside those roots, no wildcards, no parent traversal.
- Deleting user data goes to the **Recycle Bin** (`FOF_ALLOWUNDO`); only cache subfolders are hard-deleted.
- Never touches mail stores, `Documents`, or Edge/Chrome profiles (those are reported as a hint with manual instructions, since no supported API can safely edit a live Chromium profile).
- Detects running processes of the owning app and refuses to delete a directory in use.

### 8.4 Reclaimed counter

A running total on the storage page: *"Reclaimed 812 MB since install."* Cheap, makes trim habits visible, proves the feature works.

---

## 9. Unread + tray

- Injected script per service: `MutationObserver` on `document.title`, posts `{count, title}` over `chrome.webview.postMessage`. No polling from the host.
- Per-service `unreadRegex` (default `\((\d+)\)` then `(\d+)`), editable.
- Tray icon draws a dot variant when total > 0 (rendered at runtime, no assets); tooltip lists `WhatsApp 3 · Telegram 1`.
- Optional taskbar overlay badge when the window is visible.

---

## 10. Command Bar (R5, R6)

**One overlay. One component. No sidebar, no tab strip, no menu bar.** Everything navigational happens here.

### 10.1 Two entry modes

| Entry | Mode | Behaviour |
|---|---|---|
| `Ctrl+Tab` (hold) | **Cycle** | MRU list of **warm** services only, unread counts visible. `Tab` / `Shift+Tab` move the highlight, releasing `Ctrl` commits, `Esc` cancels. Query line hidden. |
| `Ctrl+P` | **Search** | Sticky bar with focused input. Rows: warm services (unread shown) → cold services (`cold`) → commands. Fuzzy filter as you type. `Enter` runs, `Esc` dismisses. |
| Cycle, then type | **Search** (retained highlight) | Auto-commit is abandoned the moment a character is entered; the selection stays highlighted and the bar becomes sticky. |

Rows are rendered by one template in both modes: icon · name · unread count · state badge (`warm` / `asleep` / `cold`) · current-highlight.

Ordering: unread first, then recency, then fuzzy score. `>` restricts to commands; bare text searches services and commands together.

### 10.2 Commands

`> theme dark` · `> theme light` · `> theme system` · `> theme <family> [variant|auto]` · `> settings` · `> add service` · `> add any site <url>` · `> plugins` · `> storage` · `> filters` · `> blocking: off` · `> warm <service>` · `> warm at boot <service>` · `> sleep <service>` · `> mute` · `> dnd 30m|1h|tomorrow` · `> reload` · `> copy url` · `> open in browser` · `> quit`

`> warm at boot <service>` toggles the persisted pin and the Settings switch reads/writes the same field; a dot on the service row marks "pinned warm" while the badge keeps showing live runtime state (`warm` / `asleep` / `cold`).

**Theme preview:** `> theme` lists **families**, with their variants revealed on `Tab`; arrowing applies the active variant live, `Enter` commits, `Esc` restores the previous family and variant. Switching to a family keeps your current mode (dark stays dark) and uses that family's default for it.

### 10.3 Keys

| Key | Action |
|---|---|
| `Ctrl+Tab` (hold) | cycle warm services |
| `Tab` / `Shift+Tab` while held | move highlight |
| release `Ctrl` | commit |
| `Ctrl+P` | search mode |
| `Ctrl+Shift+P` | passed through to the page (site print shortcut) |
| `Ctrl+1..9` | direct jump, fixed order (power path, no UI) |
| `Ctrl+W` | hide to tray |
| `Esc` | dismiss / cancel |

**Interception:** `Ctrl+Tab`, `Ctrl+P` and `Ctrl+1..9` are handled in `AcceleratorKeyPressed`, so sites cannot steal them. `Ctrl+Shift+P` is the explicit passthrough. Cycle commit is driven by a 30 ms `GetAsyncKeyState(VK_CONTROL)` poll, because WebView2 does not reliably deliver modifier key-up when focus is inside the page. The overlay is a `Canvas` over existing content — no resizing, no webview recreation.

### 10.4 Settings is a destination, not navigation

Settings stays a small window (URLs, icons, regexes, theme choice, storage, autostart, blocking) reached only by `> settings`. No tabs, no rail, no command strip anywhere in the app.

---

## 11. Service model

```jsonc
{
  "id": "whatsapp",
  "name": "WhatsApp",
  "url": "https://web.whatsapp.com/",
  "iconPath": null,            // cached favicon, or custom image
  "accent": "#25D366",         // optional brand override for the theme accent
  "warmAtBoot": true,
  "unreadRegex": "\\((\\d+)\\)",
  "mute": false,
  "userAgent": null,
  "injectCss": null,
  "blocking": "default",       // default | off | +list
  "shareProfileWith": null     // e.g. messenger -> facebook
}
```

Add flow: paste URL → loads once → favicon + title captured → saved **cold** → "warm at boot" offered. Sign-in popups (`window.open`) open as child windows in the same profile. Known embedded-browser walls (Google sign-in, some Cloudflare challenges) are detected and replaced with a "sign in via your browser, then come back" hint.

Defaults: WhatsApp, Messenger, Telegram warm; Facebook cold. **Newly added services start cold**, with the `warm at boot` switch offered immediately after first sign-in.

---

## 12. Design system

Zero permanent chrome means the design budget goes into the **Command Bar, tray menu, settings and title bar** — plus making themes first-class plugins.

Reference: Limusic's app. Near-black neutral base in the `#241c22` family, one restrained accent, no gradients, no glow, generous spacing, UI type at 12–13 px with wide tracking on labels.

### 12.1 Tokens (semantic, not raw colours)

| Token | CSS variable | Used for |
|---|---|---|
| `bg.window` | `--cd-bg-window` | window backdrop |
| `bg.surface` | `--cd-bg-surface` | overlay panels, bar, list rows |
| `bg.elevated` | `--cd-bg-elevated` | hovered/selected rows, dialogs |
| `bg.scrim` | `--cd-bg-scrim` | Command Bar backdrop (alpha) |
| `border.default` / `border.focus` | `--cd-border-*` | hairlines, keyboard focus ring |
| `text.primary` / `.secondary` / `.muted` | `--cd-text-*` | hierarchy |
| `accent.default` / `accent.fg` | `--cd-accent-*` | selection, active row, badges |
| `state.danger` / `.success` / `.warning` | `--cd-state-*` | mute, DND, blocking, errors |
| `shadow.overlay` | `--cd-shadow-overlay` | overlay elevation |

Token names are CSS-variable compatible so the same token JSON can drive a future non-WPF surface (web or mobile UI) without rewriting palettes — the one structural trick worth copying from Limusic.

### 12.2 Theme plugins

A theme is a plugin with `kind: "theme"` and **one folder per family**, containing every variant the family defines — dark and light are variants of the same plugin, never separate plugins. That is what lets `> theme catppuccin` keep working when the OS flips to light mode.

`theme.json`:

```json
{
  "family": "Catppuccin",
  "defaults": { "dark": "mocha", "light": "latte" },
  "variants": [
    {
      "id": "mocha",
      "name": "Mocha",
      "mode": "dark",
      "tokens": { "bg.window": "#1e1e2e", "text.primary": "#cdd6f4", "...": "..." }
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

Rules the loader enforces:

- At least one variant; every variant needs `id` (`^[a-z0-9-]{1,24}$`), `name`, `mode`, and the full token set from §12.1.
- `defaults.dark` / `defaults.light` must name an existing variant of that mode. A family with no light variant simply has no light default, and "follow system" keeps the dark variant while flagging it in Settings.
- Selecting a family pins it; `> theme catppuccin mocha` pins a specific variant; `> theme catppuccin auto` returns to the family default for the current mode.
- Applied with `DynamicResource` onto a WPF `ResourceDictionary` keyed `Cd.<token>`, so switching is instant — no restart, no flicker.

Selected **family** is stored in settings; selected **variant per mode** is derived from `defaults`, so a family added later can opt into "follow system" without a migration.

**Bundled families** (shipped as plugins, expanding via the official repo):

| Family | Variants |
|---|---|
| Chirpdesk | Default Dark, Default Light |
| System | follows Windows app theme, maps to the active family's dark/light default |
| Dracula | Dracula, Dracula Soft |
| Catppuccin | Mocha, Macchiato, Frappé, Latte |
| Nord | Nord, Nord Light |
| Tokyo Night | Night, Storm, Day |
| Gruvbox | Dark Hard, Light |
| Rosé Pine | Main, Moon, Dawn |
| Solarized | Dark, Light |
| GitHub | Dark, Light |

The shell keeps a **hardcoded baseline theme** compiled in. If every theme plugin is missing, invalid or disabled, the app still renders — it falls back to baseline and says so in Settings. No plugin can make the UI unreadable.

Selection: `> theme`, or Settings → Appearance. "Follow system" flips light/dark on `WM_SETTINGCHANGE`, picking each family's default for the new mode.

### 12.3 Contrast + accessibility

Every variant is validated at load: text/background pairs must clear **WCAG AA 4.5:1** (3:1 for large/UI). Failures are nudged in lightness by the minimum passing amount and reported in Settings, so custom themes are flagged rather than silently unreadable — the nudge loop is capped at 20 iterations, after which it gives up and logs. Focus rings are never colour-only; selection changes fill **and** weight.

### 12.4 Site appearance

Sites own their CSS. On every theme change we set `CoreWebView2Profile.PreferredColorScheme`, so services honouring `prefers-color-scheme` follow your light/dark choice. Optional per-service `injectCss` goes further; a few examples ship, clearly best-effort.

### 12.5 Typography + motion

Segoe UI Variable for UI; `Cascadia Mono` for the Command Bar query line — the "hacker" flavour lives there, not in decoration. Overlay open/close 90 ms fade + 4 px rise. No other motion.

---

## 13. Data locations

| Path | Contents |
|---|---|
| `%APPDATA%\Chirpdesk\settings.json` | services, appearance, autostart, DND, blocking, update channel |
| `%APPDATA%\Chirpdesk\plugins\<id>\` | user-installed plugins of any kind |
| `%APPDATA%\Chirpdesk\filters\compiled\` | compiled matchers from imported lists |
| `%APPDATA%\Chirpdesk\updates\` | downloaded installer + manifest, deleted after apply |
| `%LOCALAPPDATA%\Chirpdesk\profiles\<id>\` | per-service WebView2 data |
| `%LOCALAPPDATA%\Chirpdesk\icons\` | cached favicons |
| `%LOCALAPPDATA%\Chirpdesk\logs\` | rolling log, 2 MB cap |

Settings writes are atomic (temp + rename); a corrupt file falls back to defaults with a logged warning.

---

## 14. Shipping and updates (phase 5)

- **Installer:** Inno Setup, per-user, no elevation, `setup.exe`; a plain `.zip` portable build for people who want no installer. No MSIX, no Store.
- **Shortcut creation** at install time carries the `System.AppUserModel.ID` used by toasts and the updater.
- **Self-update:** the app checks GitHub Releases (stable channel only), downloads `Chirpdesk-Setup.exe` + manifest, verifies an **Ed25519 signature** and SHA-256 of the payload, then runs the installer silently and relaunches. Unsigned, malformed or unparseable manifests are refused and logged; the app keeps running the current version.
- **Opt-in:** update checks can be disabled; nothing is ever downloaded without a signature match. Portable builds never self-update.
- **CI:** one GitHub Actions workflow on `windows-latest` that builds, runs tests, packs the installer + zip and attaches them to a Release with the signature. Release-only — never used for development.
- **No landing page.** Repo README is the only public surface for now.

---

## 15. Phases and acceptance criteria

| Phase | Scope | Done when |
|---|---|---|
| **0 — Spine** | SDK install, project skeleton, hidden host, one service (WhatsApp), toast pipeline + AUMID, single instance | 3 h tray-hidden, inbound message toasts with correct text and icon; click opens the bar on WhatsApp; never appears in Alt+Tab |
| **1 — Command Bar** | plugin loader + manifest validation, services and themes as plugins, multi-service hosting, per-service profiles, Command Bar (cycle + search), cold/warm rows, unread counts, `Ctrl+1..9`, `Ctrl+W` | `Ctrl+Tab` cycles warm services; hold → type → filter → `Enter` works as one continuous interaction; cold services appear in search but never in cycle; half-typed text survives switching |
| **2 — Lifecycle + storage** | warm/cold at boot, sleep, autostart with 20 s delay, mute + DND, settings window, add-URL flow (any-site template), per-service regex, storage scan/trim/orphan primitives, cleaner `tool` plugin with `panel.json` | Reboot with 3 warm + 1 cold: notifications within 30 s, no storm, no window; disk stays under 150 MB after a week; orphan scan finds a real leftover folder and restores it from the Recycle Bin |
| **3 — Blocking** | request matcher, curated tier-1 pack, cosmetic packs, per-service counters, allowlist, enable/disable commands | WhatsApp + Messenger + Telegram log in and send messages with blocking on; counters show blocked requests; `> blocking: off` for one service restores every request |
| **4 — Design** | token pipeline, theme plugin loader with variants, baseline theme, system light/dark, live preview from `> theme`, contrast validator, `PreferredColorScheme` sync | Theme switch is instant and previewable from the bar; every variant passes AA; a dropped-in theme plugin appears in `> theme`; deleting all theme plugins still renders the baseline |
| **5 — Plugin ecosystem** | catalogue expansion: 10 theme families, cleaner + diagnostics tools, extra services and filter packs; plugin manager UI, repo add/list/remove/sync, signature + hash verification, TOFU pinning, install/update/uninstall, conflict resolution between repos, `> repo` commands | Adding the official repo installs a newer filter pack and a theme without a rebuild; a repo with a changed key is blocked until re-trusted; a corrupt archive is rejected and the app still starts |
| **6 — Ship** | Inno Setup installer, portable zip, self-updater with signature verification, first-run overlay teaching `Ctrl+Tab`/`Ctrl+P`, CI release workflow, README docs | Fresh machine: run setup, add service, sign in, reboot, receive toast, switch theme, run a cleanup; second release updates itself and refuses a tampered manifest |
| **7 — Optional** | ABP list import (tier 3), out-of-process integration plugin host (TDLib as first integration), live preview during cycle, inline toast reply | only if v1 proves itself in daily use |

Each phase ends with a build you can actually run.

---

## 16. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| Off-screen window throttled despite flags | R2 fails outright | Phase 0 gate; fallback 1×1 `WS_EX_NOACTIVATE` at screen edge |
| Blocking breaks a login or WebSocket | Service unusable, worse than ads | Fail-open, permanent auth/WS allowlist, one command to disable per service, counters for diagnosis |
| Cosmetic selector hides the wrong thing | Confusing UI | Conservative selectors, per-service toggle, never touches requests |
| Cache deletion logs the user out | Re-pairing WhatsApp | Cache and data are separate directories, separate buttons, separate confirmations |
| Orphan scan deletes something it shouldn't | Data loss | Fixed root allowlist, path re-verification, Recycle Bin for user data, in-use detection, nothing pre-checked |
| Tampered update package | Code execution | Ed25519 + SHA-256 verification, refuse and log on mismatch, updates opt-in |
| Malicious plugin | Session theft, keylogging | v1 plugins are data-only (no code path exists); code only out-of-process with explicit consent; no in-process assembly loading, ever |
| Plugin schema drift | Community plugins break each release | `apiVersion` validation, unknown fields ignored, only additive changes within a major version, schema published |
| Broken plugin hides a service | User thinks the app lost their account | Invalid plugins are skipped and flagged; disabling never deletes a profile |
| Malicious filter pack | Blocks logins, or beacons out via CSS | Host rules cannot touch the global allowlist; CSS sanitised (no `@import`, no remote `url()`); permissions listed before install |
| Compromised plugin repo | Serves a tampered plugin | Ed25519-signed index, pinned per-repo key, explicit re-trust prompt on key change, hash from the signed index, no silent installs |
| Repo outage or stale index | Filters age, manager errors | Cached index (24 h TTL), installed plugins keep working offline, failures logged not fatal |
| `Ctrl+P` steals a site's print shortcut | Minor | `Ctrl+Shift+P` passthrough documented |
| Meta changes the web client / sniffs the UA | Service breaks | Stock Edge UA like Meta's own Windows app; per-service UA override |
| Google sign-in refuses embedded browsers | Some services unusable | Detect + explain; open-in-browser escape hatch |
| Service-worker notifications differ from page notifications | Missed toasts | `NotificationReceived` covers both; per-service verification; injected `Notification` shim as fallback |
| WebView2 memory per warm service | R10 partially fails | It's the site's cost; sleep, warm-count guidance, per-service RAM shown |
| Toast identity shows "(unknown app)" | Looks broken | AUMID + shortcut on first run, verified in phase 0 |
| `Ctrl+Tab` stolen by a site | R6 fails | `AcceleratorKeyPressed` precedence; passthrough documented |
| Windows DND swallows toasts | User thinks it's broken | Read `SHQueryUserNotificationState`; tray hint when suppressed |

---

## 17. Repo layout (target)

```
Chirpdesk/
├─ README.md
├─ PLAN.md
├─ LICENSE                    # MIT
├─ plugins/                   # every bundled extension, one folder each
│  ├─ whatsapp/               # kind: service
│  ├─ theme-dracula/          # kind: theme
│  └─ filters-meta/           # kind: filter
├─ registry/                  # the official plugin repo: index.json + index.json.sig
├─ schemas/                   # plugin.json / service / theme / filter JSON Schemas
├─ installer/                 # Inno Setup script, signing keys (public only)
├─ src/Chirpdesk/
│  ├─ App.xaml(.cs)           # startup, single instance, autostart
│  ├─ Plugins/                # discovery, manifest validation, registry, hot rescan
│  ├─ Hosting/                # hidden window, webview manager, profiles
│  ├─ Services/               # definitions, store, add-URL flow
│  ├─ Notifications/          # toast pipeline, AUMID, permission policy
│  ├─ Filtering/              # matcher, list parser, counters
│  ├─ Storage/                # size scan, trim, orphan scan
│  ├─ Updates/                # release check, signature verify, apply
│  ├─ Ui/CommandBar/          # cycle + search modes, one row renderer
│  ├─ Ui/                     # title bar, settings window, first-run overlay
│  └─ Theming/                # theme plugin loader, token mapping, contrast check
├─ docs/                      # decisions, screenshots, service notes
└─ .github/workflows/release.yml
```

Dev environment: local only, .NET 8 SDK, no Visual Studio, no Windows SDK, no MSIX. Peak dev disk ~2 GB; app footprint ~5 MB.

---

## 18. Decided / open

Decided:

- License **MIT**; repo stays **private** until v1 works.
- Stack **WPF + WebView2**; evaluate-and-reject recorded in §4.
- One Command Bar for navigation, commands and theme switching.
- **Plugin-shaped architecture**: services, templates, themes (with dark+light variants), tools and filter packs are plugins with one manifest; data-only in v1; code only out-of-process, phase 7.
- **Shell first**: v1 ships the loader, Command Bar, host, notifications and storage primitives with a minimal bundled catalogue; the rest of the catalogue lands as plugins.
- Adblock packs ship as `filter` plugins, maintained in the repo's `plugins/` + `registry/`, installable from the official repo or any user-added repo.
- `Ctrl+Tab` cycles, `Ctrl+P` searches; `Ctrl+K` removed.
- No sidebar, tab strip, menu bar or landing page.
- Self-updating signed installer, opt-in, stable channel only.
- Defaults: WhatsApp, Messenger, Telegram warm; Facebook cold.

Open:

- Title bar: minimal custom bar showing `service · unread`, or none at all.
- `Ctrl+1..9` direct jump: keep or drop for a pure two-key model.
- Blocking default: tier 1 + cosmetic on, list import off (assumed).

---

## 19. Trade-offs accepted, and what would make us revisit

Every entry is a cost we are knowingly paying. If the revisit trigger fires, the decision is re-opened deliberately, not silently drifted.

| Decision | Cost we accept | Revisit if |
|---|---|---|
| WebView2 hosting instead of native protocol clients | 150–400 MB RAM per warm service; we inherit whatever Meta ships | TDLib-class clients appear for WhatsApp/Messenger, or a service publishes an official API |
| Single hidden host window for all webviews | one window's HWND is the liveness anchor; a stuck renderer can stall it | two consecutive reports of the window refusing to show, or renderer crashes surviving a reload |
| Per-service `CoreWebView2Environment` | more processes, more RAM, slower first boot | total RAM with 4 warm services exceeds ~1.6 GB on a 12 GB machine |
| Fail-open filtering | some ads slip through when a filter file is broken | a service breaks *because* of a filter and fail-open did not prevent it |
| Cosmetic filtering with CSS selectors | selectors rot when sites redesign | more than one breakage per month per service |
| No auto-update in v1 → signed self-update after | users on old builds for longer | v1 sees daily use, or a security fix needs shipping fast |
| Custom WPF chrome instead of native title bar | DPI and window-chrome edge cases are ours to handle | visible chrome glitches on multi-monitor or fractional scaling |
| Command Bar as sole navigation | discoverability depends on the first-run overlay | a user cannot find settings after the overlay is dismissed |
| One shared cookie jar *within* a service instance | two logins for the same site need two entries | users routinely need more than two accounts per site |
| MIT, private repo, solo maintenance | no external contributors during v1 | v1 ships and is public — then CONTRIBUTING and issue templates get added |
| Plugin manifest is a published schema | compatibility burden on every field | a second consumer of the schema appears (mobile, web) or community plugins break on a minor bump |
| Users can add third-party plugin repos | supply-chain trust is partly delegated to them | repo key pinning plus an explicit fingerprint prompt; only data plugins are installable in v1 |

