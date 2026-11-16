# ADR 0004 — Ad blocking is fail-open with a permanent auth allowlist

**Status:** accepted (2026-11-16)

## Context

WebView2 can cancel requests in `WebResourceRequested`. A chat client that cannot log in or drops its WebSocket is worse than one that shows an ad, and the sites we host change their endpoints without notice.

## Decision

Three tiers — curated hosts (on), cosmetic CSS (on), imported ABP lists (off) — evaluated by one compiled matcher with a per-host verdict cache. Policy:

1. Any matcher exception, invalid filter file, or ambiguous rule → **allow**.
2. An allowlist covering auth, WebSocket and update endpoints is applied **after** all tiers, so nothing can override it. The service's own origin is allowlisted automatically before any pack loads, and a filter pack can only add entries, never remove one.
3. Per-service disable is a single command (`> blocking: off`).

## Consequences

- Some ads will pass. That is the intended trade.
- Cosmetic filtering can only hide elements, never block requests, so a rotten selector is a visual bug, not a functional one.
- Tier 3 lists are parsed offline into a domain trie; unsupported rule types (`$csp`, `$redirect`, `$removeparam`) are counted and surfaced rather than silently applied.
- Reviewers of filter packs must keep the allowlist in sync with service login flows; a test fixture asserts the allowlist dominates every tier.
