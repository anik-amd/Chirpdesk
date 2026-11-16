# Chirpdesk

**All your conversations.**

A tiny Windows tray app that keeps your web messengers (WhatsApp, Messenger, Telegram, Facebook, or any chat site you add) warm in the background, turns their notifications into real Windows toasts, and switches between them with `Ctrl+Tab`.

No bundled Chromium. No Electron. No sidebar. No subscription.

## Status

**Planning.** Nothing is implemented yet. The full technical plan lives in [PLAN.md](PLAN.md).

## What it will do

- Tray-only start at boot — no window, no taskbar entry, notifications only
- Per-service **warm** (loaded and toasting) or **cold** (loads when you ask)
- Native Windows toasts with your service's icon, dedupe, and no boot-time backlog storm
- `Ctrl+Tab` (hold) shows open services, VSCode-style MRU switching
- `Ctrl+K` command palette for switching, warming, muting, sleep, DND
- Add any chat site by URL — no catalogue to maintain
- Isolated session per service, so multiple accounts coexist
- Built-in ad, tracker and junk-script blocking — request-level, fail-open, one-click off per service
- Storage panel: per-service cache breakdown, safe trimming, and a scan for chat-app leftovers from apps you no longer use
- Theme system: Dracula, Nord, Catppuccin, Tokyo Night, Gruvbox, Rosé Pine, Solarized, GitHub, plus light/dark variants and your own scheme files

## Target footprint

| | |
|---|---|
| App | ~5 MB |
| Runtimes | 0 (uses the WebView2 and .NET 8 already on Windows) |
| Disk incl. 3 warm profiles, cache-trimmed | ~60–140 MB |
| RAM | ~50 MB shell + 150–400 MB per warm service |

## Stack

C# / .NET 8 / WPF + WebView2, published framework-dependent. No Visual Studio, no Windows SDK, no MSIX.

## License

TBD (MIT recommended).
