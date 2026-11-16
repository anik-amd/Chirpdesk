# Phase 0 — Spine Implementation Plan

**Goal:** prove the two risky things (hidden warm webview that still notifies, and a real Windows toast) with the smallest amount of code, behind a headless-tested core.

**Architecture:** three projects — `Chirpdesk.Core` (pure logic, no WPF, fully unit-testable), `Chirpdesk` (WPF + WebView2 shell, thin), `Chirpdesk.Core.Tests` (xUnit). Phase 0 builds only the seams that later phases depend on, plus one real service (WhatsApp).

**Tech Stack:** .NET 8, WPF, WebView2 (`Microsoft.Web.WebView2`), WinRT toasts via `net8.0-windows10.0.19041.0`, xUnit, `Hardcodet.NotifyIcon.Wpf` (tray, added in Task 8).

**Read first:** `PLAN.md` §5.1, §6, §11, §15 phase 0. Invariants live in `AGENTS.md`.

---

## Task 1: Solution skeleton

**Files:**
- Exists: `global.json`, `Directory.Build.props`, `.editorconfig` (verify, don't recreate)
- Create: `Chirpdesk.sln`
- Create: `src/Chirpdesk.Core/Chirpdesk.Core.csproj`
- Create: `src/Chirpdesk/Chirpdesk.csproj`
- Create: `tests/Chirpdesk.Core.Tests/Chirpdesk.Core.Tests.csproj`

**Step 1: Pin the SDK**

`global.json`:

```json
{ "sdk": { "version": "8.0.100", "rollForward": "latestFeature" } }
```

**Step 2: Create projects**

```powershell
dotnet new sln -n Chirpdesk
dotnet new classlib -o src/Chirpdesk.Core -f net8.0
dotnet new wpf      -o src/Chirpdesk      -f net8.0
dotnet new xunit    -o tests/Chirpdesk.Core.Tests -f net8.0
dotnet sln add src/Chirpdesk.Core src/Chirpdesk tests/Chirpdesk.Core.Tests
dotnet add src/Chirpdesk reference src/Chirpdesk.Core
dotnet add tests/Chirpdesk.Core.Tests reference src/Chirpdesk.Core
```

**Step 3: WPF target framework**

In `src/Chirpdesk/Chirpdesk.csproj` replace the TFM with:

```xml
<TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
<UseWPF>true</UseWPF>
<UseWindowsForms>false</UseWindowsForms>
```

**Step 4: Build**

Run: `dotnet build`
Expected: `Build succeeded` with 0 warnings.

**Step 5: Commit**

```powershell
git add -A; git commit -m "chore: scaffold solution, core, app and tests"
```

---

## Task 2: Unread count parsing (TDD)

**Files:**
- Create: `src/Chirpdesk.Core/Unread/UnreadParser.cs`
- Test: `tests/Chirpdesk.Core.Tests/Unread/UnreadParserTests.cs`

**Step 1: Write the failing tests**

```csharp
public class UnreadParserTests
{
    [Theory]
    [InlineData("(3) WhatsApp", @"\((\d+)\)", 3)]
    [InlineData("WhatsApp (12)", @"\((\d+)\)", 12)]
    [InlineData("WhatsApp", @"\((\d+)\)", 0)]
    [InlineData("(1) Telegram (9)", @"\((\d+)\)", 1)]   // first match wins
    [InlineData("99+ unread", @"(\d+)\+", 99)]
    [InlineData(null, @"\((\d+)\)", 0)]
    [InlineData("(abc) x", @"\((\d+)\)", 0)]
    public void Parses_first_capture_group(string? title, string pattern, int expected)
        => Assert.Equal(expected, UnreadParser.Parse(title, pattern));
}
```

**Step 2: Run to confirm failure**

Run: `dotnet test tests/Chirpdesk.Core.Tests --filter UnreadParserTests`
Expected: FAIL — `UnreadParser` does not exist.

**Step 3: Implement**

```csharp
namespace Chirpdesk.Core.Unread;

public static class UnreadParser
{
    public static int Parse(string? title, string pattern)
    {
        if (string.IsNullOrEmpty(title) || string.IsNullOrWhiteSpace(pattern)) return 0;
        var match = System.Text.RegularExpressions.Regex.Match(title, pattern);
        if (!match.Success || match.Groups.Count < 2) return 0;
        return int.TryParse(match.Groups[1].Value, out var n) && n > 0 ? n : 0;
    }
}
```

**Step 4: Run to confirm pass**

Run: `dotnet test tests/Chirpdesk.Core.Tests --filter UnreadParserTests`
Expected: 7 passed.

**Step 5: Commit**

```powershell
git add src/Chirpdesk.Core/Unread tests/Chirpdesk.Core.Tests/Unread
git commit -m "feat(core): parse unread counts from window titles"
```

---

## Task 3: Clock seam + notification dedupe (TDD)

**Files:**
- Create: `src/Chirpdesk.Core/Time/IClock.cs`, `src/Chirpdesk.Core/Time/SystemClock.cs`
- Create: `src/Chirpdesk.Core/Notifications/NotificationDedupe.cs`
- Test: `tests/Chirpdesk.Core.Tests/Notifications/NotificationDedupeTests.cs`, `.../Time/FakeClock.cs`

**Step 1: Failing tests**

```csharp
[Fact]
public void Drops_same_notification_within_window()
{
    var clock = new FakeClock();
    var dedupe = new NotificationDedupe(clock, TimeSpan.FromSeconds(3));
    Assert.True(dedupe.ShouldShow("chat", "Alice", "hello"));
    clock.Advance(TimeSpan.FromSeconds(2));
    Assert.False(dedupe.ShouldShow("chat", "Alice", "hello"));
}

[Fact]
public void Allows_same_notification_after_window()
{
    var clock = new FakeClock();
    var dedupe = new NotificationDedupe(clock, TimeSpan.FromSeconds(3));
    dedupe.ShouldShow("chat", "Alice", "hello");
    clock.Advance(TimeSpan.FromSeconds(4));
    Assert.True(dedupe.ShouldShow("chat", "Alice", "hello"));
}

[Fact]
public void Different_body_is_not_a_duplicate()
{
    var clock = new FakeClock();
    var dedupe = new NotificationDedupe(clock, TimeSpan.FromSeconds(3));
    dedupe.ShouldShow("chat", "Alice", "hello");
    Assert.True(dedupe.ShouldShow("chat", "Alice", "hello there"));
}

[Fact]
public void Same_body_from_a_different_sender_is_not_a_duplicate()
{
    var clock = new FakeClock();
    var dedupe = new NotificationDedupe(clock, TimeSpan.FromSeconds(3));
    dedupe.ShouldShow("chat", "Alice", "hello");
    Assert.True(dedupe.ShouldShow("chat", "Bob", "hello"));
}
```

**Step 2:** Run → FAIL (types missing).

**Step 3: Implement** — one `NotificationDedupe` per service instance; key is `SHA256($"{tag}\u001f{title}\u001f{body}")` hex, stored with `IClock.UtcNow`; prune expired entries on each call; never keep message bodies in memory beyond the hash (privacy invariant, see `AGENTS.md`). The sender/title is part of the key so two people sending "ok" at once are not collapsed.

**Step 4:** Run → PASS.

**Step 5:** Commit `feat(core): dedupe duplicate web notifications`.

---

## Task 4: Warm-up gate (TDD)

**Files:**
- Create: `src/Chirpdesk.Core/Notifications/WarmUpGate.cs`
- Test: `tests/Chirpdesk.Core.Tests/Notifications/WarmUpGateTests.cs`

Rationale: R4 — no toast storm at login. A service must be `ready` and its title count stable for 5 s before its notifications are allowed through.

**Step 1: Failing tests** — three cases: blocked before ready; blocked while title still changing; allowed after 5 s of stability and then survives later title changes.

**Step 3: Implement** — `MarkReady()` plus `ObserveTitle(count)`; `ShouldSuppress()` true until `now - lastTitleChange >= 5s && ready`.

**Step 4/5:** Run → PASS → commit `feat(core): suppress boot-time notification backlog`.

---

## Task 5: Settings store with atomic writes (TDD)

**Files:**
- Create: `src/Chirpdesk.Core/Settings/Settings.cs`, `.../SettingsStore.cs`, `.../ISettingsStore.cs`
- Test: `tests/Chirpdesk.Core.Tests/Settings/SettingsStoreTests.cs`

**Tests:** round-trip of an instance (`instanceId`, `definitionId: null`, `url`); missing file returns defaults; corrupt JSON returns defaults and writes a `.corrupt` copy; interrupted write leaves the previous file intact (simulate by writing to `.tmp` then asserting nothing replaced the original); invalid instance id (`..`, uppercase, >32 chars) is rejected; two instances with the same `instanceId` are rejected.

**Implementation notes:** `File.WriteAllText(tmp)` → `File.Replace(tmp, target, backup)`; `JsonSerializerOptions { WriteIndented = true }`; every write under a `lock`.

**Commit:** `feat(core): persist settings atomically with corrupt-file fallback`.

---

## Task 6: MRU order (TDD)

**Files:**
- Create: `src/Chirpdesk.Core/Ordering/MruList.cs`
- Test: `tests/Chirpdesk.Core.Tests/Ordering/MruListTests.cs`

**Tests:** activation moves an id to the front; duplicates collapse; unknown id inserted at front; `Cycle(from, direction)` wraps both ways; empty list returns null.

**Commit:** `feat(core): MRU ordering for command bar cycling`.

---

## Task 7: Hidden host window + single instance

**Files:**
- Create: `src/Chirpdesk/App.xaml`, `App.xaml.cs`, `Hosting/HostWindow.xaml`, `Hosting/HostWindow.xaml.cs`, `Hosting/SingleInstance.cs`
- Modify: `src/Chirpdesk/Chirpdesk.csproj` (startup object, app icon placeholder)

**Steps (manual verification, no unit test — the seam is `IClock`-style only where logic exists):**

1. `HostWindow`: `WindowStyle=None`, `ShowInTaskbar=true`, `ShowActivated=false`, `Width=2`, `Height=2`, `Left=-32000`, `Top=-32000`; in `SourceInitialized` add `WS_EX_TOOLWINDOW` via `SetWindowLongPtr`. Do **not** set `ShowInTaskbar=false` and do not change it later: WPF recreates the HWND when that property changes, which would destroy the webviews.
2. `App.OnStartup`: named `Mutex("Local\\Chirpdesk.Single")`; if already held, signal the existing instance through a named pipe and `Shutdown()`.
3. Run, then check: `Alt+Tab` shows nothing, taskbar shows nothing, Task Manager shows `Chirpdesk.exe`.
4. Launch twice → second process exits within 500 ms, first window is not duplicated.

**Commit:** `feat(app): invisible single-instance host window`.

---

## Task 8: Tray icon + exit path

**Files:**
- Create: `src/Chirpdesk/Hosting/TrayIcon.cs`
- Modify: `src/Chirpdesk/Chirpdesk.csproj` (package reference)

```powershell
dotnet add src/Chirpdesk package Hardcodet.NotifyIcon.Wpf --version 2.0.1
```

**Steps:** tray icon with menu `Quit` (add `Open` in phase 1, when a show path exists); unread dot variant rendered at runtime to a 16×16 `RenderTargetBitmap`; `Quit` disposes the mutex and exits. Verify the icon survives an Explorer restart (`TaskbarCreated` message) — that is the acceptance check.

**Commit:** `feat(app): tray icon with quit path`.

---

## Task 9: WebView2 environment with anti-throttle flags + per-profile data folder

**Files:**
- Create: `src/Chirpdesk/Hosting/WebViewHost.cs`, `.../Hosting/ServiceRuntime.cs`
- Create: `src/Chirpdesk/Hosting/HiddenHost.cs` (the warm-window container)

**Steps:**

1. Add the pinned binding **before writing host code**:

```powershell
dotnet add src/Chirpdesk package Microsoft.Web.WebView2 --version 1.0.4314.35
```

2. Build `CoreWebView2Environment` with `userDataFolder = %LOCALAPPDATA%\Chirpdesk\profiles\whatsapp` and

```
--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows
```

3. Create one `WebView2` inside `HostWindow`, `Source=https://web.whatsapp.com/`, size 1×1 while parked.
4. Add the 30 s keepalive tick: `Controller.IsVisible = true` re-assert + `TrySuspendAsync(false)`.
5. Register the notification capability probe now (used by Task 10): subscribe `PermissionRequested` and auto-grant `Notifications` for `https://web.whatsapp.com` (the full per-origin policy lands in phase 2), then subscribe `NotificationReceived`, fire a synthetic `new Notification("probe")` through `ExecuteScriptAsync`, and record whether the event fires. Log the result to `docs/measurements.md` as `native` / `title-only`.
6. **The deliberate phase-0 visibility exceptions:** show the window once so the user can scan the WhatsApp QR code (using the `WS_EX_TOOLWINDOW` → `WS_EX_APPWINDOW` toggle from Task 7), then hide it; Task 10 reuses the same minimal show path for toast clicks. No Command Bar, hotkey or tray "Open" yet — those are phase 1.
7. Manual check: log in via QR, leave tray-hidden; after 20 min, check `document.readyState` and socket state through `ExecuteScriptAsync` (debug command).

**Commit:** `feat(app): warm webview host with background throttling disabled`.

---

## Task 10: Toast pipeline + AUMID + debug trigger

**Files:**
- Create: `src/Chirpdesk/Notifications/Aumid.cs`, `.../ToastService.cs`, `.../NotificationBridge.cs`
- Modify: `src/Chirpdesk/App.xaml.cs` (`--test-toast` argument)

**Steps:**

1. `Aumid.EnsureShortcut()` creates `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Chirpdesk.lnk` with `System.AppUserModel.ID = Chirpdesk`; call `SetCurrentProcessExplicitAppUserModelID("Chirpdesk")` at startup.
2. `ToastService.Show(service, title, body, iconPath)` via `ToastNotificationManager.CreateToastNotifier("Chirpdesk")`; click handler shows the window on the owning service using the minimal show path from Task 9 (`OnActivated`).
3. `NotificationBridge` subscribes `CoreWebView2.NotificationReceived`: verify `SenderOrigin` belongs to the service; set `Handled = true`; drop anything older than process start (boot cutoff); run `WarmUpGate` + `NotificationDedupe`; then `Show`, then `ReportShown`/`ReportClicked`/`ReportClosed`.
4. Record the service's notification mode (`native` when the event fires, `title-only` when only `DocumentTitleChanged` is available) in `docs/measurements.md`; phase 0 does not build the shim yet.
5. Verify: `Chirpdesk.exe --test-toast` produces a toast titled with the app name, not "(unknown app)".

**Commit:** `feat(app): native toasts from web notifications`.

---

## Task 11: Phase 0 soak gate

**Checklist — all must pass, in one sitting:**

- [ ] Register Chirpdesk in `HKCU\...\Run` with a 20 s delay (temporary), reboot, no window appears.
- [ ] **Sign in once** with the window shown (the documented phase-0 exception), then hide it. WhatsApp logged in, tray icon present, zero taskbar/Alt+Tab artifacts afterwards.
- [ ] Notification capability for WhatsApp recorded as `native` (event fired) or `title-only`; if only `title-only` works, that is the mode written into the phase-0 record.
- [ ] Message from a second phone toasts within 1 s, with sender + text, correct icon, and the toast is titled "Chirpdesk".
- [ ] 3 hours tray-hidden: a second message still toasts (socket alive), and there is no toast backlog on resume.
- [ ] Clicking the toast shows the app on WhatsApp (no deep link required while resident).
- [ ] The window was never visible except for the deliberate sign-in and toast clicks.
- [ ] Task Manager: no runaway CPU; RAM recorded for the warm WhatsApp instance (write it into `docs/measurements.md`).
- [ ] Quit from tray leaves no orphan `msedgewebview2.exe` process tree.

If any item fails, do **not** proceed to phase 1. Record the failure mode in `docs/adr/0003-hidden-warm-host.md` as a consequence and apply the documented fallback (1×1 `WS_EX_NOACTIVATE` window at the screen edge, then minimised to taskbar).

**Commit:** `test: phase 0 soak results` with the measurements file.
