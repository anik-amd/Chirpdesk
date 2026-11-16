# ADR 0001 — Host on WPF + WebView2, not Electron or Tauri

**Status:** accepted (2026-11-16)

## Context

Chirpdesk must host the real web clients of WhatsApp, Messenger, Telegram and arbitrary chat sites. That rules out native-API clients: those services publish no usable API. Requirements add hard constraints: tray-only background operation, page-level notification interception, request filtering for ad blocking, per-service isolated sessions, and a footprint far below Electron's.

Candidates: WPF + WebView2, Tauri v2 + SvelteKit (Limusic's stack), WinUI 3, Electron, Thunderbird add-on.

## Decision

WPF (`.NET 8`, `net8.0-windows10.0.19041.0`) hosting `WebView2`, published framework-dependent.

## Consequences

- Windows 11 ships the WebView2 Runtime but **not** the .NET 8 Desktop Runtime; Windows 10 1809+ needs the WebView2 Evergreen Runtime installed once (10 is past end of support, so the installer handles it and the portable zip documents it). The app is ~5 MB and adds no Chromium copy; setup installs the framework runtime, so the app stays framework-dependent rather than self-contained.
- `CoreWebView2.NotificationReceived`, `WebResourceRequested`, `PermissionRequested`, `CoreWebView2Profile.PreferredColorScheme` and per-profile `userDataFolder` are all first-class, which removes COM-interop work from the two riskiest features.
- Tauri would use the same engine but expose neither notification interception nor request filtering; both would need raw `webview2-com` interop, and multi-webview-in-one-window is behind an unstable flag. It also costs 3–5 GB of dev disk against ~2 GB here.
- We pay for this with WPF's chrome/DPI rough edges and with webview RAM being the site's cost (150–400 MB per warm service). Accepted; see `PLAN.md` §19.
- Revisit if a browser-embedding API with better lifecycle control (or official service APIs) appears.
