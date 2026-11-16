# Chirpdesk — Technical Plan

**All your conversations.**

Status: **awaiting approval. No implementation yet.**
Target: Windows 10 1809+ / Windows 11, x64 and arm64.

---

## 1. What this is

A tray application that hosts the web versions of chat services in a single window with no permanent chrome, keeps chosen services loaded (and therefore notifying) in the background, turns their web notifications into real Windows toasts, blocks the ads and trackers those sites drag in, and gives you a place to see and reclaim the disk they consume.

It exists because every alternative is an Electron app that ships its own Chromium copy (MultiMessenger: 325 MB, Nexus/Ferdium: ~250–300 MB), and because the rest of the "lightweight" options can't do notifications without a page open.

---

## 2. Locked requirements

| # | Requirement | Implication |
|---|---|---|
| R1 | Starts at boot, **tray only** — no window, no taskbar entry | Hidden host window + login-time autostart |
| R2 | Selected services are **warm** at boot and notify even if never opened | Real loaded webviews, background throttling disabled |
| R3 | Services not selected stay **cold** until invoked | Webview created on demand, spinner while loading |
| R4 | Notifications = native Windows toasts, no backlog storm at login | `NotificationReceived` → toast, dedupe, warm-up suppression |
| R5 | Switching is `Ctrl+Tab` only; **no sidebar/tab strip anywhere** | Zero-chrome window, MRU overlay while held |
| R6 | `Ctrl+K` command palette (search services + run actions) | Fuzzy palette with `>` / `@` scopes |
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
- No general-purpose adblocker for your browser — this blocks inside Chirpdesk's webviews only.
- No inbound call guarantee (WebRTC works only if the embedded permission flow allows it — see risks).
- No auto-update in v1 (portable build; updater is a later phase).

---

## 4. Stack decision

| Candidate | Verdict | Reason |
|---|---|---|
| **WPF + WebView2 (chosen)** | ✅ | Both runtimes already on Windows; ~5 MB app; `CoreWebView2.NotificationReceived` gives page **and** service-worker notifications; full control of profiles, permissions, background flags **and** request filtering |
| WinUI 3 | ✗ | Adds Windows App SDK dependency (~50–80 MB) for no feature we need |
| Tauri (Rust) | ✗ | Smaller binary, but `wry` exposes no notification interception and no `WebResourceRequested`; needs 1.5–2.5 GB toolchain |
| Electron | ✗ | Defeats the premise (bundles Chromium, 200 MB+) |
| Thunderbird add-on | ✗ | `tabhide` unsupported, one shared cookie jar, no media permissions, no request filtering, Meta "browser unsupported" |

Target framework: `net8.0-windows10.0.19041.0` (WinRT toast projections without extra packages), `UseWPF=true`, published **framework-dependent**.

Third-party dependencies, deliberately few: `Hardcodet.NotifyIcon.Wpf` (MIT, tray). Filter lists are parsed by our own code.

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
│   └─ Overlays: Ctrl+Tab list, palette, dim scrim
│
├─ Tray icon (unread state, menu, DND)
├─ Toast pipeline (WinRT, AUMID, click → activate service)
├─ Request filter engine (shared matcher, per-webview hooks)
├─ Storage service (size scan, trim, orphan scan)
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

Warm-up order at boot: staggered 1.5 s apart, in user-defined priority. Selecting a cold service promotes it to warm **for the session only**; persisting is an explicit palette command.

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
                                              click ──► activate Chirpdesk, focus that service
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
| **1 — Curated** | Blocks known ad/telemetry/tracker hosts and `sponsored`/`ads` endpoints (~300–600 entries, shipped with the app, per-service overrides) | **on** | very low, and whitelisted endpoints can't be caught |
| **2 — Cosmetic** | Injected CSS hides promoted/sponsored/upsell containers per service (`filters/cosmetic/<service>.json`), conservative selectors only | on, off per service | low; a broken selector only hides the wrong element, never blocks a request |
| **3 — List import** | Opt-in import of ABP-syntax lists (EasyList Lite, EasyPrivacy). Parsed offline into a compact domain trie. | off | medium — hence opt-in, per-service, and easy to disable |

Tier 3 syntax supported: `||host^`, host/path patterns, `@@` exceptions, `$domain=`, `$third-party`, `$script|image|xmlhttprequest`. Explicitly **unsupported and skipped**: `$csp`, `$redirect`, `$removeparam`, `##`/`#@#` cosmetic syntax from lists (we use our own cosmetic file format). Unsupported rules are counted and shown, never silently mangled.

### 7.2 Engine

- One compiled matcher in the host process: hostname trie for `||host^` rules + a small ordered regex list for path rules.
- Every decision cached per host in a dictionary, so steady-state cost is a hash lookup: **O(1) per request**, no measurable load impact.
- Registered per webview with `AddWebResourceRequestedFilter("*", All)`; the filter consults resource context too, so "block trackers' images and XHR but not their fonts on the app's own domain" is expressible.
- Counters per service (blocked requests, blocked bytes estimate) surfaced in Settings — transparency and the first debugging tool when a service misbehaves.

### 7.3 Fail-open, always

Three hard rules, because a chat client that can't log in is worse than an ad:

1. If the matcher throws, the filter file is invalid, or anything is ambiguous → **allow the request**.
2. A permanent allowlist covers auth, WebSocket and update endpoints (`*/ws`, `*/ws/*`, `login`, `oauth`, `accounts`, service root domains). Nothing on the allowlist can be blocked by any tier.
3. Settings has **"disable blocking for this service"** which is one click away, and every toast-level failure shows a tray hint pointing at it.

### 7.4 Scripts

We do not strip site JavaScript — that's how you break WhatsApp's encryption init. What we do instead:

- Block the **third-party script requests** that carry analytics and ad SDKs (tier 1/3).
- Optionally stub `fbq`, `gtag`, `dataLayer`, `_fbq` as no-ops (off by default; dead stubs can throw, and network blocking is cleaner).
- Optional per-service `injectCss` for clutter, separate from cosmetic filters, so a user tweak can never be wiped by a filter-pack update.

---

## 8. Disk cleaning (R12)

The point is not only that Chirpdesk is small — it's that the chat apps **already on your machine** are hoarding space, and you should be able to see and reclaim it from one place.

### 8.1 Per-service storage view

Settings → Storage, and the palette command `> storage`:

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

### 8.3 Orphan scan (the "desk space" part)

Finds chat-app data left behind by things you installed before or no longer use:

- Chirpdesk profiles belonging to removed services.
- Official **WhatsApp desktop** WebView2 data (`%APPDATA%\WhatsApp`, `%LOCALAPPDATA%\WhatsApp`), **Telegram Desktop**, and Electron wrappers (Ferdium / Rambox / Station / MultiMessenger profile dirs) if present.
- Reports size, last-modified, and which app owns it — per item, with a checkbox. Nothing is pre-selected.

Safety rules, non-negotiable:

- Only paths from a **fixed allowlist of known roots**; resolved and re-verified as absolute, inside those roots, no wildcards, no parent traversal.
- Deleting user data goes to the **Recycle Bin** (`FOF_ALLOWUNDO`) so a mistake is recoverable; only cache subfolders are hard-deleted.
- Never touches mail stores, `Documents`, browser profiles of Edge/Chrome (those are reported as a hint with a "how to clear site data yourself" note, since no supported API can safely edit a live Chromium profile).
- Running these app's own processes are detected and the scan refuses to delete a directory in use.

### 8.4 Reclaimed counter

A running total on the storage page: *"Reclaimed 812 MB since install."* Cheap to implement, makes trim habits visible, and proves the feature works.

---

## 9. Unread + tray

- Injected script per service: `MutationObserver` on `document.title`, posts `{count, title}` over `chrome.webview.postMessage`. No polling from the host.
- Per-service `unreadRegex` (default `\((\d+)\)` then `(\d+)`), editable.
- Tray icon draws a dot variant when total > 0 (rendered at runtime, no assets); tooltip lists `WhatsApp 3 · Telegram 1`.
- Optional taskbar overlay badge when the window is visible.

---

## 10. Keyboard model

| Key | Action |
|---|---|
| `Ctrl+Tab` (hold) | MRU overlay: icon · name · unread, warm services only |
| `Tab` / `Shift+Tab` while held | move highlight |
| release `Ctrl` | commit |
| `Esc` while held | cancel |
| `Ctrl+K` | palette |
| `Ctrl+1..9` | direct jump (fixed order) |
| `Ctrl+W` | hide to tray |
| `Ctrl+Shift+K` | palette key passed through to the page (sites that bind `Ctrl+K`) |

**Ctrl+Tab mechanics:** intercepted in `AcceleratorKeyPressed` so sites can't steal it; overlay appears after a 120 ms hold (fast taps don't flash); commit is driven by a 30 ms `GetAsyncKeyState(VK_CONTROL)` poll, because WebView2 does not reliably deliver modifier key-up when focus is inside the page. Overlay is a `Canvas` over existing content — no resizing, no webview recreation.

**Palette:** `TextBox` + virtualized `ListBox`, subsequence fuzzy scoring, ordering = unread ▸ recency ▸ score. `>` commands only, `@` services only, bare text = both. Commands: warm/sleep, mute, DND 30 m / 1 h / until tomorrow, reload, add service, edit service, storage, filters, theme, copy URL, open in browser, settings, quit.

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

Zero permanent chrome means the design budget goes into the **overlays, tray menu, settings and title bar** — plus making colour schemes first-class.

### 12.1 Tokens (semantic, not raw colours)

| Token | Used for |
|---|---|
| `bg.window` | window backdrop |
| `bg.surface` | overlay panels, palette, list rows |
| `bg.elevated` | hovered/selected rows, dialogs |
| `bg.scrim` | Ctrl+Tab dim layer (alpha included) |
| `border.default`, `border.focus` | hairlines, keyboard focus ring |
| `text.primary`, `text.secondary`, `text.muted` | hierarchy |
| `accent.default`, `accent.fg` | selection, active row, badges |
| `state.danger`, `state.success`, `state.warning` | mute, DND, blocking, errors |
| `shadow.overlay` | overlay elevation |

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

Selection: Settings → Appearance, or palette `> theme`. "Follow system" flips light/dark on `WM_SETTINGCHANGE`.

### 12.3 Contrast + accessibility

Every scheme is validated at load: text/background pairs must clear **WCAG AA 4.5:1** (3:1 for large/UI). Failures are nudged in lightness by the minimum amount and reported in Settings, so custom schemes are flagged rather than silently unreadable. Focus rings are never colour-only; selection changes fill **and** weight.

### 12.4 Site appearance

Sites own their CSS. On every theme change we set `CoreWebView2Profile.PreferredColorScheme`, so services that honour `prefers-color-scheme` follow your light/dark choice. Optional per-service `injectCss` goes further; a few example snippets ship, clearly best-effort.

### 12.5 Typography + motion

Segoe UI Variable for UI; `Cascadia Mono` for the palette query line — the "hacker" flavour lives there, not in decoration. Overlay open/close 90 ms fade + 4 px rise. No other motion.

---

## 13. Data locations

| Path | Contents |
|---|---|
| `%APPDATA%\Chirpdesk\settings.json` | services, appearance, autostart, DND, blocking |
| `%APPDATA%\Chirpdesk\schemes\*.json` | user scheme overrides |
| `%APPDATA%\Chirpdesk\filters\` | cosmetic packs, imported lists, compiled matchers |
| `%LOCALAPPDATA%\Chirpdesk\profiles\<id>\` | per-service WebView2 data |
| `%LOCALAPPDATA%\Chirpdesk\icons\` | cached favicons |
| `%LOCALAPPDATA%\Chirpdesk\logs\` | rolling log, 2 MB cap |

Settings writes are atomic (temp + rename); a corrupt file falls back to defaults with a logged warning.

---

## 14. Phases and acceptance criteria

| Phase | Scope | Done when |
|---|---|---|
| **0 — Spine** | SDK install, project skeleton, hidden host, one service (WhatsApp), toast pipeline + AUMID, single instance | 3 h tray-hidden, inbound message toasts with correct text and icon; click focuses WhatsApp; never appears in Alt+Tab |
| **1 — Core UX** | multi-service hosting, per-service profiles, Ctrl+Tab overlay, palette, unread via title, Ctrl+1..9, Ctrl+W | 3 warm services switch instantly, half-typed text preserved; overlay keyboard-only usable; fuzzy palette finds services |
| **2 — Lifecycle + storage** | warm/cold at boot, sleep, autostart with 20 s delay, mute + DND, settings dialog, add-URL flow, per-service regex, **storage panel, trim policy, orphan scan** | Reboot with 3 warm + 1 cold: notifications within 30 s, no storm, no window; disk stays under 150 MB after a week; orphan scan finds a real leftover folder and restores it from the Recycle Bin |
| **3 — Blocking** | request matcher, curated tier-1 pack, cosmetic packs, per-service counters, allowlist, enable/disable UI | WhatsApp + Messenger + Telegram log in and send messages with blocking on; counters show blocked requests; disabling blocking for one service restores every request |
| **4 — Design** | token pipeline, scheme loader, 10 families, system light/dark, live switch, contrast validator, `PreferredColorScheme` sync | Switching scheme changes app + site preference instantly; shipped schemes pass AA; a dropped-in scheme file appears in the list |
| **5 — Ship** | portable publish + zip, shortcut creation, first-run overlay (teaching `Ctrl+Tab`/`Ctrl+K`), CI build workflow, docs | Fresh machine: unzip, run, add service, sign in, reboot, receive toast, run a cleanup — no dev tools |
| **6 — Optional** | ABP list import (tier 3), Telegram via TDLib, live preview in Ctrl+Tab, inline toast reply, auto-update | only if v1 proves itself in daily use |

Each phase ends with a build you can actually run.

---

## 15. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| Off-screen window throttled despite flags | R2 fails outright | Phase 0 gate; fallback 1×1 `WS_EX_NOACTIVATE` at screen edge |
| Blocking breaks a login or a WebSocket | Service unusable, worse than ads | Fail-open, permanent auth/WS allowlist, one-click per-service disable, counters for diagnosis |
| Cosmetic selector hides the wrong thing | Confusing UI | Conservative selectors, per-service toggle, never touches requests |
| Cache deletion logs the user out | Re-pairing WhatsApp | Cache and data are separate directories, separate buttons, separate confirmations |
| Orphan scan deletes something it shouldn't | Data loss | Fixed root allowlist, path re-verification, Recycle Bin for user data, in-use detection, nothing pre-checked |
| Meta changes the web client / sniffs the UA | Service breaks | Stock Edge UA like Meta's own Windows app; per-service UA override |
| Google sign-in refuses embedded browsers | Some services unusable | Detect + explain; open-in-browser escape hatch |
| Service-worker notifications differ from page notifications | Missed toasts | `NotificationReceived` covers both; per-service verification in phase 1; injected `Notification` shim as fallback |
| WebView2 memory per warm service | R10 partially fails | It's the site's cost; sleep, warm-count guidance, per-service RAM shown |
| Toast identity shows "(unknown app)" | Looks broken | AUMID + shortcut on first run, verified in phase 0 |
| `Ctrl+Tab` stolen by a site | R5 fails | `AcceleratorKeyPressed` precedence; `Ctrl+Shift+K` passthrough |
| Windows DND swallows toasts | User thinks it's broken | Read `SHQueryUserNotificationState`; tray hint when suppressed |

---

## 16. Repo layout (target)

```
Chirpdesk/
├─ README.md
├─ PLAN.md
├─ schemes/                  # shipped colour schemes (JSON)
├─ filters/
│  ├─ curated/               # tier-1 host lists per service
│  └─ cosmetic/              # per-service CSS packs
├─ src/Chirpdesk/
│  ├─ App.xaml(.cs)          # startup, single instance, autostart
│  ├─ Hosting/               # hidden window, webview manager, profiles
│  ├─ Services/              # definitions, store, add-URL flow
│  ├─ Notifications/         # toast pipeline, AUMID, permission policy
│  ├─ Filtering/             # matcher, list parser, counters
│  ├─ Storage/               # size scan, trim, orphan scan
│  ├─ Ui/                    # Ctrl+Tab overlay, palette, title bar, settings
│  └─ Theming/               # scheme loader, tokens, contrast check
├─ docs/                     # decisions, screenshots, service notes
└─ .github/workflows/build.yml
```

Dev environment: local only, .NET 8 SDK, no Visual Studio, no Windows SDK, no MSIX. Peak dev disk ~2 GB; app footprint ~5 MB.

---

## 17. Open decisions

1. **License** — MIT recommended, unset until you choose.
2. **Repo visibility** — currently private; flip to public whenever.
3. **Default warm set** — assumed WhatsApp + Messenger + Telegram warm, Facebook cold.
4. **Ctrl+Tab order** — assumed MRU; fixed order via `Ctrl+1..9`.
5. **Title bar** — assumed minimal custom bar showing `service · unread`.
6. **Blocking default** — assumed tier 1 + cosmetic on, list import off.
