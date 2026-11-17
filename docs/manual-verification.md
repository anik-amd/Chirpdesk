# Manual verification to do later

Phase 1 and phase 2 automated gates are green. These need a human and a real desktop session.

## Settings and plugins pages

- Open `Ctrl+P` → **Settings** and confirm it is a full page in the main window, readable in both light and dark at 100 % and 150 % scaling.
- Open `Ctrl+P` → **Plugins & services**: installed plugins are a grid of cards, each with a real brand icon, its own usage description and On/Off state. Plugins are broader than services: filter packs, themes and tools share the same ecosystem; this page currently lists service plugins plus the bundled cleaner.
- Turn on **Messenger** (or Facebook): it should add the account, open its page, and appear in `Ctrl+P` afterwards.
- Add a second account from the same plugin; both should appear in the palette with their own sessions.
- Turn the plugin off: runtimes sleep, sessions are not signed out. Turn it on again: the session should still be there.
- Tray → Settings must show the same page, not a popup.

## Command palette

- `Ctrl+P` opens the **Command palette**; its empty state lists your open tabs only. Type `settings` or `plugin` to reveal commands and installed-but-inactive plugins.
- Type `plugin` to see plugin actions: inactive plugins show as `inactive` and activate on Enter; active plugins show `Deactivate …`. Type `inactive` for only the inactive ones.
- `Ctrl+Tab` still cycles only warm tabs; `Esc` closes the palette/page and is passed to the site when neither is open; `Ctrl+W` closes.
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

## Measurements still owed

- Three warm + one cold after reboot: RAM and disk, plus notifications within 30 s and no boot storm.
- Third account measurement (deferred on purpose).

## Known gaps

- Colour families/themed Settings page wait for phase 4; Settings is deliberately a light document page for now.
- Global `Ctrl+Alt+Space` hotkey is deferred.
- Removing an instance keeps its profile for the orphan scan.
