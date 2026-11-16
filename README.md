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
- `Ctrl+Tab` (hold) cycles open services, VSCode-style MRU switching
- `Ctrl+P` opens the same command bar in search mode — switch services, run commands, change theme
- Add any chat site by URL — an "any site" template plugin creates a normal service
- Isolated session per service, so multiple accounts coexist
- Built-in ad, tracker and junk-script blocking — request-level, fail-open, one-click off per service
- Storage panel: per-service cache breakdown, safe trimming, and a scan for chat-app leftovers from apps you no longer use
- Theme families (Dracula, Nord, Catppuccin, Tokyo Night, Gruvbox, Rosé Pine, Solarized, GitHub) each shipping dark **and** light variants
- **Everything is a plugin**: services, the any-site template, themes, adblock packs and the disk cleaner share one manifest — add your own or a third-party plugin repo later

## Target footprint

| | |
|---|---|
| App | ~5 MB |
| Runtimes | 0 (uses the WebView2 and .NET 8 already on Windows) |
| Disk incl. 3 warm profiles, cache-trimmed | ~60–140 MB |
| RAM | ~50 MB shell + 150–400 MB per warm service |

## Stack

C# / .NET 8 / WPF + WebView2, published framework-dependent. No Visual Studio, no Windows SDK, no MSIX.

## Docs

- [PLAN.md](PLAN.md) — requirements, architecture, phases, risks, accepted trade-offs
- [docs/plans/](docs/plans/) — task-by-task implementation plans (phase 0 ready)
- [docs/plugins.md](docs/plugins.md) — plugin manifest, kinds, trust tiers, repo format
- [docs/testing-strategy.md](docs/testing-strategy.md) — test pyramid, seams, manual E2E matrix
- [docs/adr/](docs/adr/) — architecture decision records
- [docs/engineering.md](docs/engineering.md) — conventions, commits, definition of done
- [AGENTS.md](AGENTS.md) — invariants for coding agents working in this repo

## License

MIT — see [LICENSE](LICENSE).
