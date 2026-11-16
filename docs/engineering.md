# Engineering conventions

Read `PLAN.md` for what we are building, `AGENTS.md` for the invariants that must never break, and `docs/testing-strategy.md` for what must be tested.

## Projects

| Project | Rule |
|---|---|
| `src/Chirpdesk.Core` | pure .NET 8, **no WPF, no WebView2, no OS calls**. Everything else depends on it. |
| `src/Chirpdesk` | WPF shell + WebView2 interop. Thin: wiring, rendering, P/Invoke. No business rules. Logging goes through `ILog`, defined in Core. |
| `tests/Chirpdesk.Core.Tests` | unit tests only; no integration category by default |

A pull request that puts a decision in `Chirpdesk` instead of `Chirpdesk.Core` is wrong even if it works — it becomes untestable.

## Build and test

```powershell
dotnet build                      # must be warning-free
dotnet test --filter Category!=Integration
dotnet run --project src/Chirpdesk
```

`TreatWarningsAsErrors` is on. Suppress specifics locally with `#pragma` and a reason, never globally.

## Commits

Conventional Commits, imperative, scoped:

```
feat(core): parse unread counts from window titles
fix(app): keep toast activation on the owning service
docs(adr): record fail-open blocking policy
test(core): cover allowlist precedence
chore: pin SDK to 8.0.100
```

One logical change per commit. Phase plans (`docs/plans/`) prescribe the commit at each task; follow it so history reads as the plan.

## Branches

`main` is always buildable. Work happens on `feat/<phase>-<slug>` (e.g. `feat/p1-command-bar`), merged after review. No direct pushes to `main` for feature work once phase 1 starts.

## Definition of done (per task)

1. Tests written first and failing for the right reason.
2. `dotnet build` clean, `dotnet test` green.
3. New decision recorded in an ADR if it is not reversible cheaply.
4. Performance budgets in `PLAN.md` §2.1 not regressed (Command Bar < 50 ms, switch < 100 ms, toast < 1 s).
5. Committed with the message from the phase plan.

## Definition of done (per phase)

All of the above, plus the manual E2E matrix rows for that phase in `docs/testing-strategy.md` §5, plus a `docs/measurements.md` entry with RAM, disk and latency numbers.

## Review

After each phase task, request review with the **Requesting Code Review** skill: capture `BASE_SHA`/`HEAD_SHA`, dispatch a reviewer against the phase plan, fix Critical and Important findings before continuing. Minor findings get a `TODO(phase-n)` note, not a silent skip.

## Code style

- Nullable reference types on, `ImplicitUsings` on, file-scoped namespaces.
- No `async void` except event handlers; no `.Result`/`.Wait()` on async paths.
- P/Invoke lives in `Interop/` files, one class per Win32 area, with `SetLastError` and a throwing wrapper.
- Logging: a tiny built-in `ILog` in `Chirpdesk.Core` with a rolling file sink (2 MB cap). **Never log message bodies, contacts or URLs with query strings.** `Microsoft.Extensions.Logging` would be a third runtime package and is deliberately not used.
- Errors surfaced to the user must name the service and the next action, never a bare HRESULT.

## Secrets and sensitive data

- No signing keys, tokens or certificates in the repo or CI logs. The ECDSA private key lives off-repo; only the public SPKI key ships.
- The repo stays private until v1; even then, no user data, no crash dumps with session data.
- Filter packs and cosmetic packs are data; they must not contain site cookies or tokens.

## Versioning and releases

- SemVer, starting `0.1.0` at the end of phase 0.
- Tags `v0.1.0` build the installer + zip and sign the manifest with ECDSA P-256 (phase 6 onward).
- Release notes are generated from Conventional Commits and curated by hand; no auto-published drafts.
