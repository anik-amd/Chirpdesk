# Phase 1 — Command Bar, definitions, multi-service

**Status:** Tasks 1–3 complete (2026-11-17) and Task 4 started: one WebView2 environment per profile is now cached process-wide, which is the shared-profile requirement the registry will build on. Next in Task 4: the registry itself (warm/cold, active id, park/activate) and wiring `ShowHostFor` to it.

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

Migration from v1 is additive: an instance with `definitionId = null` and a `url` stays a custom site; an instance whose `definitionId` matches a known definition inherits that definition's `url`/regex when its own fields are empty. Migration writes only after a successful validation pass, and a v2 file opened by a v1 build still refuses to downgrade (existing behaviour).

**Tests:** v1 file loads and keeps its custom instance; migration does not invent an instance; duplicate ids still rejected; a v2 instance with a definition but no URL is valid and resolves later.

**Commit:** `feat(core): settings v2 distinguishes definitions from instances`.

---

## Task 3: Shipped definitions

**Files:**
- Create: `src/Chirpdesk/Definitions/whatsapp.json`, `messenger.json`, `telegram.json`, `instagram.json`
- Create: `src/Chirpdesk/Definitions/DefinitionCatalog.cs`
- Test: `tests/Chirpdesk.Core.Tests/Definitions/CatalogTests.cs` (with the JSON loaded as embedded resources from the app project via a linked copy, or a Core-side fixture)

Embedded resources, not loose files, so a portable copy cannot lose them. The catalog resolves `instance + definition` into the runtime values the host needs (`url`, `name`, `unreadRegex`, `notificationMode`), with instance values winning.

Only WhatsApp's `notificationMode` is measured (`native`, phase 0). The others ship as `null` meaning "probe at first run and record", which is already what `ServiceRuntime` does.

**Tests:** instance overrides definition; definition supplies the URL when the instance has none; unknown definition id yields a clear failure and no crash; a definition with a bad icon still resolves.

Shipped and loaded as four embedded JSON documents (`Definitions/*.json`, embedded so a portable copy cannot lose them). WhatsApp is `native`/`pageDarkMode: off` because phase 0 measured it. Messenger, Telegram and Instagram ship with the default `native` and `auto`: **unmeasured**, and phase 1 must either probe each one or record them as assumed, not claim verification.

**Commit:** `feat(app): ship WhatsApp, Messenger, Telegram and Instagram definitions`.

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

## Task 5: Command Bar — cycle mode

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

**Tests:** prefix outranks substring; warm outranks cold at equal score; custom site matches by name and host; empty query shows warm services in MRU order; no match shows nothing and `Enter` does nothing.

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
| definitions | every shipped definition validates; a deliberately malformed one in a temp folder is skipped, not fatal |
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
