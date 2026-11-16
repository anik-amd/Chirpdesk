# Measurements

Numbers are only meaningful on the machine they were taken on. Record the build SHA every time.

| Date | Build | Phase | Notification mode | Boot→warm | RAM (shell) | RAM per warm service | Disk (app) | Disk (profiles) | Toast latency | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| — | — | 0 | — | — | — | — | — | — | — | pending |

## Method

- Boot→warm: stopwatch from login to "all configured services report ready" (host log line).
- RAM: Task Manager → Details, sum of `Chirpdesk.exe` + its `msedgewebview2.exe` children, per service (one service warm at a time for the per-service column).
- Disk: size of the app directory, and of `%LOCALAPPDATA%\Chirpdesk\profiles`.
- Toast latency: site's own notification timestamp vs host log timestamp (instrumented in phase 0).
- Notification mode: the phase-0 probe result per service (`native`, `shim` or `title-only`) and whether the toast contained sender/text.
