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
| `NotificationDedupe` | duplicate inside window, allowed after window, differing body, tag collision |
| `WarmUpGate` | suppressed before ready, suppressed while title changing, released after 5 s stable, no re-suppression later |
| `SettingsStore` | round-trip, defaults on missing, corrupt file quarantined, interrupted write keeps old file, id validation |
| `MruList` | promote to front, dedupe, wrap both directions, empty |
| `SchemeLoader` | token completeness, unknown token rejected, dark/light pairing, sparse overrides merge, invalid JSON quarantined |
| `ContrastCheck` | AA pass/fail boundaries at 4.5:1 and 3:1, auto-nudge terminates, never exceeds 20 iterations |
| `FilterMatcher` | `\|\|host^` exact/trailing-dot/subdomain, `@@` exception wins, allowlist beats every tier, fail-open on malformed rule, O(1) cache hit path |
| `PathGuard` | traversal (`..`), symlink/junction escape, path outside allowlisted roots, drive-relative paths, long paths |
| `TrimPlanner` | never selects `Cookies`/`Local Storage`/`IndexedDB`/`Service Worker`, respects budgets, oldest-first order, refuses while service warm |

Coverage target: **≥ 85 % line coverage in `Chirpdesk.Core`**, with 100 % of the "must never happen" cases above. No coverage target for `src/Chirpdesk` (UI + interop); it is kept deliberately thin.

## 4. Integration tests (Windows only, `dotnet test` with category `Integration`)

- Host boot: hidden window created, `WS_EX_TOOLWINDOW` present, not in taskbar.
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
| 5 | Cycle→type | `Ctrl+Tab`, `Tab`, type `tel`, `Enter` → Telegram focused |
| 6 | Cold service | selecting a cold service loads it, then it appears in cycle order |
| 7 | Blocking sanity | login, send, receive on WhatsApp/Messenger/Telegram with tier 1 + cosmetic on |
| 8 | Blocking fail-open | corrupt the filter file → all services still log in |
| 9 | Storage | trim reclaims > 0 bytes, session still valid afterwards |
| 10 | Orphan scan | finds a seeded fake leftover, Recycle Bin restore works |
| 11 | Change | settings dialogs apply without restart |

Record results in `docs/measurements.md` with date, build SHA and RAM/disk numbers.

## 6. CI

- `windows-latest`: `dotnet build -warnaserror`, `dotnet test --filter Category!=Integration`, scheme/filter JSON validation, artifact zip on tags.
- No webview, no toast, no network to chat services in CI — by design.
- A release tag additionally runs the installer build and signature step (phase 5).

## 7. Known gaps (accepted)

- WebView2 rendering, IME, DPI behaviour and tray quirks are untestable in CI — covered by the manual matrix.
- Third-party sites change without notice; no test can prevent that, only the fail-open policy and per-phase smoke matrix.
- Filter-list parsing is tested on fixtures, not on live EasyList (too large and changes daily); phase 3 captures a pinned fixture snapshot.
