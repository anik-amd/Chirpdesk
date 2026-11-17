# Testing Strategy

Goal: confidence where the code makes decisions, honest manual gates where it touches WebView2, Windows toasts and real chat services.

## 1. Shape

```
        Manual soak / E2E        (phase gates, ~8 checks per phase)
      ────────────────────────
     Integration (WebView2)      (small, host boot + profile + toast identity)
   ────────────────────────────
  Unit tests in Chirpdesk.Core   (the bulk; no WebView2, no WPF, no OS)
```

The split is enforced by project structure, not discipline: `Chirpdesk.Core` has no WPF/WebView2 reference, so anything untestable cannot live there.

## 2. Seams

| Seam | Exists so that |
|---|---|
| `IClock` | dedupe, warm-up gate, sleep timers, trim schedule are all testable with `FakeClock` |
| `ISettingsStore` | UI never touches disk directly; corrupt-file behaviour is unit-testable |
| `IToastSink` | notification policy is tested without Windows notifications |
| `IProcessProbe` | orphan scan's "in use" refusal is testable without launching WhatsApp |
| `IFileSystem` (thin, only for delete/move) | path-safety rules are tested without real deletion |

## 3. What gets unit tests (Core)

| Area | Cases that matter |
|---|---|
| `UnreadParser` | no title, no match, first-match-wins, non-numeric capture, `99+` forms |
| `NotificationDedupe` | duplicate inside window, allowed after window, differing body, same body from a different sender not collapsed, tag collision |
| `WarmUpGate` | suppressed before ready, suppressed while title changing, released after 5 s stable, no re-suppression later |
| `SettingsStore` | round-trip, defaults on missing, corrupt file quarantined, interrupted write keeps old file, id validation |
| `MruList` | promote to front, dedupe, wrap both directions, empty |
| `ThemeTokenMap` | token completeness, unknown token rejected, variant mode pairing, live swap leaves no stale resource key |
| `ContrastCheck` | AA pass/fail boundaries at 4.5:1 and 3:1, auto-nudge terminates, never exceeds 20 iterations |
| `FilterMatcher` | `\|\|host^` exact/trailing-dot/subdomain, `@@` exception wins, allowlist beats every tier, fail-open on malformed rule, O(1) cache hit path |
| `PathGuard` | traversal (`..`), symlink/junction escape, path outside allowlisted roots, drive-relative paths, long paths |
| `TrimPlanner` | never selects `Cookies`/`Local Storage`/`IndexedDB`/`Service Worker`, respects budgets, oldest-first order, refuses while the profile is warm, only trims disposed profiles |
| `OrphanPolicy` | never reports a shared profile, never escapes an enumerated root, refuses a directory owned by a running app, user data goes to the Recycle Bin while caches are hard-deleted |
| `NotificationPolicy` | per-service mode (`native`/`shim`/`title-only`) selects the right path, origin not belonging to the service is dropped, boot cutoff drops pre-start notifications |
| `ServiceInstance` | instance id validated and unique, a legacy `definitionId: null` row with an inline URL still resolves, disabling a definition keeps the instance and profile, `shareProfileWith` refuses a missing owner, two instances sharing a profile resolve to one environment, deleting an owner requires unsharing first, two instances of one definition get distinct profiles |
| `PluginManifest` | missing required field, bad id pattern, unknown `apiVersion`, unimplemented permission rejected, asset path traversal rejected |
| `ThemeVariants` | duplicate variant id rejected, `defaults` must reference an existing variant of matching mode, missing token in one variant disables the family, family with no light variant keeps dark under follow-system, all-themes-missing falls back to baseline |
| `PluginLoader` | manifest id/kind/version/apiVersion/permissions validated; user plugin shadows bundled; duplicate ids, missing payload, id mismatch, bad JSON and traversing assets skip only that plugin |
| `BarState` | cycle opens on the next/previous MRU row; highlight wraps; Ctrl release commits; Esc is a hide action; empty list is a no-op; search results keep the selected id when it survives filtering |
| `SearchIndex` | prefix beats substring and exact beats prefix; warm beats cold beats command at equal score; aliases and legacy host match; empty query lists warm rows in input order; no match returns nothing |
| `ToolPanel` | unknown primitive disables only that action, `revealFolder` rejects a path argument, `deleteOrphan` rejects an id from another session, missing permission greys the action, panel still renders when all actions fail |
| `UpdateManifest` | ECDSA P-256 signature verified against the pinned SPKI key, tampered payload hash rejected, wrong key rejected, malformed manifest rejected, version not newer than running build rejected, no network path taken on failure |
| `PluginRegistry` | user plugin overrides bundled with same id, duplicate ids across repos require a choice, rescan picks up a new folder, broken plugin disabled not fatal, a user-installed `tool` is refused |
| `RepoIndex` | bad signature rejected, hash mismatch rejected, size mismatch rejected, `minAppVersion` gating, key change blocks until re-trust, index cache TTL |

Coverage target: **≥ 85 % line coverage in `Chirpdesk.Core`**, with 100 % of the "must never happen" cases above. No coverage target for `src/Chirpdesk` (UI + interop); it is kept deliberately thin.

## 4. Integration tests (Windows only, `dotnet test` with category `Integration`)

- Host boot: hidden window created, `WS_EX_TOOLWINDOW` present, not in taskbar.
- Show/hide: showing a service moves the same HWND on-screen and restores the taskbar button; hiding re-applies `WS_EX_TOOLWINDOW` without recreating the webview.
- Profile creation: two services get distinct `userDataFolder` roots; cookies written by one are absent in the other.
- AUMID: shortcut exists, `AppUserModelID` string matches `Chirpdesk`, toast notifier constructs without throwing.
- Filter hookup: a synthetic in-page `fetch("https://doubleclick.net/x")` is cancelled, while `fetch` to the service origin succeeds (fail-open check).
- Settings migration: v0 file (no `blocking` field) loads and is upgraded in place.

These run on the dev machine only; CI runs unit tests and compiles, never hosts a webview.

## 5. Manual E2E matrix (per phase gate)

| # | Scenario | Pass condition |
|---|---|---|
| 1 | Cold boot to warm | notifications work within 40 s of login, no window flash |
| 2 | No boot storm | ≤ 2 toasts at login for real backlog, none after the gate opens |
| 3 | Long idle | 8 h tray-hidden, test message still toasts |
| 4 | Switch | `Ctrl+Tab` → 3 services → commit; half-typed text and scroll position survive |
| 5 | Search | `Ctrl+P`, type `tel`, `Enter` → Telegram focused; `Ctrl+Tab` never enters search |
| 6 | Cold service | selecting a cold service loads it, then it appears in cycle order |
| 7 | Blocking sanity | login, send, receive on WhatsApp/Messenger/Telegram with tier 1 + cosmetic on |
| 8 | Blocking fail-open | corrupt the filter file → all services still log in |
| 9 | Storage | trim reclaims > 0 bytes, session still valid afterwards |
| 10 | Orphan scan | finds a seeded fake leftover, Recycle Bin restore works |
| 11 | Change | settings dialogs apply without restart |
| 12 | Theme variants | `> theme catppuccin` follows dark/light as Windows flips; every variant readable; deleting all theme plugins renders the baseline and says so |
| 13 | Definition plugin | install a `service` plugin → its site appears, creating an instance gives it its own profile and it stays cold; a second instance of the same definition keeps a separate session; no URL-only add path exists |
| 14 | Notification capability | each bundled service reports its mode (`native`/`shim`/`title-only`); a background message produces a toast in that mode and the mode is shown in Settings |
| 15 | Tray entry | tray click shows the window when hidden, then `Ctrl+P` opens the palette; no global hotkey in v1 |
| 16 | Tool panel | cleaner panel renders, trim reclaims bytes, a disabled/unknown action greys out with a reason and the panel still opens |
| 17 | Multiple accounts | one definition, two instances: both warm with separate unread counts and toasts, signing one out leaves the other signed in; Messenger and Facebook are separate definitions and open their own sites |
| 18 | Close tab | `Ctrl+W` closes the highlighted tab in the palette or the active tab when closed; the next tab takes over, the instance stays cold in settings, and the last tab hides the window |

Record results in `docs/measurements.md` with date, build SHA and RAM/disk numbers.

## 6. CI

- `windows-latest`: `dotnet build -warnaserror`, `dotnet test --filter Category!=Integration`, plugin/filter JSON-schema validation, artifact zip on tags.
- No webview, no toast, no network to chat services in CI — by design.
- A release tag additionally runs the installer build and signature step (phase 6).

## 7. Known gaps (accepted)

- WebView2 rendering, IME, DPI behaviour and tray quirks are untestable in CI — covered by the manual matrix.
- Third-party sites change without notice; no test can prevent that, only the fail-open policy and per-phase smoke matrix.
- Filter-list parsing is tested on fixtures, not on live EasyList (too large and changes daily); phase 3 captures a pinned fixture snapshot.
