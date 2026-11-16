# ADR 0005 — Per-service WebView2 profiles, one environment each

**Status:** accepted (2026-11-16)

## Context

Users need multiple accounts of the same service (work/personal WhatsApp, two Messengers) and must be able to add any site twice. A shared cookie jar makes that impossible. Chromium also keeps a single session per profile, so isolation can only come from separate profile directories.

## Decision

One `CoreWebView2Environment` per service instance, with `userDataFolder = %LOCALAPPDATA%\Chirpdesk\profiles\<serviceId>`. Service ids are validated (`^[a-z0-9-]{1,32}$`, unique) and resolved under the profiles root, never as absolute paths from settings.

Deliberate exception: `shareProfileWith` lets Messenger pair with Facebook so one sign-in serves both. Off by default, and the UI must state that both entries then share a cookie jar.

## Consequences

- True isolation, at the cost of more processes and RAM per warm service.
- Two WhatsApp accounts cost two full webviews; sleep is the only mitigation.
- Deleting a service deletes only its own profile directory (path-guarded), and the Recycle Bin is used for user data.
- Cookie/session backup is per service; there is no "move account" feature in v1.
