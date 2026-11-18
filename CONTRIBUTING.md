# Contributing

Chirpdesk is a Windows tray app built with WPF and WebView2. Keep changes small, testable and
consistent with the ADRs in `docs/adr/`.

## Build and test

```powershell
dotnet build
dotnet test --filter Category!=Integration
pwsh -NoProfile -File scripts/gate.ps1 -SoakSeconds 10
```

`dotnet build` must stay warning-free. Tests tagged `Integration` need a real desktop and a
signed-in service; they are not run in CI.

## Rules that are not negotiable

Read `AGENTS.md` before touching code. In particular:

- never block login, WebSocket or update traffic;
- never delete outside the allowlisted roots, and send user data to the Recycle Bin;
- never trim cookies, local storage, IndexedDB or service workers;
- never log message bodies, contact names or full URLs with query strings;
- never show the window unless the user asked;
- never load plugin code in-process;
- never install an unverified plugin or silently accept a changed repository key.

A new irreversible decision needs an ADR in `docs/adr/`. New dependencies need an ADR too.

## Pull requests

Describe the user-visible change, the tests you ran, and any manual checks from
`docs/manual-verification.md`. Security-sensitive changes (updates, repositories, archives,
blocking, settings paths) need a test for the refusal path, not only the happy path.
