# Chirpdesk — Technical Plan

**All your conversations.**

Status: **planning complete, implementation not started.**
License: MIT. Repo: private until v1 works.
Target: Windows 10 1809+ / Windows 11, x64 and arm64.

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
| R9 | Theme system: many popular schemes, dark **and** light, live switch | Token-based scheme pipeline, JSON scheme files |
| R10 | Small on disk and RAM | WPF + WebView2, framework-dependent, cache trimming |
| R11 | **Block ads, trackers and junk scripts** in the web clients | Request-level filter engine + cosmetic CSS, fail-open |
| R12 | **See and reclaim disk** used by these chat apps | Per-service size breakdown, safe cleanup, orphan scan |

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

`> theme dark` · `> theme light` · `> theme system` · `> theme <name>` · `> settings` · `> add service` · `> edit service` · `> storage` · `> filters` · `> blocking: off` · `> warm <service>` · `> sleep <service>` · `> mute` · `> dnd 30m|1h|tomorrow` · `> reload` · `> copy url` · `> open in browser` · `> quit`

**Theme preview:** `> theme` lists schemes; arrowing applies live, `Enter` commits, `Esc` restores the previous scheme. This is where the colour system shows off.

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

Settings stays a small window (URLs, icons, regexes, scheme files, storage, autostart, blocking) reached only by `> settings`. No tabs, no rail, no command strip anywhere in the app.

---

## 11. Service model

```jsonc
{
  "id": "whatsapp",
  "name": "WhatsApp",
  "url": "https://web.whatsapp.com/",
  "iconPath": null,            // cached favicon, or custom image
  "accent": "#25D366",         // optional brand override for scheme accent
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

Defaults: WhatsApp, Messenger, Telegram warm; Facebook cold.

---

## 12. Design system

Zero permanent chrome means the design budget goes into the **Command Bar, tray menu, settings and title bar** — plus making colour schemes first-class.

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

Token names are CSS-variable compatible so the same scheme JSON can drive a future non-WPF surface (web or mobile UI) without rewriting palettes — the one structural trick worth copying from Limusic.

### 12.2 Scheme files

Schemes are **data**, not code: `schemes/<id>.json` with `id`, `name`, `author`, `kind` (`dark`/`light`), `tokens{}`, optional sparse `overrides{}`. The loader validates, computes contrast, and maps tokens onto a WPF `ResourceDictionary` keyed `Cd.<token>`, applied with `DynamicResource` so switching is instant — no restart, no flicker.

Built-in families (dark + light where defined):

| Family | Variants |
|---|---|
| Chirpdesk | Default Dark, Default Light |
| System | follows Windows app theme |
| Dracula | Dracula, Dracula Soft |
| Catppuccin | Mocha, Macchiato, Frappé, Latte |
| Nord | Nord, Nord Light |
| Tokyo Night | Night, Storm, Day |
| Gruvbox | Dark Hard, Light |
| Rosé Pine | Main, Moon, Dawn |
| Solarized | Dark, Light |
| GitHub | Dark, Light |

Selection: `> theme`, or Settings → Appearance. "Follow system" flips light/dark on `WM_SETTINGCHANGE`.

### 12.3 Contrast + accessibility

Every scheme is validated at load: text/background pairs must clear **WCAG AA 4.5:1** (3:1 for large/UI). Failures are nudged in lightness by the minimum passing amount and reported in Settings, so custom schemes are flagged rather than silently unreadable. Focus rings are never colour-only; selection changes fill **and** weight.

### 12.4 Site appearance

Sites own their CSS. On every theme change we set `CoreWebView2Profile.PreferredColorScheme`, so services honouring `prefers-color-scheme` follow your light/dark choice. Optional per-service `injectCss` goes further; a few examples ship, clearly best-effort.

### 12.5 Typography + motion

Segoe UI Variable for UI; `Cascadia Mono` for the Command Bar query line — the "hacker" flavour lives there, not in decoration. Overlay open/close 90 ms fade + 4 px rise. No other motion.

---

## 13. Data locations

| Path | Contents |
|---|---|
| `%APPDATA%\Chirpdesk\settings.json` | services, appearance, autostart, DND, blocking, update channel |
| `%APPDATA%\Chirpdesk\schemes\*.json` | user scheme overrides |
| `%APPDATA%\Chirpdesk\filters\` | cosmetic packs, imported lists, compiled matchers |
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
| **1 — Command Bar** | multi-service hosting, per-service profiles, Command Bar with cycle + search modes, cold/warm rows, unread counts, `Ctrl+1..9`, `Ctrl+W` | `Ctrl+Tab` cycles warm services; hold → type → filter → `Enter` works as one continuous interaction; cold services appear in search but never in cycle; half-typed text survives switching |
| **2 — Lifecycle + storage** | warm/cold at boot, sleep, autostart with 20 s delay, mute + DND, settings window, add-URL flow, per-service regex, storage panel, trim policy, orphan scan | Reboot with 3 warm + 1 cold: notifications within 30 s, no storm, no window; disk stays under 150 MB after a week; orphan scan finds a real leftover folder and restores it from the Recycle Bin |
| **3 — Blocking** | request matcher, curated tier-1 pack, cosmetic packs, per-service counters, allowlist, enable/disable commands | WhatsApp + Messenger + Telegram log in and send messages with blocking on; counters show blocked requests; `> blocking: off` for one service restores every request |
| **4 — Design** | token pipeline, scheme loader, 10 families, system light/dark, live preview from `> theme`, contrast validator, `PreferredColorScheme` sync | Theme switch is instant and previewable from the bar; shipped schemes pass AA; a dropped-in scheme file appears in `> theme` |
| **5 — Ship** | Inno Setup installer, portable zip, self-updater with signature verification, first-run overlay teaching `Ctrl+Tab`/`Ctrl+P`, CI release workflow, README docs | Fresh machine: run setup, add service, sign in, reboot, receive toast, switch theme, run a cleanup; second release updates itself and refuses a tampered manifest |
| **6 — Optional** | ABP list import (tier 3), Telegram via TDLib, live preview during cycle, inline toast reply | only if v1 proves itself in daily use |

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
├─ schemes/                   # shipped colour schemes (JSON)
├─ filters/
│  ├─ curated/                # tier-1 host lists per service
│  └─ cosmetic/               # per-service CSS packs
├─ installer/                 # Inno Setup script, signing keys (public only)
├─ src/Chirpdesk/
│  ├─ App.xaml(.cs)           # startup, single instance, autostart
│  ├─ Hosting/                # hidden window, webview manager, profiles
│  ├─ Services/               # definitions, store, add-URL flow
│  ├─ Notifications/          # toast pipeline, AUMID, permission policy
│  ├─ Filtering/              # matcher, list parser, counters
│  ├─ Storage/                # size scan, trim, orphan scan
│  ├─ Updates/                # release check, signature verify, apply
│  ├─ Ui/CommandBar/          # cycle + search modes, one row renderer
│  ├─ Ui/                     # title bar, settings window, first-run overlay
│  └─ Theming/                # scheme loader, tokens, contrast check
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
- `Ctrl+Tab` cycles, `Ctrl+P` searches; `Ctrl+K` removed.
- No sidebar, tab strip, menu bar or landing page.
- Self-updating signed installer, opt-in, stable channel only.
- Defaults: WhatsApp, Messenger, Telegram warm; Facebook cold.

Open:

- Title bar: minimal custom bar showing `service · unread`, or none at all.
- `Ctrl+1..9` direct jump: keep or drop for a pure two-key model.
- Blocking default: tier 1 + cosmetic on, list import off (assumed).
