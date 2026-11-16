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
- Add any chat site by URL — a built-in flow creates a normal service instance, no plugin required
- Isolated session per service instance, so multiple accounts coexist
- Built-in ad, tracker and junk-script blocking — request-level, fail-open, one-click off per service
- Storage panel: per-service cache breakdown, safe trimming, and a scan for chat-app leftovers from apps you no longer use
- Theme families (Dracula, Nord, Catppuccin, Tokyo Night, Gruvbox, Rosé Pine, Solarized, GitHub) each shipping dark **and** light variants
- **Plugin-shaped content**: shipped service definitions, themes, adblock packs and bundled tool panels share one manifest — add your own or a third-party plugin repo later. The sites you add are settings, not plugins.

## Target footprint

| | |
|---|---|
| App | ~5 MB |
| Runtimes | .NET 8 Desktop Runtime + WebView2 Evergreen (WebView2 is on Windows 11 and a one-time install on Windows 10; .NET 8 is installed by setup on any clean machine) |
| Disk incl. 3 warm profiles, cache-trimmed | under 200 MB after a week |
| RAM | ~50 MB shell + 150–400 MB per warm service |

## Stack

C# / .NET 8 / WPF + WebView2, published framework-dependent. No Visual Studio, no full Windows SDK, no MSIX.

## Docs

- [PLAN.md](PLAN.md) — requirements, architecture, phases, risks, accepted trade-offs
- [docs/plans/](docs/plans/) — task-by-task implementation plans (phase 0 ready)
- [docs/plugins.md](docs/plugins.md) — plugin manifest, kinds, trust tiers, repo format
- [docs/testing-strategy.md](docs/testing-strategy.md) — test pyramid, seams, manual E2E matrix
- [docs/measurements.md](docs/measurements.md) — recorded RAM, disk, latency and soak results
- [docs/adr/](docs/adr/) — architecture decision records
- [docs/engineering.md](docs/engineering.md) — conventions, commits, definition of done
- [AGENTS.md](AGENTS.md) — invariants for coding agents working in this repo

## License

MIT — see [LICENSE](LICENSE).
