# ADR 0018 - Userscripts are third-party code the user explicitly trusts

**Status:** accepted (2026-11-20); extends the data-only plugin rule from ADR 0007

## Context

Chirpdesk plugins are data: manifests, selectors, palette values and filter rules. The host
validates them and never loads code from a plugin. Users asked for Tampermonkey-style scripts,
which are code by definition and are usually installed from third-party sites, not from the
Chirpdesk registry.

A userscript runs inside the page, with the site's session. It can read anything the user can see
and send it anywhere. That is the point of the feature and the reason it cannot be treated like a
filter pack.

## Decision

1. **Userscripts are a shell feature, not a plugin kind.** They are not distributed by the plugin
   registry and never become part of a signed index. Sources are https URLs or local files the user
   chooses.
2. **Nothing installs silently.** The consent screen shows the name, version, `@match`/`@include`
   patterns, source and SHA-256, and states plainly that the script runs with the signed-in session.
   A script with no pattern is refused.
3. **Execution is in the page, after load.** Scripts are injected through WebView2 into the page's
   own world. `@run-at document-start` is recorded but not honoured in v1; every script runs after
   the document is ready.
4. **A small GM surface.** `GM_addStyle`, `GM_setValue`, `GM_getValue`, `GM_deleteValue`, `GM_log`,
   `GM_info` and `unsafeWindow` (the page window). Storage is namespaced per script in the site's
   localStorage. There is no `GM_xmlhttpRequest`, no `@require` and no network transport, so a
   script cannot ask the host to fetch for it.
5. **No silent updates.** Chirpdesk never re-downloads a script on its own. Installing the same
   name and namespace again replaces the file after a new consent.
6. **Disabling takes effect on the next page load.** Removing or disabling a script does not
   retroactively undo what it already did in a live page; the UI says so.

## Consequences

- The plugin trust model stays intact: every repository-signed artifact is still data-only.
- Users can install code from anywhere, which is exactly the risk the consent screen names. The app
  cannot audit a script.
- A userscript can exfiltrate messages through the page. The filter engine's allowlist protects
  auth/WebSocket traffic from the ad blocker, not from code the user installed.
- Scripts that depend on `GM_xmlhttpRequest`, `@require` or document-start timing will not work.
- Storage is per site, not per profile, because it lives in the page's localStorage.

## Revisit trigger

If users need network APIs or automatic updates, that is a new decision: it turns Chirpdesk into a
code-delivery channel with an update path. It needs its own ADR, a louder consent screen and an
explicit per-script network permission.
