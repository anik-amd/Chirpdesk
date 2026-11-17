# Phase 2 — Lifecycle, storage and the settings surface

**Status:** planned (2026-11-17). Phase 1 automated gate is green; its human rows are still open.

Continues `docs/plans/2026-11-16-phase-1-command-bar.md`. Read before writing code: `PLAN.md` §5.3–5.4, §8, §11, §13, §15; ADR 0005 (per-profile environments), ADR 0011 (sites are plugins; instances are settings), ADR 0002 (one palette, reserved keys), and `docs/testing-strategy.md` §5.

**Carry-over from phase 1:** one window, hidden-warm; the palette; service registry with warm/cold switch and tab close; plugin-backed definitions; `Ctrl+W` closes a tab; `Esc` hides. Phase 2 adds the missing lifecycle and storage layers, not a second shell.

**Measured constraint:** two warm services (WhatsApp + Telegram) cost **1387.6 MB** and 389.7 MB on disk (`docs/measurements.md`, 2026-11-17). The phase-2 gate asks for three warm + one cold; take the next measurement before recommending three defaults and revisit ADR 0005 if four warm exceed ~1.6 GB.

---

## Task 1: Settings v3 — lifecycle and storage preferences (TDD)

**Status:** complete (2026-11-17). Schema 3 adds `DndUntil`, `Autostart`, per-profile/global cache budgets and `ReclaimedBytes`; v2 migrates additively. Budgets clamp to 10–2000 MB per profile and never exceed the global budget; reclaimed bytes cannot go negative.

**Files:**
- Modify: `src/Chirpdesk.Core/Settings/AppSettings.cs`, `SettingsStore.cs`, `SettingsValidator.cs`
- Test: `tests/Chirpdesk.Core.Tests/Settings/SettingsMigrationTests.cs`

Add only what the phase needs: `DndUntil` (nullable UTC timestamp), `Autostart` (mirror of the OS entry), `CacheBudgetMb` (default 60), `GlobalCacheBudgetMb` (default 180), `ReclaimedBytes` (counter), and per-instance `LastSleepAt` if the UI needs it. Bump to schema 3; migration from v2 is additive and writes back once. A v3 file opened by a v2 build still refuses downgrade.

**Tests:** v2 fixture migrates to v3 with the same instances; defaults are filled; negative/absurd budgets are rejected or clamped; DND timestamps round-trip; unknown fields still ignored.

**Commit:** `feat(core): settings v3 for lifecycle and storage`.

---

## Task 2: Sleep and wake runtimes (TDD)

**Status:** complete (2026-11-17). `LifecycleTracker` in Core owns warm/asleep/cold; `ServiceRegistry.SleepAsync` disposes exactly once, removes the tab from MRU, tracks it as asleep, and hands over to the next warm tab (or none). Waking reuses the normal activation path, which creates the runtime and marks it warm. Selftest sleeps both test runtimes and re-adds/wakes one. Sleep/wake controls land in the settings window (Task 7); `warmAtBoot` is untouched by sleep.

**Files:**
- Modify: `src/Chirpdesk/Hosting/ServiceRegistry.cs`, `ServiceRuntime.cs`, `App.xaml.cs`
- Create: `src/Chirpdesk.Core/Lifecycle/RuntimeState.cs` (or keep the enum in Core.Hosting-free form)
- Test: `tests/Chirpdesk.Core.Tests/Lifecycle/RuntimeStateTests.cs`

The registry currently knows warm and cold only. Add the three states from PLAN §5.3: **warm** (runtime alive), **asleep** (runtime disposed this session, profile intact), **cold** (no runtime this session). `SleepAsync(id)` disposes exactly once, removes the tab from the MRU cycle, keeps the instance addressable as asleep; `WakeAsync(id)` creates a runtime through `ServiceRuntime.CreateAsync` and activates it. Selecting an asleep service wakes it. Persisted `warmAtBoot` is a separate axis and never changes on sleep.

**Tests:** sleep disposes once and the state becomes asleep; waking recreates and activates; sleeping the active tab activates the next warm tab or leaves none active; a shared profile is not disposed twice; MRU excludes asleep tabs but restores the instance on wake; cold wake works.

**Commit:** `feat(app): sleep and wake services`.

---

## Task 3: Boot warm-up and autostart (TDD)

**Status:** complete (2026-11-17). `BootPlan` selects pinned instances in user order and staggers 1.5 s; a normal launch warms them hidden (verified live: `boot: warmed 2 pinned service(s)`, no `window shown`). `--autostart` adds a 20 s in-app delay; the HKCU Run value is written through `Interop.Autostart`, and startup mirrors its real state into settings. ADR 0012 records the choice over Task Scheduler.

**Files:**
- Create: `src/Chirpdesk.Core/Lifecycle/BootPlan.cs`, `src/Chirpdesk/Interop/Autostart.cs`
- Modify: `src/Chirpdesk/App.xaml.cs`, `StartupOptions.cs`, `scripts/autostart.ps1`
- Test: `tests/Chirpdesk.Core.Tests/Lifecycle/BootPlanTests.cs`
- ADR: `docs/adr/0012-autostart-and-boot-warmup.md`

Normal startup warms every instance with `warmAtBoot`, staggered 1.5 s apart in `order`, hidden and without showing the window. `--autostart` additionally waits 20 s before the first warm-up; `--warm-all`/`--open` remain diagnostics. Autostart is `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` with `--autostart`, not Task Scheduler; the setting mirrors what the registry says and a failed write is logged and surfaced.

**Tests:** boot plan includes only pinned instances, ordered by `order`, staggering uses the injected clock; a skipped/failed instance does not stop the rest; autostart command string is correct and idempotent; `--autostart` delay applies only to warm-up, never to the tray.

**Commit:** `feat(app): warm pinned services at boot and autostart`.

---

## Task 4: Storage scan and cache trimming (TDD)

**Status:** complete (2026-11-17). `PathGuard` pins deletes inside the profiles root; `TrimPlanner` selects cache bytes only, skips warm profiles, applies per-profile excess then the oldest disposed profiles for the global budget. App `ProfileScanner` reports session Data vs Cache separately; `CacheTrimmer` deletes only the five cache folder names. UI/scheduling hooks land with the settings window and cleaner panel.

**Files:**
- Create: `src/Chirpdesk.Core/Storage/ProfileUsage.cs`, `TrimPlanner.cs`, `PathGuard.cs`
- Create: `src/Chirpdesk/Storage/ProfileScanner.cs`, `CacheTrimmer.cs`
- Test: `tests/Chirpdesk.Core.Tests/Storage/TrimPlannerTests.cs`, `PathGuardTests.cs`

The unit is the **profile**, never the instance. Report **Data** (`Cookies`, `Local Storage`, `IndexedDB`, `Service Worker`) and **Cache** (`Cache`, `Code Cache`, `GPUCache`, `DawnCache`, `ShaderCache`) separately. Trim deletes only the cache folder set, only for disposed profiles, oldest-first until the per-profile and global budgets are met; warm/asleep-on-disk profiles are never trimmed under a live webview. Update `ReclaimedBytes` after a successful trim.

**Tests:** session folders are never selected; reparse points and paths outside `%LOCALAPPDATA%\Chirpdesk\profiles` are refused; warm profiles are skipped; budgets choose the oldest profiles; a partial failure logs and does not abort the rest; empty/missing profiles are harmless.

**Commit:** `feat(core): plan safe cache trims`.

---

## Task 5: Orphan scan and Recycle Bin (TDD)

**Status:** complete (2026-11-17). `OrphanScanner` reports unreferenced profile folders and the fixed known app roots only, skipping reparse points and running owners. `KnownRoots`/`ProcessProbe` build the allowlist; `RecycleBin` uses `SHFileOperation` with `FOF_ALLOWUNDO`; `OrphanDeleter` re-verifies that a profile is a direct child of the profiles root or that the path is an exact known root. Recycle Bin restore still needs one human check.

**Files:**
- Create: `src/Chirpdesk.Core/Storage/OrphanScanner.cs`, `KnownRoots.cs`
- Create: `src/Chirpdesk/Interop/RecycleBin.cs`, `src/Chirpdesk/Storage/ProcessProbe.cs`
- Test: `tests/Chirpdesk.Core.Tests/Storage/OrphanScannerTests.cs`

Scan the fixed roots from PLAN §8.3 only: Chirpdesk profiles not referenced by settings, WhatsApp desktop, Telegram Desktop, and the named Electron wrappers. Report path, bytes, last-modified and owning app; nothing pre-selected. A profile referenced by an instance — including a shared profile — is never an orphan. Skip directories owned by a running process. User data deletion goes to the Recycle Bin (`FOF_ALLOWUNDO`); only cache subfolders are hard-deleted.

**Tests:** a seeded unreferenced profile is found; a referenced/shared profile is not; traversal and reparse escapes are refused; a running owner is skipped; unknown roots are ignored; the scan never returns a path outside the allowlist.

**Commit:** `feat(core): scan known app data and profile orphans safely`.

---

## Task 6: Instance creation from installed definitions (TDD)

**Files:**
- Create: `src/Chirpdesk.Core/Settings/InstanceFactory.cs`
- Modify: `src/Chirpdesk/App.xaml.cs`, `SettingsStore`
- Test: `tests/Chirpdesk.Core.Tests/Settings/InstanceFactoryTests.cs`

ADR 0011 makes this the only way to add a site: pick an installed `service` definition, derive a stable instance id, write the row cold with a fresh profile, and offer "warm at boot" after first sign-in. No URL entry, no ad-hoc custom instance. Two accounts are two instances of one definition; Messenger and Facebook remain separate definitions.

**Tests:** ids are slugged and unique; a definition with no URL is refused; two instances of one definition get distinct ids and profiles; the new row starts cold; a disabled/missing definition is refused fail-open.

**Commit:** `feat(app): create instances from installed definitions`.

---

## Task 7: Settings window (UI)

**Files:**
- Create: `src/Chirpdesk/Settings/SettingsWindow.xaml(.cs)`, `SettingsViewModel.cs`
- Modify: `TrayIconFactory.cs` (Settings item), `HostWindow` if the window is owned
- Test: manual row 11; pure formatting/mapping covered in Core tests where it exists

One window, 720 × 600, resizable, opened only by the user (tray → Settings). No tabs, rail or command strip. Sections in one scroll: **Services** (instances with name, per-instance regex, warm at boot, mute, sleep/wake, remove), **Add service** (installed definitions only), **Appearance** (system/light/dark), **Autostart**, **Do Not Disturb**, **Storage** (per-profile Data/Cache/Last trim, Trim cache, Orphan scan, Reclaimed total). Destructive actions show the exact path and byte count first.

**Commit:** `feat(ui): settings window for services, lifecycle and storage`.

---

## Task 8: Mute and Do Not Disturb (TDD)

**Files:**
- Create: `src/Chirpdesk.Core/Notifications/NotificationPolicy.cs`
- Modify: `src/Chirpdesk/Notifications/ToastService.cs` wiring, `App.xaml.cs`, settings window/tray
- Test: `tests/Chirpdesk.Core.Tests/Notifications/NotificationPolicyTests.cs`

Filter toasts, not events: a muted instance suppresses its toasts; global DND until a timestamp suppresses every toast. Unread counts and the title fallback keep running; notifications that expire during DND are dropped, not replayed. DND choices for v1 are off / 30 min / 1 h / until tomorrow; no schedule editor yet. The tray shows DND state and clears it.

**Tests:** mute suppresses that instance only; DND suppresses all; expired/`null` DND does not; policy never blocks the unread counter; a DND change applies to the next toast without restarting a webview.

**Commit:** `feat(core): suppress muted and DND toasts without losing counts`.

---

## Task 9: Bundled cleaner `tool` panel (TDD)

**Files:**
- Create: `plugins/tool-cleaner/plugin.json`, `panel.json`
- Create: `src/Chirpdesk.Core/Plugins/PanelDefinition.cs`, `PanelValidator.cs`
- Create: `src/Chirpdesk/Settings/ToolPanelView.xaml(.cs)`
- Modify: `PluginLoader` to surface bundled tools, `SettingsWindow` Storage section
- Test: `tests/Chirpdesk.Core.Tests/Plugins/PanelValidatorTests.cs`

Render the closed primitive set (`list`, `table`, `text`, `progress`, `button`, `separator`) and execute only host actions (`storageReport`, `trimCache`, `scanOrphans`, `deleteOrphan`, `revealFolder`, `openSettings`). The plugin names a root from the fixed enum and never a path. Unknown primitive or invalid argument disables that action, not the panel (invariant 8). User-installed tools stay refused in v1.

**Tests:** valid panel loads; unknown primitive disables one action; bad argument is rejected; `revealFolder` cannot carry a path; delete only accepts an id from the current scan; a user `tool` is refused while a bundled one loads.

**Commit:** `feat(app): render the bundled cleaner tool panel`.

---

## Task 10: Phase 2 gate

### 10.1 Automated — extend `--selftest`

| Check | Asserts |
|---|---|
| settings v3 | v2 fixture migrates, defaults fill, DND timestamp round-trips |
| lifecycle | sleep disposes once, wake recreates, warm-at-boot list is ordered |
| storage | session folders never selected, warm profiles skipped, budgets respected, allowlist holds |
| orphans | unreferenced profile found, referenced/shared profile not, running owner skipped |
| dnd | mute/DND suppress only toasts, never unread counting |
| autostart | command string/idempotence with a fake registry writer |

`scripts/gate.ps1` gains the storage/orphan fixture and prints one line per check.

### 10.2 Human — what automation cannot judge

- Reboot with **3 warm + 1 cold**: toasts within 30 s, no storm, no window.
- Sleep a warm tab, receive nothing, wake it: session still signed in and the socket reconnects.
- Storage page shows real Data vs Cache numbers; trim reclaims > 0 bytes; session still valid.
- Orphan scan finds a seeded leftover, restore from Recycle Bin works.
- Settings changes apply without restart; 720 × 600 at 100 % and 150 % scaling.
- DND 30 min suppresses toasts; unread keeps counting; tray clears DND.
- Two instances of one definition keep separate sessions after a sleep/wake cycle.

### 10.3 Checklist

- [ ] Three warm + one cold after reboot, no boot storm, no window.
- [ ] Disk stays under 200 MB after a week of normal use.
- [ ] A seeded orphan is found and restored from the Recycle Bin.
- [ ] Instance creation from an installed definition works; no URL add path exists.
- [ ] Sleep/wake keeps the session; closing a tab still makes it cold.
- [ ] RAM with three warm recorded; ADR 0005 trigger re-evaluated with that number.
- [ ] Quit leaves no orphan `msedgewebview2.exe`.

**Commit:** `test: phase 2 results`.

---

## Deliberately out of scope

Blocking/filters (phase 3), theme families and token pipeline (phase 4), plugin repositories and manager UI (phase 5), installer/updater (phase 6), TDLib integration (phase 7). Autostart writes one HKCU value only; no service, no elevation, no Task Scheduler.
