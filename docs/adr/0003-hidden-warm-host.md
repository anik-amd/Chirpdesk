# ADR 0003 — Hidden warm host window with throttling disabled

**Status:** accepted, with a phase-0 gate

## Context

R1/R2 require no visible window at boot, yet notifications must arrive with nothing opened. A web notification only exists while the site's page is alive, so the page must be loaded and connected even when the app is invisible.

## Decision

One host window, created at startup, positioned at `(-32000, -32000)` at 2×2 px, `ShowInTaskbar=false`, `ShowActivated=false`, plus `WS_EX_TOOLWINDOW` so it never appears in Alt+Tab. Warm services are `WebView2` instances in that window, parked at 1×1 when not active.

Chromium backgrounds occluded content, which throttles JS timers and eventually kills WebSocket heartbeats, so the environment is created with:

```
--disable-background-timer-throttling
--disable-renderer-backgrounding
--disable-backgrounding-occluded-windows
```

and a 30 s tick re-asserts `Controller.IsVisible = true` and `TrySuspend(false)`.

## Consequences

- Toasts fire regardless of occlusion (the event is raised on the host), but *connection* liveness depends on the flags — they are correctness, not tuning.
- The whole app's liveness is anchored to one HWND; a wedged renderer affects that service only, but the window must stay alive for the process to be useful.
- Fallbacks, in order: 1×1 `WS_EX_NOACTIVATE` window at screen edge; then a minimised taskbar window (still zero in-app chrome).
- **Phase 0 gate decides this.** 3 hours tray-hidden with a real login, then an inbound message must toast. Failure is recorded here with the observed mode and the fallback chosen.
