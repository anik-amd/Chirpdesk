# Phase 1 — Command Bar, definitions, multi-service

**Status:** Tasks 1–4 and 4b complete (2026-11-17): definitions, settings v2, registry, warm/cold activation, MRU cycling, per-profile environments, and the service plugin loader. Bundled definitions now live as `plugins/<id>/` folders copied beside the exe; user plugins load from `%APPDATA%\Chirpdesk\plugins` and shadow bundled ids. Next: Task 5 (Command Bar cycle mode).

Continues `docs/plans/2026-11-16-phase-0-spine.md`. Do not start until phase 0's soak, reboot row and toast-click row are ticked: this phase multiplies every one of those behaviours by the number of services.

Read before writing code: `PLAN.md` §5.1, §6, §11, §12; ADR 0002 (bar and keys), ADR 0005 (one environment per profile), ADR 0007 (data-only plugins).

**What phase 0 left in place, and must not be thrown away:** `HostWindow` (off-screen, `WS_EX_TOOLWINDOW`), `SingleInstance` + the show event, `ServiceRuntime` (park/activate, gate, dedupe, bridge), `WebViewHost` (per-profile environment), `SettingsStore` + `ServiceInstance`, `MruList`, `SelfTest`, `scripts/gate.ps1`. Phase 1 adds a layer above them, not a replacement.

**Measured constraint:** one signed-in WhatsApp is 547 MB private working set (`docs/measurements.md`). The bar's warm list must therefore be honest about cost, and default warm count guidance is two or three, not "as many as you like".

---

## Task 1: Definition and manifest model (TDD)

**Files:**
- Create: `src/Chirpdesk.Core/Definitions/ServiceDefinition.cs`, `ThemeDefinition.cs`, `PluginManifest.cs`, `DefinitionValidator.cs`
- Test: `tests/Chirpdesk.Core.Tests/Definitions/DefinitionValidatorTests.cs`

A definition is data-only (ADR 0007). Fields that matter now: `id`, `name`, `url`, `icon` (relative file name inside the plugin), `unreadRegex`, `notificationMode` (`native|shim|title-only`), `pageDarkMode` (`off|auto`, ADR 0010), `userAgent`, `activation` (optional `urlTemplate` and `tagPattern`, see below), `minApiVersion`, `apiVersion`.

Validation is fail-open: an invalid definition is skipped and logged, never fatal, never silently replaced. Rules: ids match `^[a-z0-9][a-z0-9-]{0,31}$`; `url` is absolute https with no credentials; `notificationMode` is one of the three; `minApiVersion` ≤ host API version; `icon` has no separators or `..`; unknown JSON fields are ignored.

**Tests:** each rule accepted and rejected at least once; unknown field ignored; a definition with no icon still loads.

`activation` is the one field that exists purely for the click. It is a URL template such as `https://web.whatsapp.com/send?phone={tag}` plus a `tagPattern` the tag must match before substitution; if the tag does not match, the click opens the service and nothing more. The host substitutes only the tag, never arbitrary notification text, so a hostile page cannot steer the resulting URL. This is the concrete reason a definition beats a bare URL: only the definition knows how its site addresses a conversation.

**Commit:** `feat(core): validate data-only service and theme definitions`.

---

## Task 2: Settings schema v2 — definitions and instances (TDD)

**Files:**
- Modify: `src/Chirpdesk.Core/Settings/AppSettings.cs`, `ServiceInstance.cs`, `SettingsStore.cs`, `SettingsValidator.cs`
- Test: `tests/Chirpdesk.Core.Tests/Settings/SettingsMigrationTests.cs`

`AppSettings` gains `PluginSources` (local folders only in phase 1) and moves to `CurrentSchemaVersion = 2`.

Migration from v1 is additive: an instance with `definitionId = null` and a `url` stays a legacy custom instance; an instance whose `definitionId` matches a known definition inherits that definition's `url`/regex when its own fields are empty. Migration writes only after a successful validation pass, and a v2 file opened by a v1 build still refuses to downgrade (existing behaviour).

**Tests:** v1 file loads and keeps its custom instance; migration does not invent an instance; duplicate ids still rejected; a v2 instance with a definition but no URL is valid and resolves later.

**Commit:** `feat(core): settings v2 distinguishes definitions from instances`.

---

## Task 3: Shipped definitions

**Files:**
- Create: `src/Chirpdesk/Definitions/whatsapp.json`, `messenger.json`, `telegram.json`, `facebook.json`, `instagram.json`
- Create: `src/Chirpdesk/Definitions/ShippedDefinitions.cs` (the loader entry point), `src/Chirpdesk.Core/Definitions/ServiceCatalog.cs`
- Test: `tests/Chirpdesk.Core.Tests/Definitions/ServiceCatalogTests.cs`

Embedded resources, not loose files, so a portable copy cannot lose them. The catalog resolves `instance + definition` into the runtime values the host needs (`url`, `name`, `unreadRegex`, `notificationMode`), with instance values winning.

Only WhatsApp's `notificationMode` is measured (`native`, phase 0). Omitted mode means `native`; `ServiceRuntime` still logs a capability probe (`permission=… serviceWorker=…`) so an unmeasured site can be recorded without claiming it was verified.

**Tests:** instance overrides definition; definition supplies the URL when the instance has none; unknown definition id yields a clear failure and no crash; a definition with a bad icon still resolves.

Shipped and loaded as five embedded JSON documents (`Definitions/*.json`, embedded so a portable copy cannot lose them). WhatsApp is `native`/`pageDarkMode: off` because phase 0 measured it. Telegram is `off` because its web client has its own `night` theme. Messenger and Facebook are **separate definitions** even though they share a vendor: `messenger` opens `messenger.com`, `facebook` opens `facebook.com`, and both support multiple accounts by creating two instances of the same definition. Messenger, Facebook and Instagram ship with `auto` dark mode and the default `native` notifications: **unmeasured**, and phase 1 must either probe each one or record them as assumed, not claim verification.

**Commit:** `feat(app): ship WhatsApp, Messenger, Telegram, Facebook and Instagram definitions`.

---

## Task 4: Service registry — warm, cold, and switching

**Files:**
- Create: `src/Chirpdesk/Hosting/ServiceRegistry.cs`
- Modify: `src/Chirpdesk/Hosting/ServiceRuntime.cs`, `HostWindow.xaml.cs`
- Test: `tests/Chirpdesk.Core.Tests/Ordering/RegistryOrderTests.cs` for the pure ordering parts

Registry owns every instance: one `ServiceRuntime` per **warm** instance, `MruList` for cycle order, and the active id. Switching means `Park()` the old runtime and `Activate()` the new one, without disposing either. `HostWindow.SetServiceContent` already swaps the visual.

Cold instances have no runtime and no profile lock; they appear in search and start on selection through the same `ServiceRuntime.CreateAsync`.

**Hard rule:** an environment is created per profile and reused across instances that share `profileId` (ADR 0005). Creating a second environment over a live folder is invalid; the registry is the only place that decides.

**Tests:** MRU updates on activation; two instances sharing a profile reuse one environment object; removing an instance disposes its runtime exactly once.

**Commit:** `feat(app): registry for warm and cold services`.

---

## Task 4b: Definition plugin loader

**Status:** complete (2026-11-17). Bundled definitions moved to `plugins/<id>/`; `PluginLoader` scans bundled then user roots, fills `id`/`name` from the manifest, validates permissions (refusing `inject-js`, `process`, and bundled-only permissions for user plugins), and shadows by id.

**Why now:** ADR 0011 removes the URL add flow. Installing a `service` plugin is the only way to introduce a site, so this loader is what makes the app extensible instead of fixed to the embedded definitions.

**Files:**
- Create: `src/Chirpdesk.Core/Plugins/PluginManifest.cs`, `PluginLoader.cs`
- Modify: `src/Chirpdesk/Definitions/ShippedDefinitions.cs` (or replace it with the loader), `App.xaml.cs`
- Test: `tests/Chirpdesk.Core.Tests/Plugins/PluginLoaderTests.cs`

Scan `<install>\plugins\` and `%APPDATA%\Chirpdesk\plugins\<id>\`; each folder needs `plugin.json` and, for `kind: service`, `service.json`. Validate the manifest (`id` pattern, `kind`, `version`, `apiVersion` = 1, permissions known), validate the definition through `DefinitionLoader`, and feed the result into `ServiceCatalog`. A user plugin shadows a bundled plugin of the same id; the manager reports it. `FileSystemWatcher` rescan can wait for Task 9 or phase 5 as long as startup scanning is correct.

Every failure is per-plugin and logged: missing payload, invalid JSON, id mismatch between manifest and definition, traversal in `icon`, duplicate ids, `apiVersion` too high. The app still starts with whatever loaded, and existing instances stay resolvable.

**Tests:** a valid service plugin loads with manifest values applied; an invalid manifest is skipped and one error is reported; id mismatch is rejected; an `icon` containing `..` or a separator is rejected; duplicate ids keep the bundled one unless the user plugin shadows it; unknown `kind` is skipped; a missing `service.json` is skipped; `apiVersion: 2` is disabled with a clear reason.

**Commit:** `feat(core): load service definitions from plugin folders`.

---

## Task 5: Command Bar — cycle mode

**Status:** complete (2026-11-17). The bar is an overlay inside `HostWindow` (airspace, ADR 0002); `BarState` in Core owns cycle/latch/release/cancel; `ModifierWatcher` polls Ctrl at 30 ms; opening collapses the active view and closing restores it. Search ranking is Task 6; scroll/focus survival is verified in Task 7.

**Files:**
- Create: `src/Chirpdesk/CommandBar/CommandBarWindow.xaml(.cs)`, `BarState.cs`, `ModifierWatcher.cs`
- Test: `tests/Chirpdesk.Core.Tests/CommandBar/BarStateTests.cs`

Size cap 560 × 420, anchored 12 % from the top, centred (§12 budget). Cycle mode is a list of warm services in MRU order, highlight moved by `Tab`/`Shift+Tab`, committed on `Ctrl` release. `ModifierWatcher` polls `GetAsyncKeyState` at 30 ms in one class (ADR 0002) and raises "released".

**Tests:** highlight wraps; a printable key latches to search with the highlight retained; releasing `Ctrl` after latch does not commit; `Esc` cancels without switching; empty list is a no-op, not a crash.

**Commit:** `feat(app): command bar cycle mode`.

---

## Task 6: Search mode and ranking

**Files:**
- Create: `src/Chirpdesk/CommandBar/SearchIndex.cs`
- Test: `tests/Chirpdesk.Core.Tests/CommandBar/SearchIndexTests.cs`

Order: warm services, then cold services, then commands. Matching: prefix beats substring, case-insensitive, and a definition's aliases match (`wa` → WhatsApp). Typing `tel` must put Telegram first even when a cold service named "Telethon" also exists.

**Tests:** prefix outranks substring; warm outranks cold at equal score; a legacy custom instance matches by name and host; empty query shows warm services in MRU order; no match shows nothing and `Enter` does nothing.

**Commit:** `feat(app): command bar search mode and ranking`.

---

## Task 7: Collapse the page under the bar

**Files:**
- Modify: `src/Chirpdesk/Hosting/ServiceRuntime.cs`, `HostWindow.xaml.cs`

WPF cannot paint over a WebView2 HWND (PLAN §16). While the bar is open, the active webview is collapsed (`Visibility.Collapsed`), and restored on close. Not disposed, so scroll position and page state survive.

**Verify by hand once:** open the bar over a scrolled chat, close it, the scroll position is unchanged and the page still has focus.

**Commit:** `feat(app): collapse the page while the command bar is open`.

---

## Task 8: Global hotkey and reserved keys

**Files:**
- Create: `src/Chirpdesk/Interop/HotKey.cs`
- Modify: `src/Chirpdesk/App.xaml.cs`, `CommandBarWindow.xaml.cs`
- Test: `tests/Chirpdesk.Core.Tests/CommandBar/BarKeyTests.cs`

`Ctrl+Alt+Space` via `RegisterHotKey` on the hidden window; `WM_HOTKEY` opens the bar in search mode. Registration failure is logged and surfaced (tray tooltip for now, Settings in phase 2), never silent.

Reserved while the bar is open: `Ctrl+1..9` activates by position in the visible list, `Ctrl+W` hides the window without closing the service, `Esc` closes the bar.

**Tests:** key handling is a pure state machine, so every combination above is unit-tested; the P/Invoke is one method.

**Commit:** `feat(app): global hotkey and reserved keys`.

---

## Task 9: Phase 1 gate

### 9.1 Automated — extend `--selftest`

| Check | Asserts |
|---|---|
| definitions | every bundled `plugins/<id>` pair validates; a deliberately malformed folder in a temp root is skipped, not fatal; a user plugin shadows the bundled id |
| catalog | instance overrides resolve; definition fallback resolves; unknown id is a clear failure |
| migration | v1 fixture migrates to v2 without inventing or losing an instance |
| registry | two warm services park/activate without disposing; shared profile reuses one environment; MRU order after three switches |
| bar state | cycle wrap, latch, cancel and reserved keys, all from the pure state machine |
| hotkey | registration succeeds, or the failure is reported (soft, because another app may own the combo) |

`scripts/gate.ps1` runs all of it.

### 9.2 Human — what automation cannot judge

- `Ctrl+Tab` cycles warm services and commits on release.
- Hold `Ctrl`, type `tel`, `Enter`: one continuous interaction, no second gesture.
- Half-typed search text survives a switch.
- Two instances of one definition keep separate sessions.
- The bar is usable at 100 % and 150 % scaling on the small screen, and never covers more than 560 × 420.

### 9.3 Checklist

- [ ] Two WhatsApp accounts signed in, separate sessions, separate unread counts.
- [ ] Cycle → search → commit without touching the mouse.
- [ ] Cold service starts on selection and lands on its page, not an empty window.
- [ ] Window never appears except for the user's own request.
- [ ] RAM with three warm services recorded in `docs/measurements.md`; ADR 0005's trigger re-evaluated with that number.
- [ ] Quit leaves no orphan `msedgewebview2.exe`.

**Commit:** `test: phase 1 results`.

---

## Deliberately out of scope

Themes are validated but not applied (phase 4), storage UI and trimming (phase 2), blocking (phase 3), plugin repositories (phase 5). If any of those is needed to finish a task here, the task is wrong, not the phase order.
