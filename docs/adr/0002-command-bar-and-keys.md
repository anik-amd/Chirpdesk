# ADR 0002 — One Command Bar, `Ctrl+Tab` cycles and `Ctrl+P` searches

**Status:** accepted (2026-11-16), revised 2026-11-17: the printable-key latch from cycle into search was removed; cycle is a pure switcher and `Ctrl+P` is the only search entry. The global `Ctrl+Alt+Space` summon is deferred; the tray icon is the entry when the window is hidden.

## Context

The app has no sidebar, tab strip or menu bar by requirement. Navigation, commands and theme switching must therefore happen in a single surface. Two separate overlays (a "switcher" plus a "palette") would duplicate list rendering, keyboard handling and theming for no user benefit.

## Decision

One overlay component with two entry modes:

- `Ctrl+Tab` (hold) → **cycle** mode: MRU list of warm services, commit on `Ctrl` release.
- `Ctrl+P` → **search** mode: sticky input, **open (warm) tabs only** in MRU order. Typing during cycle does **not** open search; cycle stays a pure switcher. Cold services and commands stay out of the palette until their own tasks.
- The bar takes focus when it opens, so it receives those keys; reserved accelerators (`Ctrl+W` closes a tab, `Ctrl+1..9`, `Esc` closes the palette or page) keep their meaning. `Esc` is never a global hide: sites use it to leave a conversation.

There is deliberately no "second `Tab`" latch: with `Ctrl` still held, another `Tab` produces the same `Ctrl+Tab` event as the first (and as auto-repeat), so it cannot be distinguished.

Because a tray-only app has no focus, `Ctrl+Tab` cannot be the only entry. A global `Ctrl+Alt+Space` summon was planned but is **deferred**; until it ships, the tray icon shows the window, and `Ctrl+P` works once the window has focus.

`Ctrl+K` and the `@` scope are rejected. `Ctrl+Shift+P` is passed to the page.

## Consequences

- One row template, one focus model, one place to theme; the bar is the whole UI.
- Discoverability depends on the first-run overlay and the tray tooltip; without a global hotkey the tray is the only entry while the window is hidden.
- The global hotkey is deferred. When it returns it can collide with another app; registration failure must be surfaced in Settings, never silent, with the tray as the fallback.
- `Ctrl+P` collides with the sites' print shortcut; accepted, with `Ctrl+Shift+P` as the documented escape hatch.
- Commit-on-release cannot rely on WebView2 delivering modifier key-up, so it is polled via `GetAsyncKeyState` at 30 ms. This is a deliberate impurity in a hot path, isolated in one class.
- WPF cannot paint over a WebView2 HWND, so opening the bar collapses the active webview and closing restores it. The control is not disposed; scroll and page input state survive. Before collapsing, the host captures the last frame and paints it behind the bar's scrim, so the palette reads as an overlay over the site rather than a blank pane. A separate overlay window was rejected as more focus/DPI failure modes for the same behaviour.
- Revisit if users ask for a persistent surface (then the answer is probably a hover-revealed strip, not a sidebar).
