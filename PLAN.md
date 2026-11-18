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
| `docs/plugins.md` | plugin manifest, kinds, payloads, repositories |
| `docs/measurements.md` | recorded RAM, disk, latency and soak results |
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
| R1 | Starts at boot, **tray only** — no window, no taskbar entry until you ask for one | Hidden host window by default; a visible conversation window exists only on demand |
| R2 | Selected services are **warm** at boot and notify even if never opened | Real loaded webviews, background throttling disabled |
| R3 | Services not selected stay **cold** until invoked | Webview created on demand, spinner while loading |
| R4 | Notifications = native Windows toasts, no backlog storm at login | `NotificationReceived` → toast, dedupe, warm-up suppression, capability probe per service |
| R5 | **One** command surface for everything; no sidebar, tab strip or menu | Unified Command Bar, zero permanent chrome |
| R6 | `Ctrl+Tab` cycles open services; `Ctrl+K` opens search and `Ctrl+Shift+K` lists open tabs | Single overlay component, three entry modes |
| R7 | Add a **chat site by installing its definition plugin** | A `service` plugin is the only on-ramp; every site has a curated definition (ADR 0011) |
| R8 | Isolated session per service (multiple accounts) | One `WebView2` profile per service **instance** |
| R9 | Theme system: popular families, each with dark **and** light variants built in, live switch | Theme plugins with `variants[]`, token pipeline, instant `DynamicResource` swap |
| R10 | Small on disk and RAM | WPF + WebView2, framework-dependent, cache trimming |
| R11 | **Block ads, trackers and junk scripts** in the web clients | Request-level filter engine + cosmetic CSS, fail-open |
| R12 | **See and reclaim disk** used by these chat apps | Per-profile size breakdown, safe cleanup, orphan scan |
| R13 | Shipped service **definitions**, themes, filter packs and tool panels are **plugins** with one manifest | Data-only in v1; user service instances are settings, not plugins |
| R14 | Ship a **plugin directory** in the repo; users may add **their own plugin repos** | Signed indexes, per-repo trust consent, data-only installs |
| R15 | Shell first, catalogue later | Core ships with: loader, Command Bar, webview host, notification pipeline, storage primitives. Catalogue content arrives as plugins |

## 2.1 Non-functional targets and constraints

| Dimension | Target | Why this number |
|---|---|---|
| Command Bar open | < 50 ms perceived, < 120 ms before the row list paints | it is the only UI; latency here is the product |
| Service switch | < 100 ms warm, no reload, no scroll reset | warm webviews are the whole point |
| Toast latency | < 1 s from the site calling `new Notification()` | slower than the site's own toast feels broken |
| Boot to all-warm | < 40 s after login, staggered | earlier than that fights Explorer/AV and loses |
| Idle CPU | < 1.5 % average with 3 warm services (shell alone < 0.2 %) | three Chromium renderers idle are never free; 0.5 % was wishful |
| RAM | shell < 70 MB, 150–400 MB per warm service | per-service cost is the site's, not ours |
| Disk | app < 10 MB, total < 200 MB after a week of 3 warm services | the original complaint; 150 MB was tighter than WhatsApp's cache allows |
| Soak | 8 h tray-hidden with sockets alive, zero missed test notifications | R2 is unprovable otherwise |
| Dependencies | ≤ 2 runtime NuGet packages (WebView2 + tray), 0 native libraries we author | security surface + auditability; test packages and targeting packs are excluded |
| Build | one command from clean clone on a machine with .NET 8 SDK only | no VS, no full Windows SDK, no MSIX |

Hard constraints: single developer, ~13 GB free disk, Windows 11 dev machine and Windows 10 compatibility target, no code-signing certificate in v1 (unsigned installer, documented), no backend service ever. Windows 10 needs the WebView2 Evergreen Runtime; every clean machine (10 or 11) needs the .NET 8 Desktop Runtime, because the app is framework-dependent. The installer handles both (phase 6); the portable zip documents them.

Testability constraint that shapes the code: everything with logic must be injectable and headless — `IClock`, `IToastSink`, `IServiceStore`, `IProcessProbe` — so the test suite never needs a WebView2 or a toast to run. UI and WebView2 code is thin by design and verified manually.

## 3. Non-goals (v1)

- No message sync, no server, no account of any kind. Nothing leaves the machine.
- No native protocol clients in v1 (Telegram on TDLib is a later optional swap).
- No chat-level search inside a service (fragile DOM scraping).
- No general-purpose adblocker for your browser — blocking applies inside Chirpdesk's webviews only.
- No marketing site / landing page.
- No inbound call guarantee (WebRTC works only if the embedded permission flow allows it — see risks).
- No custom download manager in v1: WebView2's default download flow is used, with no bespoke UI.
- No MSIX / Store distribution.

---

## 4. Stack decision

| Candidate | Verdict | Reason |
|---|---|---|
| **WPF + WebView2 (chosen)** | ✅ | WebView2 ships with Windows 11 (Windows 10 needs the Evergreen Runtime) and setup installs the .NET 8 Desktop Runtime; ~5 MB app; `CoreWebView2.NotificationReceived` gives page notifications, with service-worker notifications an explicit phase-0 gate (see §6); direct control of profiles, permissions, background flags and request filtering. One language. ~1.5–2 GB dev disk. |
| Tauri v2 (Limusic's stack) | ✗ | Same WebView2 engine underneath, so no runtime advantage. Its two weakest points are exactly our two critical ones: notification interception and `WebResourceRequested` require raw `webview2-com` COM interop in Rust, and multi-webview-in-one-window sits behind an unstable flag. Costs 3–5 GB dev disk and three languages. |
| WinUI 3 | ✗ | Windows App SDK dependency (~50–80 MB) for no feature we need |
| Electron | ✗ | Defeats the premise (bundles Chromium, 200 MB+) |
| Thunderbird add-on | ✗ | `tabhide` unsupported, one shared cookie jar, no media permissions, no request filtering, Meta "browser unsupported" |

Target framework: `net8.0-windows10.0.19041.0` (WinRT toast projections without extra packages), `UseWPF=true`, published **framework-dependent**.

Runtime NuGet packages, deliberately few: `Microsoft.Web.WebView2` (engine binding) and `Hardcodet.NotifyIcon.Wpf` (MIT, tray). Test packages, the Windows SDK projection pack and `Inno Setup` (build-time only) are not runtime dependencies and are not shipped in the app. Filter lists are parsed by our own code; so is logging — `Microsoft.Extensions.Logging` would be a third package, so the app uses a tiny `ILog` with a rolling file sink written in `Chirpdesk.Core`.

---

## 4.1 Plugin architecture (cross-cutting)

Every **distributable content pack** is a plugin: a folder with a `plugin.json` manifest. User data — the services you add, their URLs, names and window state — is settings, not a plugin. That distinction is the whole model:

- **Definition** = a plugin (shipped or installed from a repo): url, icon, unread regex, skin CSS, filter pack ref, accent.
- **Instance** = a row in `settings.json`: `definitionId` + optional overrides, one profile directory, one warm/cold state.

Adding `https://web.telegram.org/` means installing Telegram's **definition plugin**, which makes the site available; running it writes a settings instance. There is no URL add flow: a definition is the only object that knows the site's regex, notification mode and `pageDarkMode`, and a user-added URL would be a permanently second-class instance (ADR 0011).

| Kind | Payload | Code? | Trust tier | Ships in v1 |
|---|---|---|---|---|
| `service` | definition: url, icon, unread regex, optional CSS, filter refs, accent | no | bundled / repo | WhatsApp, Messenger, Telegram, Facebook |
| `theme` | `variants[]` with `mode: dark\|light`, `tokens{}`, optional `preview` | no | bundled / repo | Dracula, Catppuccin, Nord, … |
| `filter` | curated host list, cosmetic CSS | no | bundled / repo | tier 1 + cosmetic packs |
| `tool` | declarative `panel.json` (list/table + actions from host primitives) | no | **bundled only in v1** | Disk cleaner |
| `integration` | optional helper executable + JSON-RPC contract (e.g. TDLib for Telegram) | **yes** | user-installed, explicit consent | phase 7 only |

There is no `template` kind and no `"template": true` flag, and no built-in "add any site" form: every site is a `service` definition plugin, bundled or installed from a repo, so nothing can be added to the app that is not also inspectable, versioned and replaceable. User instances remain settings rows and are still how two accounts of one site stay separate.

**Layout**

```
plugins/<id>/
├─ plugin.json          # id, kind, name, version, apiVersion, author, permissions[]
├─ icon.png|svg
├─ service.json         # kind: service  (definition only)
├─ theme.json           # kind: theme    (variants[], each with mode + tokens)
├─ filters.json         # kind: filter   (hosts, cosmetic selectors)
├─ panel.json           # kind: tool     (list/table + host action primitives)
└─ README.md            # optional, shown in the plugin manager
```

Bundled plugins live in the app's install directory; user plugins in `%APPDATA%\Chirpdesk\plugins\<id>\`. Both are scanned the same way, so shipped and user content follow identical rules.

**Lifecycle**

- Scan on startup and on `FileSystemWatcher` change; parse and validate each manifest; a broken plugin is skipped, logged, and marked in the manager — it never prevents the app from starting.
- `apiVersion` mismatch → plugin disabled with a clear message, not a crash.
- Enable/disable per plugin, persisted in settings; disabling a theme falls back to the compiled baseline, disabling a service definition leaves existing instances and their profiles on disk intact (the instances show `definition missing` until it is re-enabled or removed).
- Commands: `> plugins`, `> plugin: disable <id>`, `> theme <id>` (themes are addressable as plugins without exposing the word "plugin" in normal use).

**Why no in-process code plugins**

The app holds every logged-in session. A .NET assembly loaded in-process is full trust: it can read cookies, intercept keystrokes, exfiltrate anything. Assembly-level isolation (`AssemblyLoadContext`) does not sandbox that. Therefore code extensions are out-of-process only: a helper process the app starts, talking JSON-RPC over a named pipe, with a manifest-declared command surface, a timeout, and no shared memory. A hung or malicious helper can be killed and cannot touch the tray, the webviews or the settings store.

**What "plugin" does not buy**

Plugins do not invent UI, and they do not invent capabilities. A `tool` plugin declares a panel in `panel.json` using **host primitives only** — `list`, `table`, `text`, `progress`, `button`, `separator` — and may invoke a fixed set of **host actions** (`storageReport`, `trimCache`, `scanOrphans`, `deleteOrphan`, `revealFolder`, `openSettings`). There is no XAML, no code-behind, no filesystem path, no arbitrary command execution; the host renders the panel and validates every action and every argument against its allowlist. A `tool` cannot open a window, draw chrome, or place anything outside the settings surface.

Because a tool can only surface built-in capability, **user-installed tools are disabled in v1**. The cleaner ships as a bundled tool panel so it is data (disableable, inspectable, replaceable in a repo later) without pretending third-party tools add function. The **plugin manager and diagnostics are built-in shell UI, never plugins** — a plugin cannot manage the plugin system that loaded it.

**Shell first, catalogue later**

The v1 shell ships the loader, Command Bar, webview host, notification pipeline and storage primitives, with a minimal bundled catalogue (4 service definitions, 2–3 themes, 1 cleaner panel). Everything else — the other theme families, more service definitions, more filter packs — arrives through the plugin repo, not through a rebuild. That ordering is what makes the extensibility pay off: adding Catppuccin Frappé is a data change, not a release.

**Cost accepted**

A public manifest schema is an API we must keep stable; every field is a compatibility promise. Mitigation: `apiVersion` is validated, unknown fields are ignored, and the v1 schema is documented in `docs/plugins.md`. A JSON Schema file lands with repo installs in phase 5; until then `PluginLoader` is the validator.

## 4.2 Plugin repositories

A repository is a static HTTPS folder — no server logic. The repo's own `registry/` directory is exactly that, so our directory and a third party's follow the same rules.

```
registry/
├─ index.json          # catalogue: plugins, versions, hashes
└─ index.json.sig      # ECDSA P-256 signature over index.json
```

```jsonc
{
  "schema": 1,
  "name": "Chirpdesk Official",
  "generated": "2026-11-16T10:00:00Z",
  "keyFingerprint": "ecdsa-p256:9f3c…",
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
5. Unpack to a temp dir: reject absolute paths, `..`, reparse points and zip-slip entries; validate `plugin.json` and payload against schema, then move into `%APPDATA%\Chirpdesk\plugins\<id>\`.
6. Enable only if the user toggled it on; otherwise install disabled.

**Trust tiers and pinning**

| Tier | Source | Key | Consent |
|---|---|---|---|
| Bundled | `<install>\plugins\` | trusted by install location (no network fetch) | implicit |
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
├─ Host window (HWND always alive; off-screen and invisible until asked)
│   ├─ WS_EX_TOOLWINDOW while hidden; WS_EX_APPWINDOW while shown; ShowInTaskbar never toggled
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

### 5.1 Hidden-but-warm host, and the window you actually see

There is **one** window, not two. It is created with `ShowInTaskbar=true` — WPF gives a window with `ShowInTaskbar=false` a hidden owner, and toggling that property later recreates the HWND, which would destroy every webview in the tree. Instead, `WS_EX_TOOLWINDOW` is applied in `SourceInitialized` and the window is `Show()`n off-screen at `(-32000, -32000)`, 2×2 px, with `ShowActivated=false`. That keeps Chromium's composition target alive while the window stays out of the taskbar and Alt+Tab.

When the user asks for a service (tray click, Command Bar commit, toast click), the **same HWND** moves on-screen at its last bounds, the active webview fills it, and `SetWindowLongPtr` clears `WS_EX_TOOLWINDOW` and sets `WS_EX_APPWINDOW`, so it behaves like a normal app window (taskbar button, Alt+Tab entry). `Ctrl+W`, the close button and losing the last service returns it off-screen and re-applies `WS_EX_TOOLWINDOW`. `ShowInTaskbar` is never touched after creation. No reparenting, no window recreation, no renderer restart. Warm webviews that are not active stay parked at 1×1.

This is why visibility is **not** a lifecycle workaround: showing the window is a user action, and hiding it never unloads a warm webview. Invariant 5 in `AGENTS.md` is written that way, not as "never show a window".

**Summoning it while hidden.** A tray-only app cannot receive `Ctrl+Tab` when nothing has focus. A global `Ctrl+Alt+Space` summon was planned but is **deferred**; until it ships, tray left-click shows the window, after which `Ctrl+K` opens the bar. There is no silent broken summon path because there is no hotkey path yet.

Chromium treats off-screen/occluded content as backgrounded, which throttles JS timers and therefore kills WebSocket heartbeats. Countermeasures (both mandatory):

```
--disable-background-timer-throttling
--disable-renderer-backgrounding
--disable-backgrounding-occluded-windows
```

plus a 30 s host tick that re-asserts `Controller.IsVisible` and calls `TrySuspendAsync(false)`.

> **Phase 0 gate:** this must be proven. Log in once with the window shown (the deliberate sign-in exception; toast clicks are the only other user-requested show path in phase 0), hide it, then 3 hours tray-hidden with a real WhatsApp login, then an inbound message must toast. If it fails, the fallback is a 1×1 window parked at screen edge (behind everything) with `WS_EX_NOACTIVATE`, which is still invisible in practice.

### 5.2 Profiles

One `CoreWebView2Environment` per **profile**, not per instance. A profile is one `userDataFolder = %LOCALAPPDATA%\Chirpdesk\profiles\<profileId>`, and an instance's `profileId` defaults to its `instanceId`. Cookies, storage and logins never mix across profiles. Two WhatsApp accounts are two instances with two profiles, therefore two environments. Messenger and Facebook **may** optionally share a profile so one sign-in serves both (setting, default off); those two instances then **share the owner's environment object** — two environments cannot open the same `userDataFolder` at once — and deleting the owner requires unsharing first.

The per-profile environment is deliberate but not free: each one is its own browser process tree. Phase 1 records the RAM with 3 and 4 warm services; if 4 warm services exceed ~1.6 GB on this 12 GB machine, the documented fallback is one environment with multiple WebView2 profiles (cheaper, slightly weaker isolation). ADR 0005 carries that trigger.

### 5.3 Lifecycle

| State | Webview | Notification | Unread | Wake cost |
|---|---|---|---|---|
| Warm | loaded | yes | yes | 0 |
| Asleep | disposed | no | `asleep` | full reload (~3 s) |
| Cold | never created | no | `cold` | full load (~3 s) |

Warm-up order at boot: staggered 1.5 s apart, in user-defined priority. Selecting a cold service promotes it to warm **for the session only**; persisting is an explicit command. "Warm" never implies "visible": a warm service can be hidden, parked and toasting.

**Two axes that must not be confused:**

- **Profile** is not optional. Every service instance always owns its own isolated profile directory. Two accounts = two service entries = two profiles. There is no "shared profile" toggle except the deliberate Messenger↔Facebook pairing option.
- **Warm at boot** is one persisted boolean per service (`warmAtBoot`). Runtime state can differ from it: a service pinned warm can be manually slept until the next launch, and a cold service cannot be asleep (it has no webview to dispose).

Cold never means signed out — the profile stays on disk, so pairing/session survive indefinitely.

### 5.4 Cache trimming

On sleep and at boot, delete per profile: `Cache`, `Code Cache`, `GPUCache`, `DawnCache`, `ShaderCache`. **Keep** `Cookies`, `Local Storage`, `IndexedDB`, `Service Worker` — that's the session and the push registration. See §8.

Never trim a profile whose webview is still running: Chromium owns those files, and deleting under it risks corruption. Warm services are trimmed at the **next sleep or next boot**, not on a timer that would force a reload.

---

## 6. Notifications pipeline

```
site calls new Notification(...)  ──►  CoreWebView2.NotificationReceived
                                        │
                                        ├─ origin check: SenderOrigin must belong to this service
                                        ├─ Handled = true (suppress WebView2 default UI)
                                        ├─ boot cutoff: drop notifications older than process start
                                        ├─ dedupe (one per service instance): tag + sender/title + SHA-256(body) for 3 s
                                        ├─ warm-up gate: drop until the page has been ready for 5 s (time-based, never title-based: a message changes the title at the same instant it notifies)
                                        ├─ per-service mute / global DND filter
                                        └─► ToastNotification via AUMID
                                              shown ──► ReportShown
                                              click ──► ReportClicked + show service
                                              dismiss ──► ReportClosed
```

**What WebView2 actually intercepts.** `NotificationReceived` covers **non-persistent** notifications created with the `Notification()` constructor. Persistent notifications from `ServiceWorkerRegistration.showNotification()` are *not* exposed to the host — WebView2 renders its own UI and the host gets nothing (WebView2Feedback #5800). Do not assume this away.

Per-service capability probe, done in phase 0 and recorded per definition:

| Mode | How it works | When |
|---|---|---|
| `native` | `NotificationReceived` fires; full toast with title/body/icon | `new Notification()` services |
| `shim` | page-context override of `Notification` and `ServiceWorkerRegistration.prototype.showNotification` posts `{title, body, tag}` over `window.chrome.webview.postMessage` | service calls `showNotification` from the page |
| `title-only` | `DocumentTitleChanged` + unread count → generic toast ("New message in WhatsApp") | background service-worker push only; degraded but never silent |

The mode is a property of the service definition, overridable per instance, and shown in Settings. The shim is built-in host code, not a plugin permission: `inject-js` remains unavailable to plugins. If a site needs background push with content and WebView2 cannot provide it, that is an ADR-0003 failure recorded at the gate, not something to paper over.

Phase 0 measured WhatsApp Web as **`native`**: a real message on 2026-11-16 fired `NotificationReceived` with title and body, and the toast carried both (`docs/measurements.md`). No shim is needed for it; the mode field stays because the other services are still unmeasured.

- **Origin check:** a notification whose `SenderOrigin` is not the service origin or an allowed subdomain is dropped. Otherwise an ad iframe could toast.
- **AUMID:** `Chirpdesk` + a Start-menu shortcut carrying `System.AppUserModel.ID`, created on first run (IShellLink). Without it Windows shows "(unknown app)". Uninstall removes it.
- **Permissions:** auto-grant `Notifications` for configured chat origins on `PermissionRequested`; prompt once (custom dialog, remembered) for `Camera`/`Microphone`; deny `Geolocation` silently.
- **Sound:** per-service mute respected; otherwise the Windows toast sound plays.
- **DND:** toast suppressed, badge keeps counting; optional schedule. Notifications that expire during DND are dropped, not replayed.
- **OS-level block:** if Windows notifications are disabled for the app, `ToastNotifier.Setting` is detected and Settings says so instead of silently swallowing toasts.
- **Click when the app is not running:** a click from the Action Center activates the app through a per-user COM class (ADR 0009); the resident instance shows the toast's service, and if nothing is running the COM-launched process signals the app into existence. Cold-start deep links are still not promised.
- **Click destination:** a click opens the service the toast came from, not the last one used. It does **not** open the conversation: WebView2 exposes title, body, tag and icon, and a chat route cannot be derived from those reliably.
- **Deep link into a conversation:** a property of a **definition**, not of an arbitrary site. A definition may ship an `activation` template (`urlTemplate` over `{tag}`, with a required tag pattern), because only the definition knows the site's routing. An arbitrary site gets "opens the site" and no more; that asymmetry is deliberate and is the reason to ship definitions for services rather than only adding URLs.
- **Inline reply:** out of scope for v1 (COM activation + toast input). Click-to-open only.

---

## 7. Ad, tracker and script blocking (R11)

WebView2 gives us `WebResourceRequested` / `AddWebResourceRequestedFilter`, i.e. every request is inspectable **before** it leaves the process and can be cancelled. That is enough for a real blocker without a browser extension.

### 7.1 Three tiers, default is the safe one

| Tier | What it does | Default | Breakage risk |
|---|---|---|---|
| **1 — Curated** | Blocks known ad/telemetry/tracker hosts and `sponsored`/`ads` endpoints (~300–600 entries, shipped, per-service overrides) | **on** | very low; whitelisted endpoints can't be caught |
| **2 — Cosmetic** | Injected CSS hides promoted/sponsored/upsell containers, selectors shipped in the service definition's filter pack (mirrored locally), conservative selectors only | on, off per service | low; a bad selector hides the wrong element, never blocks a request |
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
2. A permanent allowlist covers auth, WebSocket and update endpoints, and the service's own origin is auto-allowlisted before any pack loads (`*/ws`, `*/ws/*`, `login`, `oauth`, `accounts`, service root domains). Nothing on the allowlist can be blocked by any tier, and no plugin can remove an entry.
3. `> blocking: off` for a single service, one command away, plus a tray hint on failure pointing at it.

### 7.4 Scripts

We do not strip site JavaScript — that's how WhatsApp's encryption init breaks. Instead:

- Block the **third-party script requests** that carry analytics and ad SDKs (tier 1/3).
- Optionally stub `fbq`, `gtag`, `dataLayer`, `_fbq` as no-ops (off by default; dead stubs can throw, and network blocking is cleaner).
- Optional per-service `injectCss` for clutter, separate from cosmetic filters, so a user tweak is never wiped by a filter-pack update.

---

## 8. Disk cleaning (R12)

The point is not only that Chirpdesk is small — it's that the chat apps **already on your machine** are hoarding space, and you should see and reclaim it from one place.

### 8.1 Per-profile storage view

Settings → Storage, and the Command Bar command `> storage`. The unit is the **profile**, because a shared profile is one folder: instances that share it are listed together on one row.

| Column | Meaning |
|---|---|
| Service | WhatsApp, Messenger, … (shared-profile instances share one row) |
| Data | cookies, `Local Storage`, `IndexedDB`, `Service Worker` — **never touched by a trim** |
| Cache | `Cache`, `Code Cache`, `GPUCache`, `DawnCache`, `ShaderCache` — the only reclaimable category |
| Last trim | timestamp |
| Blocked | requests blocked (from §7) |

Actions per service: **Trim cache** (safe) · **Sign out & reset** (clears everything, requires re-pairing, explicit destructive confirmation) · **Remove service + delete data** (Recycle Bin). Every action shows the exact path and byte count in a confirmation dialog first. Removing a dependent instance never deletes a shared profile; removing a profile owner is blocked until its dependents are unshared or removed.

There is no separate "clear media" action. WhatsApp keeps media blobs inside `IndexedDB`, so deleting "media" means deleting session storage — exactly what invariant 3 forbids as a trim. The UI reports the remaining profile size as **Data** and tells the user that reclaiming it means signing out; anything else would be a lie or a data-loss bug.

### 8.2 Automatic policy

- Trim caches on sleep and at boot; keep sessions.
- Per-profile cache budget (default 60 MB) and global budget (default 180 MB); when exceeded, trim the oldest **disposed** profiles first and log what was trimmed. Total profiles are expected to stay under 200 MB, per §2.1.
- Never trim while a webview is running. A warm service is trimmed when it next sleeps or when the app next starts; if the budget is exceeded while warm, Settings says which services need a sleep instead of forcing a reload.

### 8.3 Orphan scan ("desk space")

Finds chat-app data left behind by things installed before or no longer used:

- Chirpdesk profiles under `%LOCALAPPDATA%\Chirpdesk\profiles\` that no settings entry references.
- Official **WhatsApp desktop** data (`%APPDATA%\WhatsApp`, `%LOCALAPPDATA%\WhatsApp`, and the Store package's `%LOCALAPPDATA%\Packages\5319275A.WhatsAppDesktop_*\LocalState` when present), **Telegram Desktop** (`%APPDATA%\Telegram Desktop`), and Electron wrappers (Ferdium / Rambox / Station / MultiMessenger) — each from an enumerated root, never a glob over the user profile.
- Reports size, last-modified and owning app — per item, with a checkbox. Nothing pre-selected.

Safety rules, non-negotiable:

- Only paths from a **fixed allowlist of known roots**, resolved and re-verified as absolute, inside those roots, no wildcards, no parent traversal, no reparse-point escape.
- Deleting user data goes to the **Recycle Bin** (`FOF_ALLOWUNDO`); only cache subfolders are hard-deleted.
- Never touches mail stores, `Documents`, or Edge/Chrome profiles (those are reported as a hint with manual instructions, since no supported API can safely edit a live Chromium profile).
- Detects running processes of the owning app and refuses to delete a directory in use.
- A profile shared by two instances is never reported as orphaned while either instance exists.

### 8.4 Reclaimed counter

A running total on the storage page: *"Reclaimed 812 MB since install."* Cheap, makes trim habits visible, proves the feature works.

---

## 9. Unread + tray

- `CoreWebView2.DocumentTitleChanged` → parse the title with the service's `unreadRegex`. No injected script, no polling from the host, no `MutationObserver` shim (the host already gets the event).
- Per-instance `unreadRegex` (default `\((\d+)\)`, then a bare `(\d+)` fallback), editable; validated for exactly one capture group, with an invalid override falling back to the default and logging.
- If a definition has no usable title count, the badge shows a dot rather than guessing a number.
- Tray icon draws a dot variant when total > 0 (rendered at runtime, no assets); tooltip lists `WhatsApp 3 · Telegram 1`, truncated to the shell limit.
- Optional taskbar overlay badge when the window is visible.

---

## 10. Command Bar (R5, R6)

**One overlay. One component. No sidebar, no tab strip, no menu bar.** Everything navigational happens here.

### 10.1 Three entry modes

| Entry | Mode | Behaviour |
|---|---|---|
| `Ctrl+Tab` (hold) | **Cycle** | MRU list of **warm** services only, unread counts visible. `Tab` / `Shift+Tab` move the highlight, releasing `Ctrl` commits, `Esc` closes the palette. Query line hidden. |
| `Ctrl+K` | **Search** | Sticky bar with focused input. Rows: enabled services (open/asleep/cold) in MRU order, then **Settings** and **Plugins & services** commands. Fuzzy filter as you type. `Enter` activates, `Esc` closes the palette. |
| `Ctrl+Shift+K` | **Tabs** | Sticky list of open tabs, active tab highlighted. `Enter` switches, `Esc` closes. Same rows as `Ctrl+Tab`. |
| `Ctrl+K` while cycling | **Search** | The same open bar switches to search with the highlighted tab still selected (unified palette, one focus model). There is no "second `Tab`" gesture: with `Ctrl` still down another `Tab` is indistinguishable from the first `Ctrl+Tab` and from key auto-repeat. |

Rows are rendered by one template in every mode: icon · name · state badge (`open` / `asleep` / `cold`) · current-highlight.

Ordering: open services first, then asleep/cold, then commands; prefix beats substring, then input order (MRU for open rows).

### 10.2 Commands

`> theme dark` · `> theme light` · `> theme system` · `> theme <family> [variant|auto]` · `> settings` · `> add service <definition>` · `> plugins` · `> storage` · `> filters` · `> blocking: off` · `> warm <service>` · `> warm at boot <service>` · `> sleep <service>` · `> mute` · `> dnd 30m|1h|tomorrow` · `> reload` · `> copy url` · `> open in browser` · `> quit`

`> warm at boot <service>` toggles the persisted pin and the Settings switch reads/writes the same field; a dot on the service row marks "pinned warm" while the badge keeps showing live runtime state (`warm` / `asleep` / `cold`).

**Theme preview:** `> theme` lists **families**, with their variants revealed on `Tab`; arrowing applies the active variant live, `Enter` commits, `Esc` restores the previous family and variant. Switching to a family keeps your current mode (dark stays dark) and uses that family's default for it.

### 10.3 Keys

| Key | Action |
|---|---|
| `Ctrl+Alt+Space` (global) | **deferred** — not in v1; the tray icon shows the window when hidden |
| `Ctrl+Tab` (hold) | cycle warm services |
| `Tab` / `Shift+Tab` while held | move highlight |
| `Up` / `Down` while the bar is open | move highlight |
| `Ctrl+Tab` while any bar is open | move through that list |
| `Ctrl+K` while cycling | switch the same bar to search, keeping the highlighted tab |
| release `Ctrl` | commit the cycle |
| `Ctrl+K` | search mode |
| `Ctrl+Shift+K` | sticky open-tab list |
| `Ctrl+P` | passed through to the page (site print shortcut) |
| `Ctrl+1..9` | direct jump, settings order (power path, no UI) |
| `Ctrl+W` | close the highlighted tab in the palette, otherwise the active tab; last tab hides the window |
| `Esc` | close the palette or page; otherwise passed through to the site |

**Window behaviour:** opening a service shows the single window and switches it to a normal taskbar/Alt+Tab window; the close button and "hide" return it off-screen and restore `WS_EX_TOOLWINDOW`. Nothing unloads, so the webview keeps its scroll position and half-typed text. `Ctrl+W` closes a tab; deactivating a plugin sleeps its service instead. `Esc` never hides the window: inside the palette/page it goes back, and inside a site it stays the site's key.

**Settings and plugins interface.** Settings is a **page in the main window**, opened from the command palette or the tray; there is no separate settings window. The Plugins page lists installed service plugins with their own usage description and Active/Inactive state: **Activate** creates the first account and opens it, **Add account** creates another instance, **Deactivate** sleeps the runtimes without signing out. Browse/install UI is a placeholder until the signed repository lands in phase 5. Installed-but-inactive plugins also appear in the command palette and can be activated from there.

**Interception:** `Ctrl+K`, `Ctrl+Shift+K` and `Ctrl+Tab` are handled by one foreground-filtered low-level keyboard hook, because the WebView2 message hook did not fire reliably while the page had focus. Once the bar opens it handles `Tab`, arrows, `Ctrl+1..9`, `Ctrl+W`, `Esc` and text itself. `Ctrl+P` is the explicit passthrough. Cycle commit is driven by a 30 ms `GetAsyncKeyState(VK_CONTROL)` poll, because WebView2 does not reliably deliver modifier key-up when focus is inside the page. The global summon is deferred.

**Airspace:** WPF cannot paint over a WebView2 HWND. Opening the bar captures the last frame, collapses the active webview, and paints the frozen frame behind the bar's scrim; closing restores the live view. The control is not disposed, so scroll position and half-typed page text survive, and the renderer keeps its state. A second top-level overlay window was rejected: it adds focus, DPI and always-on-top failure modes for the same result.

### 10.4 Settings is a destination, not navigation

Settings is a page (URLs, regexes, appearance, storage, autostart, DND) opened from the palette or tray. No tabs, no rail, no command strip anywhere in the app.

---

## 11. Service model: definition + instance

A **definition** is a `service` plugin: shared, versioned, replaceable. An **instance** is what the user actually runs: one entry in `settings.json`, one profile, one unread count. The settings store owns instances; the plugin loader owns definitions.

`settings.json` (excerpt, schema v1):

```jsonc
{
  "schemaVersion": 1,
  "services": [
    {
      "instanceId": "whatsapp-work",       // ^[a-z0-9-]{1,32}$, unique
      "definitionId": "whatsapp",          // plugin id; null only on legacy rows from before ADR 0011
      "name": "WhatsApp (work)",           // display name
      "url": null,                         // override; null = definition url; legacy rows may carry an inline url
      "iconUri": null,                     // captured favicon or custom image
      "accent": null,                      // override of definition accent
      "warmAtBoot": true,
      "unreadRegex": null,                 // override; null = definition/default
      "userAgent": null,                   // override; null = definition or stock Edge UA
      "injectCss": null,                   // user tweak, separate from cosmetic packs
      "mute": false,
      "blocking": "default",               // default | off | +list
      "notificationMode": null,            // null = definition; native | shim | title-only
      "shareProfileWith": null,            // instanceId of profile owner
      "profileId": "whatsapp-work",        // normally == instanceId
      "order": 0
    }
  ]
}
```

A row with `"definitionId": null` and an inline `url` is a **legacy custom instance**: it still resolves fail-open, but nothing creates new ones (ADR 0011). An instance of a known definition leaves `url` null so a definition update from a repo takes effect; overriding the URL pins it. Known embedded-browser walls (Google sign-in, some Cloudflare challenges) are detected and replaced with an honest "this site blocks embedded sign-in — open it in your browser" hint plus an `open in browser` action. Cookies cannot be transferred between the browser and the webview, so we do not pretend that signing in elsewhere authenticates the instance.

**Popups and sign-in.** `NewWindowRequested` is handled explicitly: a popup the page opens for OAuth or `window.open` becomes a visible, transient WebView2 window using the **same profile**, with normal chrome, because the user clicked a button that asked for it. It closes independently and never replaces the service webview. Popups are never silently handed to the system browser; if a site refuses embedded browsers, the honest "this site blocks embedded sign-in — open it in your browser" hint is shown instead of pretending a browser sign-in transfers into the instance.

Add flow: install or enable a `service` definition plugin (bundled or from a repo) → the site appears in the catalogue → selecting it writes an instance (`definitionId` = plugin id, fresh profile), saved **cold** → offer "warm at boot". Two accounts of the same site = create a second instance of that definition; the second instance gets a fresh `instanceId` and profile. A site without a definition is not addable; write the plugin (see `docs/plugins.md`) instead. Messenger and Facebook stay **separate definitions** (`messenger.com` and `facebook.com`), and each supports multiple accounts as instances.

**Profile ownership.** `profileId` defaults to `instanceId`. `shareProfileWith` is recorded on the **dependent** instance (`"shareProfileWith": "facebook"`), and the owner sees the pairing in Settings and can revoke it — that revocation is the second side of the opt-in. The target is the owner. Deleting the owner requires unsharing or deleting both — otherwise the remaining instance silently loses its session.

Defaults: WhatsApp, Messenger, Telegram warm; Facebook cold. **Newly added instances start cold**, with the `warm at boot` switch offered immediately after first sign-in.

---

## 12. Design system

Zero permanent chrome means the design budget goes into the **Command Bar, tray menu, settings and title bar** — plus making themes first-class plugins.

Reference: Limusic's app. Near-black neutral base in the `#241c22` family, one restrained accent, no gradients, no glow, generous spacing, UI type at 12–13 px with wide tracking on labels.

**Space budget (a real constraint, not a taste):** the app is used at a small desk. The Command Bar is capped at **560 × 420 px**, anchored 12 % from the top, horizontally centred — never full-screen, never resizable. The conversation window opens at **980 × 720**, centred on the monitor holding the cursor, and remembers its last bounds per instance; it is never auto-maximised. Settings is **720 × 600** and resizable. Nothing may require a wide monitor or a second screen. These are asserted in the phase-1 UI test, not left to the designer's eye.

**Disk budget for development:** build output all lands in `artifacts/` (`UseArtifactsOutput`), so a clean is one folder and there are no per-project `bin`/`obj` duplicates. No Visual Studio, no Windows SDK, no MSIX tooling, no fixed-version WebView2 runtime — the SDK plus two NuGet packages are the whole toolchain.

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

Token names are CSS-variable compatible so the same token JSON can drive a future non-WPF surface (web or mobile UI) without rewriting palettes — the one structural trick worth copying from Limusic. The WPF shell itself consumes each token as a `DynamicResource` key (`Cd.Bg.Window`, `Cd.Text.Primary`, …); the `--cd-*` names are generated for the optional injected-CSS path and for that future surface, not used to style the shell.

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

Selected **family** is stored in settings; selected **variant per mode** is derived from `defaults`, so a family added later can opt into "follow system" without a migration. The settings shape is explicit rather than implied:

```jsonc
"theme": {
  "family": "theme-catppuccin",   // plugin id
  "mode": "auto",                 // auto | dark | light
  "pinnedVariant": null           // e.g. "mocha"; null = family default for the active mode
}
```

`pinnedVariant` wins over `mode` only while that variant's mode matches; if it doesn't, the family default for the active mode applies and Settings flags the mismatch rather than pinning a light variant in dark mode.

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

Sites own their CSS. On every theme change we set `CoreWebView2Profile.PreferredColorScheme`, so services honouring `prefers-color-scheme` follow your light/dark choice. Optional per-instance `injectCss` goes further; a few examples ship, clearly best-effort.

### 12.5 Typography + motion

Segoe UI Variable for UI; `Cascadia Mono` for the Command Bar query line, falling back to `Consolas` (Windows 10 does not always ship Cascadia) — the "hacker" flavour lives there, not in decoration. Overlay open/close 90 ms fade + 4 px rise. No other motion. When Windows high-contrast mode is on, the baseline high-contrast palette wins over any theme plugin.

---

## 13. Data locations

| Path | Contents |
|---|---|
| `%APPDATA%\Chirpdesk\settings.json` | schemaVersion + service instances, appearance, autostart, DND, blocking, update channel |
| `%APPDATA%\Chirpdesk\plugins\<id>\` | user-installed plugins of any kind |
| `%APPDATA%\Chirpdesk\filters\compiled\` | compiled matchers from imported lists |
| `%APPDATA%\Chirpdesk\updates\` | downloaded installer + manifest, deleted after apply |
| `%LOCALAPPDATA%\Chirpdesk\profiles\<profileId>\` | per-profile WebView2 data |
| `%LOCALAPPDATA%\Chirpdesk\icons\` | cached favicons |
| `%LOCALAPPDATA%\Chirpdesk\logs\` | rolling log, 2 MB cap |

Settings writes are atomic (temp + rename); a corrupt file falls back to defaults with a logged warning. Migrations are explicit and one-way: `schemaVersion` is bumped only when a loader exists for the old shape, and a newer file is never silently downgraded.

---

## 14. Shipping and updates (phase 6)

- **Installer:** Inno Setup, per-user, no elevation, `setup.exe`; a plain `.zip` portable build for people who want no installer. No MSIX, no Store. The installer checks for the .NET 8 Desktop Runtime and the WebView2 Evergreen Runtime. WebView2 installs per-user without elevation; the .NET Desktop Runtime is machine-wide, so if it is missing setup shows a blocking prerequisite page with the official download and an optional "install it for me" step that elevates through the signed Microsoft bootstrapper. Chirpdesk itself never requires admin. The portable zip documents both requirements.
- **Uninstall** removes app files and the AUMID shortcut. Profiles are user data: the uninstaller offers a clearly-labelled "also delete my Chirpdesk data" checkbox (default off) and sends it to the Recycle Bin.
- **Shortcut creation** at install time carries the `System.AppUserModel.ID` used by toasts and the updater; uninstall removes it.
- **Self-update:** the app checks GitHub Releases (stable channel only), downloads `Chirpdesk-Setup.exe` + manifest, verifies an **ECDSA P-256 signature** and SHA-256 of the payload, then runs the installer silently and relaunches. Unsigned, malformed or unparseable manifests are refused and logged; the app keeps running the current version. A manifest whose version is not newer than the running build is also refused, so a valid old release cannot be replayed as a downgrade. Before applying, the app disposes every WebView2 environment and exits cleanly — the installer never runs over live profile locks.
- **Why ECDSA, not Ed25519:** .NET 8/9/10 still ship no Ed25519 implementation; using it would mean BouncyCastle (a third runtime package) or NSec (native libsodium, a native dependency). `ECDsa` over NIST P-256 is in-box, FIPS-friendly and signs SHA-256. Keys are SPKI PEM; signatures are fixed 64-byte IEEE P1363, base64 in the `.sig` file. This is a deliberate downgrade in signature scheme, not in verification rigor.
- **Opt-in:** update checks can be disabled; nothing is ever downloaded without a signature match. Portable builds never self-update.
- **CI:** one GitHub Actions workflow on `windows-latest` that builds, runs tests, packs the installer + zip and attaches them to a Release with the signature. Release-only — never used for development.
- **No landing page.** Repo README is the only public surface for now.

---

## 15. Phases and acceptance criteria

| Phase | Scope | Done when |
|---|---|---|
| **0 — Spine** | SDK install, project skeleton, hidden host + one-shot visible login path, one service (WhatsApp), toast pipeline + AUMID, notification capability probe, single instance | Log in once, hide, 3 h tray-hidden, inbound message toasts with correct text and icon; toast click shows WhatsApp in the window; window never appears unprompted; Alt+Tab clean while hidden |
| **1 - Command Bar** | plugin loader + manifest validation (service definitions and themes), definition/instance settings model, multi-service hosting, per-profile environments, Command Bar (one palette: cycle + Ctrl+K search over open tabs), show/hide window styles, cold/warm rows, unread counts, `Ctrl+1..9`, `Ctrl+W` | `Ctrl+Tab` cycles warm services in the palette; `Ctrl+K` lists open tabs only and `type tel → Enter` focuses Telegram; cold services and commands are not in the palette yet; half-typed text survives switching; two instances of one definition keep separate sessions |
| **2 — Lifecycle + storage** | warm/cold at boot, sleep, autostart with 20 s delay, mute + DND, settings window, instance creation from installed definitions, per-instance regex, storage scan/trim/orphan primitives, cleaner `tool` panel | Reboot with 3 warm + 1 cold: notifications within 30 s, no storm, no window; disk stays under 200 MB after a week; orphan scan finds a real leftover folder and restores it from the Recycle Bin |
| **3 — Blocking** | request matcher, curated tier-1 pack, cosmetic packs, per-service counters, allowlist, enable/disable commands | WhatsApp + Messenger + Telegram log in and send messages with blocking on; counters show blocked requests; `> blocking: off` for one service restores every request |
| **4 — Design** | one baseline palette with dark and light ends (ADR 0010), token pipeline, system light/dark following, live preview, contrast validator, high-contrast fallback | Theme switch is instant; every variant passes AA; dark reaches the hosted page only where the definition opts in (`pageDarkMode: auto`), never on a site with its own theme. Colour families and theme plugins are deferred until after v1. |
| **5 — Plugin ecosystem** | catalogue expansion: 10 theme families, extra service definitions and filter packs; plugin manager UI, repo add/list/remove/sync, signature + hash verification, TOFU pinning, install/update/uninstall, conflict resolution between repos, `> repo` commands | Adding the official repo installs a newer filter pack and a theme without a rebuild; a repo with a changed key is blocked until re-trusted; a corrupt archive is rejected and the app still starts |
| **6 - Ship** | Inno Setup installer with .NET 8 Desktop + WebView2 runtime checks, portable zip, self-updater with ECDSA verification and downgrade refusal, first-run overlay teaching the tray/`Ctrl+Tab`/`Ctrl+K`, CI release workflow, README docs | Fresh machine with neither runtime: setup detects both, explains or installs them with consent, then the app runs; install a definition plugin, sign in, reboot, receive toast, switch theme, run a cleanup; second release updates itself and refuses a tampered or older manifest |
| **7 — Optional** | ABP list import (tier 3), out-of-process integration plugin host (TDLib as first integration), live preview during cycle, inline toast reply | only if v1 proves itself in daily use |

Each phase ends with a build you can actually run.

---

## 16. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| Off-screen window throttled despite flags | R2 fails outright | Phase 0 gate; fallback 1×1 `WS_EX_NOACTIVATE` at screen edge |
| Toggling `ShowInTaskbar` recreates the HWND | Every hosted webview is destroyed on show/hide | Never change it after creation; toggle `WS_EX_TOOLWINDOW`/`WS_EX_APPWINDOW` instead (ADR 0003) |
| WPF cannot paint over a WebView2 HWND | Command Bar renders behind the page | Collapse the active webview while the bar is open; integration check in phase 1 |
| `NewWindowRequested` left unhandled | OAuth/sign-in popups lost or silently sent to the system browser | Same-profile, user-initiated popup window; explicit handling, never silent external |
| Blocking breaks a login or WebSocket | Service unusable, worse than ads | Fail-open, permanent auth/WS allowlist, one command to disable per service, counters for diagnosis |
| Cosmetic selector hides the wrong thing | Confusing UI | Conservative selectors, per-service toggle, never touches requests |
| Cache deletion logs the user out | Re-pairing WhatsApp | Cache and data are separate directories, separate buttons, separate confirmations |
| Orphan scan deletes something it shouldn't | Data loss | Fixed root allowlist, path re-verification, Recycle Bin for user data, in-use detection, nothing pre-checked |
| Tampered update package | Code execution | ECDSA P-256 + SHA-256 verification, refuse and log on mismatch, updates opt-in |
| Signed old release replayed as a downgrade | Runs a vulnerable build | Refuse a manifest whose version is not newer than the running build |
| Malicious plugin | Session theft, keylogging | v1 plugins are data-only (no code path exists); code only out-of-process with explicit consent; no in-process assembly loading, ever |
| Plugin schema drift | Community plugins break each release | `apiVersion` validation, unknown fields ignored, only additive changes within a major version, schema published |
| Broken plugin hides a service | User thinks the app lost their account | Invalid plugins are skipped and flagged; disabling a definition never deletes an instance or profile |
| Malicious filter pack | Blocks logins, or beacons out via CSS | Host rules cannot touch the global allowlist; service origin auto-allowlisted; CSS sanitised (no `@import`, no remote `url()`); permissions listed before install |
| Compromised plugin repo | Serves a tampered plugin | ECDSA-signed index, pinned per-repo key, explicit re-trust prompt on key change, hash from the signed index, zip-slip rejection, no silent installs |
| Repo outage or stale index | Filters age, manager errors | Cached index (24 h TTL), installed plugins keep working offline, failures logged not fatal |
| `Ctrl+K` avoids the sites' print shortcut | Minor | `Ctrl+P` passthrough documented |
| Meta changes the web client / sniffs the UA | Service breaks | Stock Edge UA like Meta's own Windows app; per-service UA override |
| Google sign-in refuses embedded browsers | Some services unusable | Detect + explain; open-in-browser escape hatch |
| Service-worker notifications differ from page notifications | Missed toasts | Phase 0 capability probe per service; `native` → `shim` → `title-only` fallback; mode visible in Settings |
| Global hotkey deferred | no summon while another app has focus | users ask for it often enough that the tray becomes friction; then `RegisterHotKey` + failure reporting returns |
| Toast clicked while the app is closed | Cold start without deep link | v1 is autostart-resident; click launches the app and shows the service, but deep-link activation is a later COM activator |
| Windows 10 has no WebView2 Runtime | App cannot start | Installer installs the Evergreen bootstrapper; portable zip documents the requirement |
| Trimming under a running webview | Cache/profile corruption | Trim only disposed profiles; warm services trim at next sleep or boot; never on a timer |
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
├─ plugins/                   # every bundled content pack, one folder each
│  ├─ whatsapp/               # kind: service (definition)
│  ├─ theme-dracula/          # kind: theme
│  └─ filters-meta/           # kind: filter
├─ registry/                  # the official plugin repo: index.json + index.json.sig
├─ schemas/                   # plugin.json / service / theme / filter / panel JSON Schemas
├─ installer/                 # Inno Setup script, public signing keys only
├─ src/Chirpdesk.Core/         # pure logic: parsing, policy, settings, planners, ILog
├─ src/Chirpdesk/              # WPF + WebView2 shell
│  ├─ App.xaml(.cs)           # startup, single instance, autostart
│  ├─ Plugins/                # discovery, manifest validation, registry, hot rescan
│  ├─ Hosting/                # window show/hide, webview manager, profiles
│  ├─ Services/               # definitions vs instances, add-URL flow, store
│  ├─ Notifications/          # toast pipeline, AUMID, capability probe, permissions
│  ├─ Filtering/              # matcher, list parser, counters
│  ├─ Storage/                # size scan, trim, orphan scan
│  ├─ Updates/                # release check, ECDSA verify, apply
│  ├─ Ui/CommandBar/          # cycle + search modes, one row renderer
│  ├─ Ui/                     # title bar, settings window, first-run overlay
│  └─ Theming/                # theme plugin loader, token mapping, contrast check
├─ tests/Chirpdesk.Core.Tests/ # xUnit, FakeClock, fixtures
├─ docs/                      # decisions, screenshots, service notes
└─ .github/workflows/release.yml
```

Dev environment: local only, .NET 8 SDK, no Visual Studio, no full Windows SDK, no MSIX. Peak dev disk ~2 GB; app footprint ~5 MB. Windows 10 additionally needs the WebView2 Evergreen Runtime at runtime, not at build time.

---

## 18. Decided / open

Decided:

- License **MIT**; repo stays **private** until v1 works.
- Stack **WPF + WebView2**; evaluate-and-reject recorded in §4.
- One Command Bar for navigation, commands and theme switching.
- **Plugin-shaped architecture**: shipped service **definitions**, themes (with dark+light variants), filter packs and bundled tool panels are plugins with one manifest; data-only in v1; code only out-of-process, phase 7. **A site exists only as a service plugin; running it is an instance in `settings.json` (ADR 0011).**
- **Shell first**: v1 ships the loader, Command Bar, host, notifications and storage primitives, with a minimal bundled catalogue; the rest of the catalogue lands as plugins.
- **Visibility is user-requested**: one window, hidden at boot, shown on tray/Command Bar/toast, never shown to fix a lifecycle problem.
- **Global summon hotkey deferred**; tray left-click is the entry while hidden.
- Adblock packs ship as `filter` plugins, maintained in the repo's `plugins/` + `registry/`, installable from the official repo or any user-added repo.
- **Updates and repo indexes sign with ECDSA P-256 + SHA-256** because .NET has no Ed25519 implementation; no extra crypto package.
- `Ctrl+Tab` cycles, `Ctrl+K` searches, `Ctrl+Shift+K` lists open tabs.
- No sidebar, tab strip, menu bar or landing page.
- Self-updating signed installer, opt-in, stable channel only.
- Defaults: WhatsApp, Messenger, Telegram warm; Facebook cold.

Open:

- Title bar: minimal custom bar showing `service · unread`, or none at all.
- `Ctrl+1..9` direct jump: keep or drop for a pure two-key model.
- Blocking default: tier 1 + cosmetic on, list import off (assumed).
- Deep-link toast activation when the app is closed (COM activator, later).

---

## 19. Trade-offs accepted, and what would make us revisit

Every entry is a cost we are knowingly paying. If the revisit trigger fires, the decision is re-opened deliberately, not silently drifted.

| Decision | Cost we accept | Revisit if |
|---|---|---|
| WebView2 hosting instead of native protocol clients | 150–400 MB RAM per warm service; we inherit whatever Meta ships | TDLib-class clients appear for WhatsApp/Messenger, or a service publishes an official API |
| One window that hides off-screen and is shown on demand | show/hide has to toggle taskbar/Alt+Tab styles correctly; the hidden HWND is the liveness anchor | two consecutive reports of the window refusing to show or losing its taskbar button |
| Per-profile `CoreWebView2Environment` | more processes, more RAM, slower first boot | total RAM with 4 warm services exceeds ~1.6 GB on a 12 GB machine → evaluate multi-profile single environment |
| `NotificationReceived` only covers non-persistent notifications | service-worker `showNotification` needs a shim or degrades to title-only | a core service uses background SW push with content and neither shim nor title fallback satisfies R4 |
| ECDSA P-256 instead of Ed25519 | slightly larger signatures/keys; not the modern default | .NET ships Ed25519, or a crypto dependency is approved by ADR |
| Every site is a definition plugin, and instances are settings rows (ADR 0011) | a site with no plugin cannot be added; a one-off site needs a plugin written first | users need a one-off site often enough that authoring a plugin is the blocker; provide a generator/template or a "make a plugin from this URL" helper |
| Installing a service plugin does not make an instance | one extra step to run a site; two accounts are still two instances | users expect install = sign in, or the instance step is invisible enough to feel broken |
| Tool plugins are bundled-only and cannot add capability | the cleaner is data but not user-extensible in v1 | a real third-party tool need appears that the host primitive set can express safely |
| Fail-open filtering | some ads slip through when a filter file is broken | a service breaks *because* of a filter and fail-open did not prevent it |
| Cosmetic filtering with CSS selectors | selectors rot when sites redesign | more than one breakage per month per service |
| No auto-update in v1 → signed self-update after | users on old builds for longer | v1 sees daily use, or a security fix needs shipping fast |
| Custom WPF chrome instead of native title bar | DPI and window-chrome edge cases are ours to handle | visible chrome glitches on multi-monitor or fractional scaling |
| Command Bar as sole navigation | discoverability depends on the first-run overlay and the tray | a user cannot find settings after the overlay is dismissed |
| One shared cookie jar *within* a service instance | two logins for the same site need two entries | users routinely need more than two accounts per site |
| MIT, private repo, solo maintenance | no external contributors during v1 | v1 ships and is public — then CONTRIBUTING and issue templates get added |
| Plugin manifest is a published schema | compatibility burden on every field | a second consumer of the schema appears (mobile, web) or community plugins break on a minor bump |
| Users can add third-party plugin repos | supply-chain trust is partly delegated to them | repo key pinning plus an explicit fingerprint prompt; only data plugins are installable in v1 |

