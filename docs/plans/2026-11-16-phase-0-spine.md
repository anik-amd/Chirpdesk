# Phase 0 — Spine Implementation Plan

**Goal:** prove the two risky things (hidden warm webview that still notifies, and a real Windows toast) with the smallest amount of code, behind a headless-tested core.

**Architecture:** three projects — `Chirpdesk.Core` (pure logic, no WPF, fully unit-testable), `Chirpdesk` (WPF + WebView2 shell, thin), `Chirpdesk.Core.Tests` (xUnit). Phase 0 builds only the seams that later phases depend on, plus one real service (WhatsApp).

**Tech Stack:** .NET 8, WPF, WebView2 (`Microsoft.Web.WebView2`), WinRT toasts via `net8.0-windows10.0.19041.0`, xUnit, `Hardcodet.NotifyIcon.Wpf` (tray, added in Task 8).

**Read first:** `PLAN.md` §5.1, §6, §14 phase 0. Invariants live in `AGENTS.md`.

---

## Task 1: Solution skeleton

**Files:**
- Create: `global.json`, `Directory.Build.props`, `.editorconfig`
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
    Assert.True(dedupe.ShouldShow("chat", "hello"));
    clock.Advance(TimeSpan.FromSeconds(2));
    Assert.False(dedupe.ShouldShow("chat", "hello"));
}

[Fact]
public void Allows_same_notification_after_window()
{
    var clock = new FakeClock();
    var dedupe = new NotificationDedupe(clock, TimeSpan.FromSeconds(3));
    dedupe.ShouldShow("chat", "hello");
    clock.Advance(TimeSpan.FromSeconds(4));
    Assert.True(dedupe.ShouldShow("chat", "hello"));
}

[Fact]
public void Different_body_is_not_a_duplicate()
{
    var clock = new FakeClock();
    var dedupe = new NotificationDedupe(clock, TimeSpan.FromSeconds(3));
    dedupe.ShouldShow("chat", "hello");
    Assert.True(dedupe.ShouldShow("chat", "hello there"));
}
```

**Step 2:** Run → FAIL (types missing).

**Step 3: Implement** — key is `SHA1($"{tag}\u001f{body}")` hex, stored with `IClock.UtcNow`; prune expired entries on each call; never keep message bodies in memory beyond the hash (privacy invariant, see `AGENTS.md`).

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

**Tests:** round-trip; missing file returns defaults; corrupt JSON returns defaults and writes a `.corrupt` copy; interrupted write leaves the previous file intact (simulate by writing to `.tmp` then asserting nothing replaced the original); path traversal in service id is rejected.

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

1. `HostWindow`: `WindowStyle=None`, `ShowInTaskbar=false`, `ShowActivated=false`, `Width=2`, `Height=2`, `Left=-32000`, `Top=-32000`; in `SourceInitialized` add `WS_EX_TOOLWINDOW` via `SetWindowLong`.
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

**Steps:** tray icon with menu `Open · Quit`; unread dot variant rendered at runtime to a 16×16 `RenderTargetBitmap`; `Quit` disposes the mutex and exits. Verify the icon survives an Explorer restart (`TaskbarCreated` message) — that is the acceptance check.

**Commit:** `feat(app): tray icon with quit path`.

---

## Task 9: WebView2 environment with anti-throttle flags + per-service profile

**Files:**
- Create: `src/Chirpdesk/Hosting/WebViewHost.cs`, `.../Hosting/ServiceRuntime.cs`
- Create: `src/Chirpdesk/Hosting/HiddenHost.cs` (the warm-window container)

**Steps:**

1. Build `CoreWebView2Environment` with `userDataFolder = %LOCALAPPDATA%\Chirpdesk\profiles\whatsapp` and

```
--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows
```

2. Create one `WebView2` inside `HostWindow`, `Source=https://web.whatsapp.com/`, size 1×1 while parked.
3. Add the 30 s keepalive tick: `Controller.IsVisible = true` re-assert + `TrySuspend(false)`.
4. Manual check: log in via QR, leave tray-hidden; after 20 min, check `document.readyState` and socket state through `ExecuteScriptAsync` (debug command).

**Commit:** `feat(app): warm webview host with background throttling disabled`.

---

## Task 10: Toast pipeline + AUMID + debug trigger

**Files:**
- Create: `src/Chirpdesk/Notifications/Aumid.cs`, `.../ToastService.cs`, `.../NotificationBridge.cs`
- Modify: `src/Chirpdesk/App.xaml.cs` (`--test-toast` argument)

**Steps:**

1. `Aumid.EnsureShortcut()` creates `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Chirpdesk.lnk` with `System.AppUserModel.ID = Chirpdesk`; call `SetCurrentProcessExplicitAppUserModelID("Chirpdesk")` at startup.
2. `ToastService.Show(service, title, body, iconPath)` via `ToastNotificationManager.CreateToastNotifier("Chirpdesk")`; click handler focuses the owning service (`OnActivated`).
3. `NotificationBridge` subscribes `CoreWebView2.NotificationReceived`: set `Handled = true`, run `WarmUpGate` + `NotificationDedupe`, then `Show`, then `ReportShown`.
4. Verify: `Chirpdesk.exe --test-toast` produces a toast titled with the app name, not "(unknown app)".

**Commit:** `feat(app): native toasts from web notifications`.

---

## Task 11: Phase 0 soak gate

**Checklist — all must pass, in one sitting:**

- [ ] Reboot with Chirpdesk in `HKCU\...\Run` (temporary), 20 s delay, no window appears.
- [ ] WhatsApp logged in, tray icon present, zero taskbar/Alt+Tab artifacts.
- [ ] Message from a second phone toasts within 1 s, with sender + text, correct icon.
- [ ] 3 hours tray-hidden: second message still toasts (socket alive).
- [ ] `Ctrl+W`-style hide not yet implemented — window must never have been visible at all.
- [ ] Task Manager: no runaway CPU; RAM recorded for the warm WhatsApp service (write it into `docs/measurements.md`).
- [ ] Quit from tray leaves no orphan `msedgewebview2.exe` process tree.

If any item fails, do **not** proceed to phase 1. Record the failure mode in `docs/adr/0003-hidden-warm-host.md` as a consequence and apply the documented fallback.

**Commit:** `test: phase 0 soak results` with the measurements file.
