# ADR 0003 — One hidden-warm host window, shown only on request

**Status:** accepted, with a phase-0 gate

## Context

R1/R2 require no visible window at boot, yet notifications must arrive with nothing opened. A web notification only exists while the site's page is alive, so the page must be loaded and connected even when the app is invisible. The app also has to become visible when the user actually wants to read or reply — and a second window would mean reparenting webviews and restarting renderers.

## Decision

One host window, created at startup with `ShowInTaskbar=true` and positioned at `(-32000, -32000)` at 2×2 px, `ShowActivated=false`. `WS_EX_TOOLWINDOW` is applied in `SourceInitialized`, which keeps it out of the taskbar and Alt+Tab while hidden. Warm services are `WebView2` instances in that window, parked at 1×1 when not active.

When the user opens a service (tray, `Ctrl+Alt+Space`, Command Bar commit, toast click), the same HWND moves on-screen, the active webview fills it, and `SetWindowLongPtr` clears `WS_EX_TOOLWINDOW` and sets `WS_EX_APPWINDOW`. Hiding (`Ctrl+W`, close button) returns it off-screen and re-applies `WS_EX_TOOLWINDOW`. Visibility is always user-requested; it is never used as a lifecycle workaround.

`ShowInTaskbar` must not be toggled after creation: WPF recreates the HWND when it changes, destroying every hosted webview. Extended window styles are the supported path, and a window created with `ShowInTaskbar=false` has a hidden owner that still suppresses the taskbar button even after `WS_EX_TOOLWINDOW` is removed — hence creation with `true`.

Chromium backgrounds occluded content, which throttles JS timers and eventually kills WebSocket heartbeats, so the environment is created with:

```
--disable-background-timer-throttling
--disable-renderer-backgrounding
--disable-backgrounding-occluded-windows
```

and a 30 s tick re-asserts `Controller.IsVisible = true` and `TrySuspendAsync(false)`.

**Notification scope.** `CoreWebView2.NotificationReceived` covers non-persistent notifications (`new Notification()`). It does **not** expose persistent `ServiceWorkerRegistration.showNotification()` calls; WebView2 renders those itself. Phase 0 therefore probes each service and records `native`, `shim` (page-context override posts the content to the host) or `title-only` (generic toast from the unread count). The mode is visible in Settings. This limitation is part of the gate, not an implementation detail to discover later.

## Consequences

- Toasts fire regardless of occlusion (the event is raised on the host), but *connection* liveness depends on the flags — they are correctness, not tuning.
- The whole app's liveness is anchored to one HWND; a wedged renderer affects that service only, but the window must stay alive for the process to be useful.
- Show/hide must toggle `WS_EX_TOOLWINDOW`/`WS_EX_APPWINDOW` and reposition the same HWND; toggling `ShowInTaskbar` is forbidden because WPF recreates the window and every webview with it.
- A service that only emits background service-worker notifications degrades to a generic toast. If content is required, the gate fails for that service and the fallback is recorded here.
- Fallbacks, in order: 1×1 `WS_EX_NOACTIVATE` window at screen edge; then a minimised taskbar window (still zero in-app chrome).
- **Phase 0 gate decides this.** Log in once with the window shown, hide it, then 3 hours tray-hidden with a real login, then an inbound message must toast. Failure is recorded here with the observed mode and the fallback chosen.

## Amendment 2026-11-17 - a user launch is a request

Starting Chirpdesk from the shortcut, the Start menu or a second launch while it is resident now
shows the window: the launch is the user asking for it. Only `--autostart` (the HKCU Run path at
login) and an explicit `--hidden` stay in the tray. The host still starts off-screen and warms
hidden before the first frame is shown, so the rule "never show the window except because the user
asked" is unchanged - boot is not a request.
