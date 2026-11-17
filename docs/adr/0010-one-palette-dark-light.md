# ADR 0010 — One palette, dark and light only

**Status:** accepted (2026-11-17), supersedes the colour-family part of PLAN §12 for v1

## Context

The plan described theme families and colour schemes as first-class plugins. While finishing phase 0 the product owner narrowed it: the first complete app needs exactly one palette with a dark and a light end, and **dark has to reach the web page**, not just the window chrome. A chat client rendered in a bright white rectangle inside a dark frame is worse than no dark mode.

## Decision

One baseline palette. `theme` is `system | light | dark` and live-follows Windows while it is `system`. There is no colour-family picker, no theme plugin format, and no per-token overrides in v1.

Page darkening uses two levers in this order:

1. `CoreWebView2Profile.PreferredColorScheme` — free, correct, and the site reacts as it would to a dark OS.
2. A `filter: invert(1) hue-rotate(180deg)` on the root element, with images/video/canvas/svg/iframe inverted back, applied when the page's own background measures light. That is what makes sites which ignore `prefers-color-scheme` actually dark.

The decision is observable: the injected function returns the measured luminance and whether the class was applied, and the host logs it (`web theme: mode=dark result=dark:0.85`). A theme that silently did nothing would look identical to a site that chose white.

Page darkening is a **per-site** policy, because the site knows whether it already has a dark theme and the user may not:

- a service definition declares `pageDarkMode: off | auto`, and an instance may override it;
- `off` is the default, and it is what WhatsApp uses — WhatsApp follows the system scheme itself, so the filter would only fight it;
- `auto` means "darken only if the page still measures light after `prefers-color-scheme`", which is what a definition sets when its site has no dark theme of its own (ADR 0011 removed the URL add flow that used to set it);
- the app never guesses from the URL alone; the policy is explicit data.

## Consequences

- Theme work is minutes, not a token pipeline; phase 4 keeps tokens, contrast validation and high-contrast fallback but loses families until after v1.
- The filter is a blunt instrument: gradients shift hue, shadows invert oddly, and fixed-position layers can move because `filter` creates a containing block. It is the price of dark on sites that do not support it; sites that do support `prefers-color-scheme` never get the filter.
- The app cannot tell a deliberately light site from a broken one; luminance is the only signal, so a site choosing a bright background on purpose will still be inverted in dark mode.
- Nothing here is a plugin surface, so no theme plugin can change the palette until the family work returns.

## Amendment 2026-11-17 — page darkening is a bundled theme plugin

The mechanism above stays host code, but the **master switch is now a plugin**: the bundled
`page-dark` theme plugin. Activating it allows the host to darken pages; deactivating it removes
the filter from every warm runtime and hides the per-tab command. Bundled plugins default to
active, so an upgrade never silently loses darkening; only an explicit deactivation is stored
(`disabledPlugins` in settings).

This does not reintroduce colour-family plugins: `page-dark` ships no palette, tokens or
executable code. It is a named capability with a state, visible in the Plugins page, which is what
"everything is a plugin" means for a host-owned mechanism. A service definition still declares
whether its site wants darkening (`pageDarkMode: off | auto`), and an instance may override it
with `on` or `off`. Page darkening always follows the app theme: when Chirpdesk is light, no page
is inverted, whatever the per-service policy says.

