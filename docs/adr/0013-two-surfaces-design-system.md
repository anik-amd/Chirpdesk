# ADR 0013 — Two surfaces: ink chrome, paper pages, live theme tokens

**Status:** accepted (2026-11-17)

## Context

The first settings surface was a hardcoded light popup with Windows-default controls; it read as unfinished next to the dark tray shell. The command palette had its own hardcoded colours. Each page repeated palette values, so a theme change could not reach them, and switching to dark made pages unreadable instead of deliberate.

The product is a small desk app: a hidden tray shell and short, infrequent configuration pages. The interface should feel like a quiet tool, not a website or a dashboard kit.

## Decision

1. **Two surfaces, one system.** The chrome (title bar, palette, tray-adjacent states) is ink: `#10171A` with warm off-white text and a brass selection mark. Configuration pages are paper: warm stone canvas, raised paper cards, ink text. The contrast between "live surface" and "document surface" is intentional.
2. **Both palettes are first-class.** `Ui/TokensLight.xaml` and `Ui/TokensDark.xaml` expose the same token keys; `UiTheme.Apply(dark)` swaps the merged dictionary. Pages and controls use `DynamicResource`, so a Windows theme change reaches open pages without a restart.
3. **No per-page colour constants.** Pages use shared styles (`Heading`, `MutedText`, `Card`, `PillButton`, `GhostButton`) and token keys (`CdCanvas`, `CdSurface`, `CdText`, `CdRule`, `CdAccent`, `CdDanger`, `CdPalette…`). Numbered decoration, all-caps eyebrow labels and card grids as a default are deliberately absent.
4. **Typography is functional.** Segoe UI Variable Display for titles, Segoe UI Variable Text for body, Cascadia Mono only for queries and counts. Type does the hierarchy work; surfaces stay quiet.
5. **Real brand marks, no generated art.** Service icons are Simple Icons (CC0) vector paths drawn in the definition's `accent` colour; unknown definitions fall back to a monogram badge. Icons are data, not bitmap assets, so plugins can supply their identity without shipping images.
6. **Settings and Plugins are pages in the main window**, not popups. The palette is a dark floating card over a captured page frame; its empty state is "Command palette", and its default rows are open tabs only. Typing searches services, installed-but-inactive plugins and commands.

## Consequences

- A future theme family needs only new token dictionaries and plugin accents; page code must not grow colour constants.
- The palette stays dark in both themes because it sits over a dimmed page capture; that is a deliberate surface, not a missed theme.
- Brand icons add a CC0 dependency in source form only (paths embedded in `ServiceIcons.cs`), not a NuGet package.
- Settings is an ordinary page; it inherits the main window's focus and Esc/back semantics.
