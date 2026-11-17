# Phase 4 — Design

**Status:** automated gate complete (2026-11-17)
**ADR:** 0010 (one palette, dark and light only), 0013 (ink chrome, paper pages)
**Plan:** `PLAN.md` §12, phase 4 row in §15

## Goal

One palette with a light and a dark end, live switching, readable token pairs and a page-dark
policy that never fights a site that already has its own theme. Colour families and user theme
plugins are out of v1 (ADR 0010).

## What landed

1. **Tokens** — `Ui/TokensLight.xaml` and `Ui/TokensDark.xaml`, swapped by `UiTheme.Apply`
   through `DynamicResource`, so a theme change repaints chrome and pages without a restart.
2. **Shell** — ink chrome (title bar, palette) and paper pages share the token set; scrollbars,
   controls and the scrim are tokenised (`CdControlHover`, `CdScrim`).
3. **Page dark** — `PreferredColorScheme` first, then the measured-luminance invert filter, and
   only while the app itself is dark. The bundled `page-dark` theme plugin is the master switch;
   per-service `off | auto | on` is a policy, not a colour family.
4. **Contrast validation** — `Chirpdesk.Core.Theming.Contrast` implements WCAG 2 relative
   luminance; `--selftest` loads both token dictionaries and asserts AA (4.5:1) for text, muted
   text, accent, danger and palette pairs. The current palettes pass with margin.
5. **High contrast** — when Windows reports `SystemParameters.HighContrast`, `UiTheme` builds a
   token set from `SystemColors` instead of light/dark and rebuilds it when the contrast theme
   changes, so text and selection stay legible.
6. **Live preview** — changing the theme in Settings applies immediately; the palette and every
   warm page follow on the same tick.

## Tests

- `tests/Chirpdesk.Core.Tests/Theming/ContrastTests.cs` — 21:1 black/white, symmetry, AA for the
  live muted pair, hex parsing strictness.
- `--selftest` `contrast` — both dictionaries, the pairs that carry text.
- Manual: switch light/dark at 100 % and 150 %, then enable a Windows contrast theme and confirm
  the app repaints without a restart.

## Deferred

- Theme families, colour pickers and user theme plugins (ADR 0010).
- A visual theme editor. The token files are the editor.
