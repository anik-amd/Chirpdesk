# Measurements

Numbers are only meaningful on the machine they were taken on. Record the build SHA every time.

| Date | Build | Phase | Notification mode | Boot→warm | RAM (shell) | RAM per warm service | Disk (app) | Disk (profiles) | Toast latency | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| 2026-11-16 | `c213fb5` | 0 | `native` (probe origin; synthetic notification) | — | 90 MB working set, 36 MB private | 404 MB total for one parked probe page: 113 MB app + 290 MB across 6 `msedgewebview2` processes | 26.6 MB published (framework-dependent, no symbols) | 21.0 MB for a fresh, not-yet-signed-in WhatsApp profile | — | Probe page for ticks; WhatsApp profile measured before login. Debug `artifacts/` tree is 30.7 MB. |

## Method

- Boot→warm: stopwatch from login to "all configured services report ready" (host log line).
- RAM: Task Manager → Details, sum of `Chirpdesk.exe` + its `msedgewebview2.exe` children, per service (one service warm at a time for the per-service column).
- Disk: size of the app directory, and of `%LOCALAPPDATA%\Chirpdesk\profiles`.
- Toast latency: site's own notification timestamp vs host log timestamp (instrumented in phase 0).
- Notification mode: the phase-0 probe result per service (`native`, `shim` or `title-only`) and whether the toast contained sender/text.

## Where the phase-0 numbers came from

- **Shell RAM — 90 MB working set / 36 MB private.** Measured 6 s after launch of a build with no service configured, taken as `WorkingSet64` and `PrivateMemorySize64` of the single `Chirpdesk.exe`. The gap between the two is file-backed framework pages (WPF, D3D, fonts), not per-user memory. The plan's target is "shell < 70 MB"; working set is over it, private bytes are not. Phase 1 decides whether that matters once a real service is measured.
- **One warm service — 404 MB total.** The hidden-warm probe holds a real `WebView2` parked at 1×1 with one HTML page. Six `msedgewebview2.exe` processes (browser, GPU, network, renderer, utility, crashpad) is Chromium's normal process set, so this is the floor for *any* warm service, before WhatsApp's own heap. Budget is 150–400 MB, so a real service will sit at or above the top of it; ADR 0005's trigger (4 warm services > 1.6 GB) is the number that matters.
- **Disk — 26.6 MB published.** 24.3 MB of that is `Microsoft.Windows.SDK.NET.dll`, the generated WinRT projection that `Windows.UI.Notifications` needs. Everything Chirpdesk actually wrote is ~2.3 MB. Replacing the three toast interfaces with hand-written COM would remove the 24 MB, at the cost of maintaining that projection; recorded as a phase-1 decision rather than done mid-gate.
- **Profiles — 21.0 MB** for a fresh WhatsApp profile before sign-in (210 files, mostly the WebView2 runtime's own cache scaffolding). After a login this grows with the message database; the 60 MB per-profile cache budget and 200 MB total in §8.2 are the numbers to watch, and the trim path does not exist until phase 2. The probe deletes its own profile and fails the gate if it cannot (`profile-released`), which is also the orphan-process proof: Chromium holds files in the profile, so a deletable profile means no browser process is still running.

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

`pwsh scripts/gate.ps1` runs that plus the teardown checks, which need a second process to observe:

```
[PASS] build - dotnet build exited 0
[PASS] selftest - eleven checks in %LOCALAPPDATA%\Chirpdesk\logs\selftest.log; exit 0
[PASS] warm-service-quit - exit 0
[PASS] no-orphan-webviews - webview processes using Chirpdesk profiles: 0 before, 0 after
[PASS] no-orphan-app - Chirpdesk processes left: 0
```
