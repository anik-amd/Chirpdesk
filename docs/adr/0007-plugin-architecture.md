# ADR 0007 — Everything extensible is a data plugin; code only out-of-process

**Status:** accepted (2026-11-16)

## Context

Chirpdesk needs five kinds of extension: services (WhatsApp, Messenger, any site), themes (Dracula, Catppuccin …), filter packs, small tools (disk cleaner, diagnostics), and later protocol integrations (e.g. Telegram via TDLib). Without a single mechanism, each grows its own ad-hoc loader, settings surface and error handling.

Three options were considered:

1. **Monolithic config** — services and themes as fixed sections in `settings.json`. Simplest, but no user-installed content, no enable/disable, no per-item versioning, and a corrupt entry endangers the whole file.
2. **In-process .NET plugins** — `AssemblyLoadContext`, `IChirpdeskPlugin`. Maximum power, minimum safety: full trust while the app holds every logged-in session, an API contract that must never break, and a bad plugin can hang the tray.
3. **Data-only plugins + out-of-process integrations** — one manifest, payloads are JSON/CSS, code lives in a separate process.

## Decision

Option 3. Every extension is `plugins/<id>/plugin.json` with `kind`, `version` and `apiVersion`. v1 implements:

- `service`, plus `"template": true` for the "add any site" flow — a template is a service that produces a derived service instance, so profiles, hosting and the repo format are shared.
- `theme`, **one plugin per family** with a `variants[]` array carrying `mode: dark|light`. A family ships both modes in one folder; the shell stores the family and derives the variant from `defaults`.
- `filter` — adblock packs are data, so they are plugin content, not a build artifact.
- `tool` — a declarative `panel.json` composed from a closed set of host primitives (`list`, `table`, `text`, `progress`, `button`, `separator`) and a closed set of host actions (`storageReport`, `trimCache`, `scanOrphans`, `deleteOrphan`, `revealFolder`, `openSettings`, `enablePlugin`, …). The host renders and validates everything; a tool never supplies a path and never executes anything.

`integration` is in the schema but not loadable until phase 7, and then only as a helper process speaking JSON-RPC over a named pipe.

No in-process assembly loading, ever — not even for first-party code.

## Consequences

- Themes and services get enable/disable, versioning, hot rescan and a plugin manager for ~200 lines of loader, because they were already data.
- User-installed themes and services work without shipping a new build, and a theme family that gains a light variant later needs no migration — `defaults.light` simply starts resolving.
- Because `tool` panels are declarative and action-limited, the desk cleaner is a plugin without becoming a second UI toolkit. The zero-chrome constraint survives "everything is a plugin".
- We accept a published schema as a compatibility promise: `apiVersion` gates breaking changes, unknown fields are ignored, and `schemas/` is validated in CI.
- Plugins still cannot add arbitrary UI. They compose host primitives and host actions, and nothing else.
- A closed action set is a real limitation: a tool that needs an operation we did not anticipate cannot ship until the host adds a primitive. That friction is deliberate — it is the same trade as the auth allowlist.
- Page-context JavaScript injection is declared in the schema but **not implemented**: it would hand a third party the authenticated session. If it ever ships, it must be per-service, off by default, and clearly labelled as full session access.
- A hung integration costs one process and a killed pipe, not the app.
