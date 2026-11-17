# ADR 0011 — A site enters Chirpdesk only as a service plugin

**Status:** accepted (2026-11-17); supersedes the built-in any-site add flow in ADR 0007 and `PLAN.md` §4.1, §11, R7 and R15

## Context

ADR 0007 kept two on-ramps for a site: a `service` definition plugin for curated sites, and a built-in "add any site by URL" flow that wrote a settings instance with `definitionId: null`. The second path was justified as making an arbitrary chat usable without waiting for a plugin.

The product owner has narrowed it: sites are added **with plugins**, so a separate ad-hoc add flow is not wanted. The reasons are the same ones that made definitions plugins in the first place:

- Two on-ramps mean two quality levels. A URL-added site has no curated unread regex, icon, notification mode or `pageDarkMode`; it is permanently the worst-configured instance in the app, and no plugin author ever exercises the path a user took to reach it.
- A definition is the only object that knows how a site behaves — how it reports unread counts, whether it has its own dark theme, how a notification tag routes. Guessing those from a URL is exactly what the app must not do.
- One content path makes the contract small: one manifest, one validator, one set of docs. Community contributions and AI-agent-authored plugins then target the same thing users install.

## Decision

1. **A site exists only as a `service` definition plugin.** There is no built-in URL form, no `--add-site`, and no creation path that produces `definitionId: null`.
2. **Instances are still settings, not plugins.** A definition is shared and versioned; an instance is one account of it (`definitionId`, profile, warm/cold state). Installing or enabling a definition makes its site available; running it writes an instance. Two accounts of one site are two instances of one definition, as before. Sibling sites stay separate definitions even under one vendor — Messenger (`messenger.com`) and Facebook (`facebook.com`) are two plugins, not one plugin with a mode — and each supports multiple accounts the same way.
3. **Legacy custom instances keep working.** An existing row with `definitionId: null` and an inline `url` still resolves from that row, and an unknown definition id with a URL still resolves fail-open. They are read compatibility, not a supported creation path. With no URL and no definition, activation reports `definition missing` and does not crash.
4. **Dark-mode policy stays in the plugin.** A definition declares `pageDarkMode: off | auto` and the host owns the mechanism (ADR 0010). Plugin data selects behaviour; plugins never supply executable theme code (ADR 0007).
5. **Authoring is part of the contract.** `docs/plugins.md` is the single authoring guide, written so a human or an AI agent can produce a valid plugin without reading the host source. The C# validator is the source of truth in v1; a published JSON Schema ships with the loader.

## Consequences

- The plugin loader is now the only on-ramp for a new site, so it is a prerequisite for any "add service" surface. Until it lands, the bundled definitions and existing settings rows are the whole catalogue.
- `StartupOptions`, `App` and the phase-1 plan lose the add-site flow and its id slugging; there is less code and fewer tests, not more.
- A site with no definition plugin cannot be added, only requested. That friction is accepted: the answer is a one-file plugin, not a second content model.
- `PLAN.md` R7 ("add any chat site by URL") becomes "add a chat site by installing its definition plugin"; R15 drops the add flow from the shell scope.
- The documentation burden moves up. If authoring is not genuinely easy, this decision makes the app less useful than the flow it removed.
- Existing profiles and settings are untouched; no migration is needed to stop creating custom instances.
