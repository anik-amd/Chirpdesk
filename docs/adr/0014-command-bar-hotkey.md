# ADR 0014 - Command Bar hotkeys are a foreground-filtered low-level hook

**Status:** accepted (2026-11-18)

## Context

The Command Bar is the app's one overlay. When a chat page owns focus, WebView2 swallows
browser shortcuts before WPF's `PreviewKeyDown` can see them. The earlier `HwndHost.MessageHook`
path did not fire reliably for the command keys on the live WebView2, so the shortcuts became
dead while the page was focused. A global hotkey would also fire when Chirpdesk is in the
background, which is wrong for a tray app.

## Decision

1. Install one `WH_KEYBOARD_LL` hook while the app runs.
2. Act only when the foreground window belongs to the Chirpdesk process. Every other keystroke
   and every keystroke while another app is foreground is passed straight through.
3. When the foreground window is Chirpdesk:
   - `Ctrl+K` opens search, or switches an open cycle bar to search.
   - `Ctrl+Shift+K` opens the sticky open-tab list.
   - `Ctrl+Tab` opens cycle mode, or moves the highlight when a bar is already open.
     `Ctrl+Shift+Tab` moves backward.
4. Swallow only those chords and open the Command Bar on the UI thread. Keep the ordinary
   window `PreviewKeyDown` path as a fallback when the hook cannot be installed. `Ctrl+P` is
   left to the page.
5. Unhook on exit. The hook is process-local state, not persisted.

## Consequences

- The hotkeys work whether focus is in a WPF page or in the WebView2 surface.
- The hook sees keystrokes for a short window, but it returns them untouched unless the app is
  foreground and the chord is one of the three above. No keystroke content is stored or logged.
- A future global-shortcut surface must extend this hook deliberately rather than adding a
  second mechanism that can steal the same keys.
