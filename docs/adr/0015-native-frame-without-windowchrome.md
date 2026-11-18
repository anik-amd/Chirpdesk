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

The other missing piece was `WS_CAPTION`. A runtime style audit showed the WPF window at
`0x160F0000`: visible, clipping, system menu, thick frame and min/max boxes, but no caption. The
standard Win32 borderless recipe (`WS_POPUP | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX |
WS_CAPTION` plus `WM_NCCALCSIZE` returning 0) keeps the native window-management identity while
painting our own frame. Without the caption bits Windows treats the window as a non-caption
surface and skips the standard minimize/maximize/restore transition.

## Decision

1. Drop WPF `WindowChrome`. Keep `WindowStyle=None`, `ResizeMode=CanResize`, then add
   `WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX` to
   the runtime style and apply it with `SWP_FRAMECHANGED`.
2. Handle `WM_NCCALCSIZE` by returning 0 so the client fills the window; when maximized, pin
   the client rect to the monitor work area so the page reaches every screen edge without the
   frame overhang becoming a gap.
3. Supply `WM_NCHITTEST` edge hit-testing (`HTLEFT`/`HTRIGHT`/`HTTOP`/`HTBOTTOM` and corners)
   because the non-client frame is no longer painted; everything else stays `HTCLIENT` so the
   custom title-bar buttons and `DragMove` continue to work.
4. Set `DWMWA_BORDER_COLOR` to `DWMWA_COLOR_NONE` on Windows 11 so the accent border does not
   draw a blue line above the custom title bar.
5. Keep the window's extended style stable. Hide/show the taskbar button through `ITaskbarList`
   (`DeleteTab`/`AddTab`) instead of toggling `WS_EX_TOOLWINDOW`.
6. Set `ShowActivated=true` for a user-requested show; the startup show stays non-activating so a
   login launch cannot steal focus.
7. Do not emulate the transition with WPF opacity or `SetWindowPos`. Native `WindowState` changes
   are the only maximize/minimize path.
8. Keep `WM_GETMINMAXINFO` so the maximized frame still respects the monitor work area.

## Consequences

- Windows 11/10 draws the normal maximize/minimize transition and the normal shadow/rounded
  frame; no custom geometry loop exists to lag or reverse.
- The runtime style is checked by the self-test (`window-style`), so a future change cannot
  silently drop the caption bits again.
- The hidden-to-tray window stays a normal app window, so Alt+Tab and the taskbar behave when it
  is shown, and the taskbar button is removed through the shell rather than by changing styles.
- Startup is still hidden and off-screen; a user show is the only path that activates.
- If a future custom frame is needed, it must be tested with a screen recording for the snap
  regression, not only by checking that `WindowState` changed.
