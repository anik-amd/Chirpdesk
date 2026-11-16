# AGENTS.md

Repo-specific guidance for coding agents. Read this before touching code.

## What this is

Chirpdesk: a Windows tray app that hosts web chat clients in WebView2, turns their notifications into Windows toasts, blocks ads, and cleans their disk usage. See `PLAN.md`.

## Commands

```powershell
dotnet build                                  # warning-free required
dotnet test --filter Category!=Integration    # unit tests, fast, no webview
dotnet run --project src/Chirpdesk            # manual run; app is invisible by default
dotnet run --project src/Chirpdesk -- --test-toast   # toast identity check
```

## Layout

- `src/Chirpdesk.Core` — pure logic. **Never** reference WPF, WebView2, Win32 or the registry from here.
- `src/Chirpdesk` — WPF shell, WebView2 hosting, P/Invoke, tray, toasts.
- `tests/Chirpdesk.Core.Tests` — xUnit; `FakeClock` and other doubles live here.
- `docs/plans/` — the task-by-task plan currently being executed.
- `docs/adr/` — why decisions were made. Read the relevant ADR before changing the design.

## Invariants — do not violate, even if a test passes

1. **Never block auth/WebSocket/update endpoints.** The allowlist in the filter engine wins over every tier. A change that lets a tier override it is a bug.
2. **Never delete outside the allowlisted roots.** No wildcards, no `..`, no absolute paths from settings. User data goes to the Recycle Bin.
3. **Never delete `Cookies`, `Local Storage`, `IndexedDB` or `Service Worker` as part of a "trim".** Those are the session; trimming is caches only.
4. **Never log message bodies, contact names, or full URLs with query strings.** Logs are capped at 2 MB and are user-shareable.
5. **Never show the window except because the user asked.** Tray, `Ctrl+Alt+Space`, Command Bar commit and toast click are user requests; boot, a crashed socket or a lifecycle bug is not. Hidden-warm is the product, and "make it visible so it works" is a bug report, not a fix.
6. **Never add a dependency** without an ADR. Budget is ≤ 2 NuGet packages.
7. **Never run the app's own update path in tests.** Signature verification is unit-tested with fixtures.
8. **Fail open.** If a filter, theme, service definition or setting is invalid, allow/ignore and log — never block the user out of a messenger.
9. **Never load plugin code in-process.** v1 plugins are data only; `integration` plugins run as separate processes, phase 7 at the earliest.
10. **Never install an unverified plugin or accept a changed repo key silently.** Signed index → hash from that index → schema validation. Key change blocks the repo until the user re-trusts it by fingerprint.
11. **Never let a plugin alter the global auth/WebSocket allowlist.** A filter plugin may add to it, never remove from it.
12. **Never let a plugin supply a filesystem path or an arbitrary action.** A `tool` plugin names a root from a fixed enum and calls host primitives; the host resolves paths and validates every argument. Adding a primitive is a code change with a test, not a schema field.

## Style

Nullable on, file-scoped namespaces, `async` all the way down, P/Invoke isolated in `Interop/`. Warnings are errors; do not silence them globally.

## Definition of done

Failing test first, then implementation, then green tests, then the commit message prescribed by the phase plan. New irreversible decision → new ADR. Phase work also needs the manual matrix rows in `docs/testing-strategy.md` §5.

## When unsure

Prefer the ADRs over intuition, and `PLAN.md` §19 over "improving" a trade-off that was already accepted. If a trade-off genuinely needs revisiting, say so and update the ADR — silently drifting from it is the failure mode this repo is trying to avoid.
