# Measurements

Numbers are only meaningful on the machine they were taken on. Record the build SHA every time.

| Date | Build | Phase | Notification mode | Boot→warm | RAM (shell) | RAM per warm service | Disk (app) | Disk (profiles) | Toast latency | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| 2026-11-16 | `c213fb5` | 0 | `native` (probe origin; synthetic notification) | — | 90 MB working set, 36 MB private | 404 MB total for one parked probe page: 113 MB app + 290 MB across 6 `msedgewebview2` processes | 26.6 MB published (framework-dependent, no symbols) | 21.0 MB for a fresh, not-yet-signed-in WhatsApp profile | — | Probe page for ticks; WhatsApp profile measured before login. Debug `artifacts/` tree is 30.7 MB. |
| 2026-11-16 | `973c877` | 0 | `native` (WhatsApp, real message from a second phone) | — | 26.7 MB private working set | 547 MB private working set total: 27 MB app + 520 MB across 8 `msedgewebview2` processes | 26.6 MB published | 158 MB after a real login (691 files, message store included) | under 1 s, observed not instrumented | Tray-hidden, one signed-in service. Summed working set is 1,056 MB but that double-counts shared Chromium pages; private is the honest figure. |

## Method

- Boot→warm: stopwatch from login to "all configured services report ready" (host log line).
- RAM: Task Manager → Details, sum of `Chirpdesk.exe` + its `msedgewebview2.exe` children, per service (one service warm at a time for the per-service column).
- Disk: size of the app directory, and of `%LOCALAPPDATA%\Chirpdesk\profiles`.
- Toast latency: site's own notification timestamp vs host log timestamp (instrumented in phase 0).
- Notification mode: the phase-0 probe result per service (`native`, `shim` or `title-only`) and whether the toast contained sender/text.

## Where the phase-0 numbers came from

- **Shell RAM — 90 MB working set / 36 MB private.** Measured 6 s after launch of a build with no service configured, taken as `WorkingSet64` and `PrivateMemorySize64` of the single `Chirpdesk.exe`. The gap between the two is file-backed framework pages (WPF, D3D, fonts), not per-user memory. The plan's target is "shell < 70 MB"; working set is over it, private bytes are not. Phase 1 decides whether that matters once a real service is measured.
- **One warm service — 404 MB summed, 547 MB private for real WhatsApp.** The hidden-warm probe holds a real `WebView2` parked at 1×1 with one HTML page: six `msedgewebview2.exe` processes, which is Chromium's normal process set and therefore the floor for *any* warm service. WhatsApp adds its own heap on top: eight processes and 520 MB private, 547 MB with the app. The 150–400 MB band in §2 does not survive contact with a real site; ADR 0005's trigger (4 warm services > 1.6 GB) is the number that decides what phase 1 does about it.
- **Disk — 26.6 MB published.** 24.3 MB of that is `Microsoft.Windows.SDK.NET.dll`, the generated WinRT projection that `Windows.UI.Notifications` needs. Everything Chirpdesk actually wrote is ~2.3 MB. Replacing the three toast interfaces with hand-written COM would remove the 24 MB, at the cost of maintaining that projection; recorded as a phase-1 decision rather than done mid-gate.
- **Profiles — 21.0 MB** for a fresh WhatsApp profile before sign-in (210 files, mostly the WebView2 runtime's own cache scaffolding). After a real login it is **158 MB** (691 files) because WhatsApp keeps its message store in `IndexedDB`; the 60 MB per-profile cache budget and 200 MB total in §8.2 are the numbers to watch, and the trim path does not exist until phase 2. The probe deletes its own profile and fails the gate if it cannot (`profile-released`), which is also the orphan-process proof: Chromium holds files in the profile, so a deletable profile means no browser process is still running.

## Live gate findings, 2026-11-16

Two defects only a real message could expose. Both were ours, not WhatsApp's, and both made the app look like it had no notification support at all:

1. **Timestamp kind.** `CoreWebView2Notification.Timestamp` is a `DateTime` whose `Kind` is `Unspecified` even though the value is UTC. The boot cutoff compared it to a `DateTimeOffset`, which read it as local time, moved it six hours into the past, and dropped every toast as stale. Live proof: `notification from before start dropped ... (2026-11-16T16:02:03 < 2026-11-16T16:01:59+00:00)` — a notification created four seconds *after* the start. Fixed by `NotificationTime.Normalize`; four tests.
2. **Warm-up gate deadlock.** The gate waited five seconds of an unchanged unread title before opening. A message changes that title at the same instant it notifies, so the gate re-armed itself and suppressed the event it existed to pass: `notification capability: native` and `notification suppressed during warm-up` two milliseconds apart, three minutes into the session. Fixed by measuring the window from readiness; six tests.

The probe harness passed throughout, because a synthetic notification does not change the title and does not arrive with a suspicious timestamp. That is exactly the gap §11.2 exists to cover.

**Consequence for phase 1:** one warm WhatsApp costs 547 MB private working set. Four of those exceed ADR 0005's 1.6 GB trigger, so the multi-profile-on-one-environment fallback needs a real decision before the warm-count guidance is published.

## Automated gate, 2026-11-16

`Chirpdesk.exe --selftest --soak-seconds=60` → exit 0, eleven checks:

```
[PASS] webview2-runtime - 157.0.4341.46
[PASS] settings - round-trip, atomic replace and corrupt-file quarantine
[PASS] app-identity - shortcut=True notifier=True aumid=Chirpdesk
[PASS] tray - icon=True tooltip=Chirpdesk
[PASS] window-hidden - toolwindow=True appwindow=False visible=True at=-32768,-32768
[PASS] single-instance - exited=True code=0 windows=False in 281 ms
[PASS] environment - created under the profiles root
[PASS] hidden-warm - timers=60/60 ready=complete
[PASS] notification-capability - event=True script=True permission=granted
[PASS] toast-shown - appeared in Action Center history
[PASS] profile-released - no process is holding the profile
```

`hidden-warm` is the R2 evidence: a page ticking once a second, parked in an off-screen 1×1 webview, ticked 60 times in 60 seconds. Chromium's default throttling would have cut that to roughly one tick per minute.

## Soak, 2026-11-16 → 2026-11-17

One process, `pid 10568`, started 22:33 and hidden until 01:04 (2.53 h). Messages at 01:00 and 01:01 both produced toasts, and nothing was replayed on the way back. Between 00:08 and 00:37 the unread title changed several times without a `NotificationReceived`; those were read-state syncs, not new messages, and the absence of any drop log confirms the bridge was never handed an event to reject.

The soak proves the socket survived 2.5 h of occlusion and that the anti-throttle flags hold past the first minute. It does not prove delivery if the machine sleeps — there is no sleep policy until phase 2 — and the machine was on AC with standby disabled.

## Boot row, 2026-11-17

Temporary `HKCU\…\Run` entry wrapping the exe in `timeout 20`. Boot at 01:49:09, login just before 01:52, process start 01:52:15 with exactly the hidden window (toolwindow set, appwindow clear, visible, rect `-32768,-32768`). The Run entry was removed afterwards; real autostart belongs to phase 2.

`pwsh scripts/gate.ps1` runs that plus the teardown checks, which need a second process to observe:

```
[PASS] build - dotnet build exited 0
[PASS] no-running-instance - Chirpdesk processes: 0 (quit from the tray first)
[PASS] selftest - eleven checks in %LOCALAPPDATA%\Chirpdesk\logs\selftest.log; exit 0
[PASS] warm-service-quit - exit 0
[PASS] no-orphan-webviews - webview processes using Chirpdesk profiles: 0 before, 0 after
[PASS] no-orphan-app - Chirpdesk processes left: 0
```

## Phase 1 gate, 2026-11-17

Base commit `dfc9a18` (two warm services: WhatsApp and Telegram).

```
[PASS] webview2-runtime - 157.0.4341.46
[PASS] settings - round-trip, atomic replace and corrupt-file quarantine
[PASS] app-identity - shortcut=True notifier=True aumid=Chirpdesk
[PASS] tray - icon=True tooltip=Chirpdesk
[PASS] window-hidden - toolwindow=True appwindow=False visible=True at=-32768,-32768
[PASS] single-instance - exited=True code=0 windows=False in 501 ms
[PASS] definitions - bundled=5 skipped=1 shadowed=whatsapp
[PASS] catalog - instance overrides, definition fallback, unknown id fails closed
[PASS] migration - v1 fixture migrated to v2, instance preserved
[PASS] bar-state - cycle wraps, Esc hides, Ctrl+W closes, Ctrl+2 selects, prefix ranking
[PASS] environment - created under the profiles root, cached per profile
[PASS] registry - mru=registry-b,registry-a cycle=registry-b bothAlive=True
[PASS] hidden-warm - timers=10/10 ready=complete
[PASS] notification-capability - event=True script=True permission=granted
[PASS] toast-shown - appeared in Action Center history
[PASS] profile-released - no process is holding the profile
```

`pwsh scripts/gate.ps1 -SkipBuild -SoakSeconds 10`: all five outer checks passed (no-running-instance, selftest, warm-service-quit, no-orphan-webviews, no-orphan-app).

**RAM and disk, two warm services.** App process 126.8 MB private working set; WebView2 1260.7 MB across 13 `msedgewebview2.exe` processes; total **1387.6 MB**. Profiles on disk **389.7 MB**. Measured after 15 s with both services warm, no sign-in performed in this session.

This is higher than the phase-0 single-service reading (547 MB) because it includes two chat clients plus their service-worker/renderer processes. It sits close to the per-warm-service cost the plan warns about; record it before promising three warm services.

**Manual evidence gathered during phase 1.** `Ctrl+Tab`/`Ctrl+Shift+Tab` cycles warm tabs; `Ctrl+P` lists open tabs and filters (`tel` ranks Telegram first); `Up`/`Down`/`Tab` move the highlight; `Esc` hides; `Ctrl+W` closes the highlighted tab and the next takes over; palette scroll survives open/close (CDP: `scrollTop=300` before, open and after); page focus returns on close; five bundled definitions load from `plugins/`; a user plugin in `%APPDATA%\Chirpdesk\plugins` loaded as a sixth definition before removal.

**Not yet covered by hand.** Two WhatsApp accounts signed in simultaneously; a cold service started from search; a real inbound message while the palette is open; three warm services RAM; sleep/standby delivery. These remain in `docs/testing-strategy.md` §5.
