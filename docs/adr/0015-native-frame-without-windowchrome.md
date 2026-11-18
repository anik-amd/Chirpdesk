# ADR 0015 - Keep the native Win32 frame so DWM transitions run

**Status:** accepted (2026-11-18)

## Context

Chirpdesk wants the stock Windows maximize/minimize transition, not a hand-rolled fade or a
geometry animation. The window is borderless with a custom title bar, so the first attempt kept
`WindowStyle=None` and added WPF's `WindowChrome` to retain resize borders. That path works for
layout, but it suppresses the DWM transition: screen captures showed a single snap frame instead
of the multi-frame Windows transition. Toggling `WS_EX_TOOLWINDOW`/`WS_EX_APPWINDOW` on every
show/hide had the same effect.

Limusic (`SimoHypers/limusic`) was used as the reference: its Tauri window is also borderless and
transparent, but it calls the stock `minimize()`, `toggleMaximize()` and `close()` and relies on
the compositor. It has no custom animation code.

## Decision

1. Drop WPF `WindowChrome`. Keep `WindowStyle=None`, `ResizeMode=CanResize` and the native
   `WS_THICKFRAME`; Win32 hit-testing still supplies `HTLEFT`/`HTRIGHT`/`HTTOP`/`HTBOTTOM` at
   the edges, and the custom title bar uses `DragMove` plus the existing double-click handler.
2. Keep the window's extended style stable. Hide/show the taskbar button through `ITaskbarList`
   (`DeleteTab`/`AddTab`) instead of toggling `WS_EX_TOOLWINDOW`.
3. Set `ShowActivated=true` for a user-requested show; the startup show stays non-activating so a
   login launch cannot steal focus.
4. Do not emulate the transition with WPF opacity or `SetWindowPos`. Native `WindowState` changes
   are the only maximize/minimize path.
5. Keep `WM_GETMINMAXINFO` so the maximized frame still respects the monitor work area.

## Consequences

- Windows 11/10 draws the normal maximize/minimize transition and the normal shadow/rounded
  frame; no custom geometry loop exists to lag or reverse.
- The hidden-to-tray window stays a normal app window, so Alt+Tab and the taskbar behave when it
  is shown, and the taskbar button is removed through the shell rather than by changing styles.
- Startup is still hidden and off-screen; a user show is the only path that activates.
- If a future custom frame is needed, it must be tested with a screen recording for the snap
  regression, not only by checking that `WindowState` changed.
