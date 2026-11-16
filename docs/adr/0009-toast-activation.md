# ADR 0009 — Toast activation goes through a per-user COM class

**Status:** accepted (2026-11-17)

## Context

A toast that is still on screen can be handled in-process: `ToastNotification.Activated` fires on the object that created it, which is why the first click tests worked. The normal case is different — toasts leave the screen after a few seconds and are then clicked from the Action Center. Those activations go through Windows' notification activation path, and an unpackaged app that has not registered a COM activator gets **nothing**: no event, no launch, no error. Observed directly: the click produced no log line at all, and a later attempt in which only the launched `-ToastActivated` process registered the class object still produced no callback.

## Decision

Register a per-user COM local server for a fixed CLSID and point the Start-menu shortcut's `System.AppUserModel.ToastActivatorCLSID` at it.

- Registry (HKCU, no elevation): `Software\Classes\CLSID\{6F1D3A3E-6B0F-4A7A-9A9E-2F0B7C5D4E21}\LocalServer32 = "<exe>" -ToastActivated`.
- The shortcut carries the same CLSID in the `ToastActivatorCLSID` property; without it Action Center clicks are dropped before COM is consulted.
- The **resident** process calls `CoRegisterClassObject` for the CLSID at startup. This is the part that makes a click reach the running app instead of starting another one.
- The class factory uses raw `IntPtr` signatures and `Marshal.GetComInterfaceForObject`, the shape the Community Toolkit uses for .NET Core; the managed `object` signature did not receive the call.
- When the app is not running, COM launches the exe with `-ToastActivated`/`-Embedding`; that process registers the same class object, waits one activation, signals the resident instance through `Local\Chirpdesk.Show`, and exits. It times out after 60 s so a failed activation cannot leave a stray process.

## Consequences

- Action Center clicks work while the app runs, and the signal path keeps working after a restart.
- The registry key and the shortcut both point at the current exe path, so a portable copy must re-register on launch (it already does, every start).
- Uninstall must delete `HKCU\Software\Classes\CLSID\{6F1D…}` and the shortcut; `ToastActivation.UnregisterServer` exists for the installer to call in phase 6.
- A click can arrive through both the COM callback and the foreground launch, producing two "show" calls. The window is idempotent, so this is cosmetic; a debounce is cheap if it ever shows.
- This does **not** give deep links into a conversation. The callback receives the toast's launch argument; turning that into a chat route needs data the notification does not necessarily carry (see ADR 0007 and PLAN §6 — a definition may supply a route template, an arbitrary site cannot).
