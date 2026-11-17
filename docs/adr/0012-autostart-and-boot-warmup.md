# ADR 0012 — Autostart is one HKCU Run value plus an in-app warm-up delay

**Status:** accepted (2026-11-17)

## Context

Chirpdesk must start at login, stay tray-only, and not fight the rest of the login sequence with several Chromium process trees. The phase-0 gate approximated this with a `timeout 20` wrapper around the exe in `HKCU\...\Run`. That wrapper is a script hack: it leaves a `cmd.exe` in the process tree, is hard to inspect, and duplicates the delay for every launch.

The alternatives were: Windows Task Scheduler (supports a delay natively), a Windows service, or an HKCU Run value with the delay implemented by the app. A service is wrong for a per-user tray app and needs elevation. Task Scheduler adds a second configuration surface, a second place where the command can drift, and a task that is harder to remove cleanly.

## Decision

1. Autostart is exactly one `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value named `Chirpdesk`, containing `"<exe>" --autostart`. User scope only; no elevation, no machine-wide state, no service.
2. `--autostart` makes the app itself wait **20 s** before the first warm-up. The process and tray can exist immediately; no window is shown and no webview starts before the delay.
3. Warm-up is still the normal boot plan: only instances with `warmAtBoot`, in user `order`, staggered 1.5 s apart. The delay is additive, not a second scheduler.
4. `AppSettings.Autostart` is a mirror. The registry is the source of truth: startup reads it and updates the mirror, so a Run value removed outside the app is reflected honestly.
5. A failed read/write is logged and never fatal. The Settings toggle (phase 2 Task 7) surfaces the failure; the app keeps running without autostart.

## Consequences

- Uninstall (phase 6) must delete the Run value, and Settings must write it through one class.
- The app is resident during the 20 s delay; that is a few MB of WPF, not a Chromium tree.
- Moving to Task Scheduler later is possible without changing the launch contract (`--autostart` already means "delayed boot"), but it would need a migration for existing installs.
- Users who inspect autostart see an ordinary per-user Run entry, not an opaque scheduled task.
