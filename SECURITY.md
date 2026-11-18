# Security policy

## Supported versions

Security fixes are shipped in the latest stable release. Older releases are not patched; the
signed updater is the supported upgrade path.

## Reporting a vulnerability

Use GitHub's **private vulnerability reporting** on this repository (Security -> Report a
vulnerability). Do not open a public issue for a vulnerability.

Please include:

- the affected version and Windows version;
- the smallest reproduction you can provide;
- whether the issue needs a malicious plugin, a malicious repository, a website, or local access.

Do not attach real session cookies, message content, profile folders, settings files or logs that
contain full URLs with query strings. Redact account names and message bodies.

## Scope

In scope: the WPF shell, WebView2 hosting, update verification, plugin/repository verification,
archive extraction, blocking rules, settings validation, and the installer/uninstaller.

Out of scope: the websites you sign into, the Microsoft Edge WebView2 runtime, and third-party
plugins or repositories that you explicitly install. A malicious plugin is still a security issue
for Chirpdesk if it can escape the data-only boundary; report it here.

## Secrets

No signing keys, tokens, cookies or session data belong in this repository. The private update and
registry keys are kept offline; only the pinned public keys ship in the app.
