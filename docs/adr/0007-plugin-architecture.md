# ADR 0007 — Everything extensible is a data plugin; code only out-of-process

**Status:** accepted (2026-11-16)

## Context

Chirpdesk needs four kinds of extension: services (WhatsApp, Messenger, any site), themes (Dracula, Catppuccin …), filter packs, and later protocol integrations (e.g. Telegram via TDLib). Without a single mechanism, each grows its own ad-hoc loader, settings surface and error handling.

Three options were considered:

1. **Monolithic config** — services and themes as fixed sections in `settings.json`. Simplest, but no user-installed content, no enable/disable, no per-item versioning, and a corrupt entry endangers the whole file.
2. **In-process .NET plugins** — `AssemblyLoadContext`, `IChirpdeskPlugin`. Maximum power, minimum safety: full trust while the app holds every logged-in session, an API contract that must never break, and a bad plugin can hang the tray.
3. **Data-only plugins + out-of-process integrations** — one manifest, payloads are JSON/CSS, code lives in a separate process.

## Decision

Option 3. Every extension is `plugins/<id>/plugin.json` with `kind`, `version` and `apiVersion`. v1 implements `service`, `theme` and `filter`; `integration` is in the schema but not loadable until phase 6, and then only as a helper process speaking JSON-RPC over a named pipe.

No in-process assembly loading, ever — not even for first-party code.

## Consequences

- Themes and services get enable/disable, versioning, hot rescan and a plugin manager for ~200 lines of loader, because they were already data.
- User-installed themes and services work without shipping a new build.
- We accept a published schema as a compatibility promise: `apiVersion` gates breaking changes, unknown fields are ignored, and `schemas/` is validated in CI.
- Plugins cannot add UI. They contribute services, themes, filters and (later) commands. This keeps the zero-chrome design from being eroded by extension points.
- Page-context JavaScript injection is declared in the schema but **not implemented**: it would hand a third party the authenticated session. If it ever ships, it must be per-service, off by default, and clearly labelled as full session access.
- A hung integration costs one process and a killed pipe, not the app.
