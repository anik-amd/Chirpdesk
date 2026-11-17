# Phase 3 — Blocking

**Status:** automated gate complete (2026-11-17). `FilterPack`, `PluginLoader.LoadFilters`,
`RequestMatcher` (permanent allowlist, service-origin allowlist, per-host verdict cache, cosmetic
selectors) and the bundled `filters-core` pack are in. `ServiceRuntime` cancels blocked requests
with a 403, counts them per runtime and injects tier-2 CSS after navigation; `ServiceInstance.Blocking`
is honoured at creation and through the per-tab `blocking` command. Settings shows `blocking on/off`
plus the live blocked count. `--selftest` now has a `blocking` check (allowlist, tier-1 hit, cosmetic,
fail-open, bundled pack). 234 unit tests pass. Human rows 7/8 (real logins with blocking on, corrupt
pack still logs in) remain for the user's manual pass. Tier 3 (ABP import) and the tray hint stay
deferred to phase 6/7 per ADR 0004.
**ADR:** 0004 (fail-open blocking), 0007 (plugin architecture), 0011 (sites are plugins)
**Plan:** `PLAN.md` §7, phase 3 row in §15

## Goal

Block ad/tracker requests without ever breaking login, WebSocket or update traffic. Tier 1
(curated hosts) and tier 2 (cosmetic selectors) ship as a bundled `filter` plugin; tier 3 (ABP
import) stays off until phase 7. Every decision is fail-open and the permanent allowlist wins over
every tier.

## Invariants this phase must prove

1. Allowlist beats every pack: a test fixture asserts it for host and path rules.
2. Invalid or throwing rule → allow, never block, never crash startup.
3. A filter plugin may add allowlist entries, never remove the permanent ones.
4. Cosmetic filtering only hides; it never cancels a request.
5. Counters are real: blocked requests are counted per service and visible.

## Tasks

1. **Filter pack model** (`Chirpdesk.Core/Blocking/FilterPack.cs`)
   - `blockHosts`, `blockPaths`, `allowHosts`, `cosmetic`.
   - Fail-open deserialization: unknown fields ignored, bad entries skipped and counted.
2. **Pack loader** (`PluginLoader.LoadFilters`)
   - Reads `filters.json` from `filter` plugins, bundled and user.
   - A broken pack is skipped with a log line; other packs still load.
3. **Matcher** (`Chirpdesk.Core/Blocking/RequestMatcher.cs`)
   - Host patterns: exact and `*.suffix` (matches suffix and subdomains).
   - Path patterns: glob (`*`) compiled with a timeout.
   - Permanent allowlist first: auth/WebSocket/update hosts and paths.
   - Service origin allowlisted before packs are merged.
   - Per-request verdict cache keyed by host + path, bounded.
   - `Evaluate(uri, resourceContext)` returns `Block` or `Allow`; exceptions → `Allow`.
4. **Cosmetic selectors** (`RequestMatcher.CosmeticCss`)
   - Deduplicated selector list; invalid selectors are the page's problem, never a crash.
5. **Bundled pack** (`plugins/filters-core/`)
   - `plugin.json` kind `filter`, `filters.json` with a small conservative tier-1 list.
6. **WebView2 wiring** (`ServiceRuntime`)
   - `AddWebResourceRequestedFilter("*", All)` and a `WebResourceRequested` handler.
   - Cancel only on `Block`; increment `BlockedRequests`.
   - Honour `ServiceInstance.Blocking` (`off` disables the matcher).
7. **Surface**
   - Settings: per-service blocked counter and the `blocking: off` state.
   - Palette command: `blocking: off` / `blocking: default` for the current tab.
   - Tray hint on repeated failure is deferred to phase 6.
8. **Gate**
   - Unit tests green; `--selftest` extended with allowlist and fail-open checks.

## Definition of done (PLAN §15)

WhatsApp + Messenger + Telegram log in and send messages with blocking on; counters show blocked
requests; `blocking: off` for one service restores every request.
