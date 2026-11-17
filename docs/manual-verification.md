# Manual verification to do later

Phase 1, phase 2 and the automated half of phase 3 are green. These need a human and a real
desktop session.

## First run

- On a fresh profile (no `%APPDATA%\Chirpdesk\settings.json`), launch Chirpdesk: the onboarding page appears once with the three surfaces.
- **Got it** and **Esc** both hide the window back to the tray; **Open plugins** opens the plugin page instead.
- The next launch goes straight to hidden-warm, with no overlay.

## Settings and plugins pages

- Open `Ctrl+P` → **Settings** and confirm it is a full page in the main window, readable in both light and dark at 100 % and 150 % scaling.
- Switch the theme in Settings between light, dark and system: the page, palette and warm pages repaint immediately, with no restart and no flash of the old palette.
- Turn on a Windows contrast theme: Chirpdesk should repaint with system colours; toggling it back should restore the normal palette without a restart.
- At 150 % scaling, check that plugin cards and service rows do not clip their descriptions or buttons.
- Open `Ctrl+P` → **Plugins & services**: installed plugins are a grid of cards, each with a real brand icon, its own usage description and On/Off state. Plugins are broader than services: filter packs, themes and tools share the same ecosystem. Expect service plugins, the bundled `page-dark` theme, the `filters-core` filter pack and the bundled cleaner.
- Toggle `page-dark` off: warm pages stop being inverted; toggle it on again and they invert without a restart.
- Toggle `filters-core` off: the next page load has no ad rules; toggle it on again and rules return.
- Turn on **Messenger** (or Facebook): it should add the account, open its page, and appear in `Ctrl+P` afterwards.
- Add a second account from the same plugin; both should appear in the palette with their own sessions.
- Turn the plugin off: runtimes sleep, sessions are not signed out. Turn it on again: the session should still be there.
- Tray → Settings must show the same page, not a popup.

## Command palette

- `Ctrl+P` opens the **Command palette** with a real text caret. Type normally; `Ctrl+A`, copy/cut/paste, Home/End, Delete and mouse selection work. `Up`/`Down` and `Tab` move the list, `Enter` activates, `Esc` goes back.
- The empty palette lists open tabs plus inactive service plugins (dimmed icons). Type `settings` or `plugin` to reveal commands.
- Type `plugin` to see plugin actions: inactive plugins activate on Enter; active plugins show `Deactivate …`.
- Service icons are real brand glyphs from Simple Icons (CC0); unread counts show at the right of each tab row.
- Selecting a cold/asleep service wakes it without re-pairing.

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
- Open `Ctrl+P` and run **Turn blocking off for this tab**. The choice is per account and survives a restart; Settings shows `blocking off` for that row.
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
