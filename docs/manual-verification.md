# Manual verification to do later

Phase 1, phase 2 and the automated half of phase 3 are green. These need a human and a real
desktop session.

## First run

- On a fresh profile (no `%APPDATA%\Chirpdesk\settings.json`), launch Chirpdesk: the onboarding page appears once with the three surfaces.
- **Got it** and **Esc** both hide the window back to the tray; **Open plugins** opens the plugin page instead.
- The next launch goes straight to hidden-warm, with no overlay.

## Startup

- Start Chirpdesk from the Start menu, the shortcut or the exe: the window appears as soon as the first pinned service is warm, within a few seconds.
- Start it again while it is hidden: the resident instance shows the same window; no second process stays alive.
- Reboot with autostart on: no window, tray only; warm services appear after about 20 s.
- Settings → **Minimize to tray** on: the minimize button hides the desk to the tray; off sends it to the taskbar.
- Settings → **Close to tray** on: the close button hides the desk; off quits the app and releases every profile.
- `--selftest` no longer leaves its toast in Action Center; one left by an older build must be dismissed once.

## Window chrome and motion

- Maximize with the taskbar at the top: the whole title bar including minimize/maximize/close stays below the taskbar, and restore returns to the previous bounds.
- Maximized: the page reaches the work-area edges with no left/right/bottom gap and no Windows accent border above the title bar.
- Maximize/restore and minimize use the native Windows transition (not a WPF fade or a hand-moved rectangle). Hiding to the tray is immediate, with no fade. The command palette appears over the captured page frame without a fade-out of the page.
- Drag the title bar, resize from every edge and double-click the title bar to maximize; all three still work without `WindowChrome`.
- Windows 11 shows rounded outer corners; Windows 10 keeps the square frame.
- The title bar shows the app mark before the name.
- A 2 px accent bar runs along the bottom of the title bar while the active tab is loading.
- Resize to roughly 360 px wide and open `Ctrl+K`: the palette, query, rows and hint stay inside the window instead of clipping.
- Open `Ctrl+K` while Settings or Plugins is showing: the palette opens **over the page**, and closing it returns to the same page, not to the chat.

## Side rail

- The left rail lists every open chat. Activate two accounts of one plugin: each icon gets a small 1 and 2 badge.
- The **+** button opens the palette with the plugin list, so another service can be activated; the palette, plugins and settings icons in the rail open their surfaces directly.
- Settings → Side rail: **Always show**, **On left edge** and **Hide completely**. On hover, the rail animates its width from the left edge; the chat must not reflow or reload.
- Switch between rail modes while a chat is open and confirm the rail animation does not steal keyboard focus from the page.

## Icons

- Taskbar, Alt+Tab, the window title bar and the tray all show the paper-bubble mark; the tray glyph stays readable on a light and a dark taskbar.
- At 100 % and 150 % scaling, the 16 px frame still reads as a bubble with the amber pin, not a blurry square.
- Send an unread message: the tray shows a red badge with a white ring on the top-right of the bubble; it is visible on both a light and a dark taskbar, and the tooltip names the unread total.

## Settings and plugins pages

- Open `Ctrl+K` → **Settings** and confirm it is a full page in the main window, readable in both light and dark at 100 % and 150 % scaling.
- Switch the theme in Settings between light, dark and system: the page, palette and warm pages repaint immediately, with no restart and no flash of the old palette.
- In Appearance, pick a light palette (Latte) and a dark palette (Frappé, Macchiato or Mocha) separately. The light choice should apply while the app is light and the dark choice while it is dark.
- Turn on a Windows contrast theme: Chirpdesk should repaint with system colours; toggling it back should restore the normal palette without a restart.
- At 150 % scaling, check that plugin cards and service rows do not clip their descriptions or buttons.
- Open `Ctrl+K` → **Plugins & services**: installed plugins are a grid of cards, each with a real brand icon, its own usage description and On/Off state. Plugins are broader than services: filter packs, themes and tools share the same ecosystem. Expect service plugins, the bundled `page-dark` theme, `catppuccin`, the `filters-core` filter pack and the bundled cleaner.
- Toggle `page-dark` off: warm pages stop being inverted; toggle it on again and they invert without a restart.
- Toggle `filters-core` off: the next page load has no ad rules; toggle it on again and rules return.
- Turn on **Messenger** (or Facebook): it should add the account, open its page, and appear in `Ctrl+K` afterwards.
- Add a second account from the same plugin; both should appear in the palette with their own sessions.
- Turn the plugin off: runtimes sleep, sessions are not signed out. Turn it on again: the session should still be there.
- Tray → Settings must show the same page, not a popup.

## Command palette

- `Ctrl+K` opens the **Command palette** with a real text caret. Type normally; `Ctrl+A`, copy/cut/paste, Home/End, Delete and mouse selection work. `Up`/`Down` and `Tab` move the list, `Enter` activates, `Esc` goes back.
- The empty palette lists open tabs plus inactive service plugins (dimmed icons). Type `settings` or `plugin` to reveal commands.
- `Ctrl+Shift+K` opens the same open-tab list as a sticky palette, with the active tab highlighted; `Enter` switches, `Esc` closes.
- Hold `Ctrl` and press `Tab` (or `Ctrl+Shift+Tab`): the same tab list appears and the highlight cycles; releasing `Ctrl` switches to the highlighted tab.
- Type `plugin` to see plugin actions: inactive plugins activate on Enter; active plugins show `Deactivate …`.
- Service icons are real brand glyphs from Simple Icons (CC0); unread counts show at the right of each tab row.
- Selecting a cold/asleep service wakes it without re-pairing.

## Updates

- Settings -> Updates -> **Check now** against a public stable release: the status names the newer version and **Install and restart** appears.
- Install a signed newer release: the installer is downloaded, size and SHA-256 match, every WebView2 closes, the silent installer runs and the new build relaunches.
- Tamper `update.json` or the downloaded setup: the update is refused, the current build keeps running, and no installer starts.
- With **Check for updates automatically** off, no network request is made at boot.

## Autostart

- Tray → Settings → tick autostart, reboot.
- Expect no window at login; warm pinned services after about 20 s; notifications within 30 s.
- Untick it and confirm the HKCU Run value is gone.

## DND and mute

- Set DND 30 minutes, send a real message: no toast, unread still increments.
- Clear DND; the next message toasts.
- Mute one service: only that service stops toasting.

## Storage

- Trim caches once a profile is over budget; confirm reclaimed > 0 and the session still works.
- When nothing is over budget the page must say so instead of appearing to do nothing.
- Seed a folder under `%LOCALAPPDATA%\Chirpdesk\profiles\` that no settings entry uses, then Scan for leftovers: it should be listed and Recycle should move it to the Recycle Bin.
- Restore that folder from the Recycle Bin.

## Blocking

- With `filters-core` active, log in and send a message on WhatsApp, Messenger and Telegram. Nothing about login, WebSocket or updates may break.
- Open `Ctrl+K` and run **Turn blocking off for this tab**. The choice is per account and survives a restart; Settings shows `blocking off` for that row.
- With blocking on, Settings shows `blocking on · N blocked` once the page has requested a known ad host. Reload the page a few times if the count is still zero.
- Corrupt `plugins\filters-core\filters.json` beside `Chirpdesk.exe` (or copy the pack to `%APPDATA%\Chirpdesk\plugins\filters-core\` and corrupt that copy), restart: every service still logs in and the log reports the pack as skipped.

## Measurements still owed

- Three warm + one cold after reboot: RAM and disk, plus notifications within 30 s and no boot storm. An idle three-warm run already measured ~1.8 GB total (`docs/measurements.md`), so check whether boot and a cold service change the picture.
- Third account measurement (deferred on purpose).

## Known gaps

- Colour families and user theme plugins are out of v1 (ADR 0010); Settings, the palette and the plugins page use the one light/dark palette.
- ABP list import (tier 3) is off until phase 7; only the bundled tier-1 pack and its cosmetic selectors are active.
- Global `Ctrl+Alt+Space` hotkey is deferred.
- Removing an instance keeps its profile for the orphan scan.
