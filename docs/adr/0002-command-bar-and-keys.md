# ADR 0002 — One Command Bar, `Ctrl+Tab` cycles and `Ctrl+P` searches

**Status:** accepted (2026-11-16)

## Context

The app has no sidebar, tab strip or menu bar by requirement. Navigation, commands and theme switching must therefore happen in a single surface. Two separate overlays (a "switcher" plus a "palette") would duplicate list rendering, keyboard handling and theming for no user benefit.

## Decision

One overlay component with two entry modes:

- `Ctrl+Tab` (hold) → **cycle** mode: MRU list of warm services, commit on `Ctrl` release.
- `Ctrl+P` → **search** mode: sticky input, warm services → cold services → commands.
- Typing during cycle mode converts it into search mode with the highlight retained.

`Ctrl+K` and the `@` scope are rejected. `Ctrl+Shift+P` is passed to the page.

## Consequences

- One row template, one focus model, one place to theme; the bar is the whole UI.
- Discoverability depends on the first-run overlay and the tray tooltip.
- `Ctrl+P` collides with the sites' print shortcut; accepted, with `Ctrl+Shift+P` as the documented escape hatch.
- Commit-on-release cannot rely on WebView2 delivering modifier key-up, so it is polled via `GetAsyncKeyState` at 30 ms. This is a deliberate impurity in a hot path, isolated in one class.
- Revisit if users ask for a persistent surface (then the answer is probably a hover-revealed strip, not a sidebar).
