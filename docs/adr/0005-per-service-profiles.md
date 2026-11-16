# ADR 0005 — One WebView2 environment per profile, one profile per account

**Status:** accepted (2026-11-16), terminology corrected after review

## Context

Users need multiple accounts of the same service (work/personal WhatsApp, two Messengers) and must be able to add any site twice. A shared cookie jar makes that impossible. Chromium also keeps a single session per profile, so isolation can only come from separate profile directories.

The model must distinguish a **definition** (the WhatsApp plugin: url, regex, icon) from an **instance** (your work WhatsApp: one profile, one unread count). Conflating them would force a plugin folder per account.

## Decision

One `CoreWebView2Environment` per **profile**, with `userDataFolder = %LOCALAPPDATA%\Chirpdesk\profiles\<profileId>`. Profile ids are validated (`^[a-z0-9-]{1,32}$`, unique) and resolved under the profiles root, never as absolute paths from settings. Instances are settings entries, not plugins; an instance's `profileId` defaults to its `instanceId`.

Deliberate exception: `shareProfileWith` lets Messenger pair with Facebook so one sign-in serves both. Off by default, the UI states that both entries then share a cookie jar, and one instance is the **profile owner**. The two instances share the owner's single environment object; creating two environments over one folder is invalid, so the profile — not the instance — is the unit of environment ownership. Deleting the owner requires unsharing first.

The per-profile environment is not free: each one is a separate browser process tree. If 4 warm services exceed ~1.6 GB RAM on the 12 GB target machine, the fallback is one environment with multiple WebView2 profiles (`CreateCoreWebView2ControllerOptions` + `ProfileName`): fewer processes, slightly weaker isolation, and a migration of profile folders. Phase 1 measures before that decision is needed.

## Consequences

- True isolation, at the cost of more processes and RAM per warm profile.
- Two WhatsApp accounts cost two full webviews; sleep is the only mitigation.
- Deleting a dependent instance never deletes the shared profile; deleting an owner is blocked until every dependent is unshared or removed. Standalone instances delete only their own profile directory (path-guarded), and the Recycle Bin is used for user data.
- Cookie/session backup is per profile; there is no "move account" feature in v1.
- The environment-per-profile choice is reversible only with a profile-folder migration, so it is measured in phase 1 rather than assumed forever.
