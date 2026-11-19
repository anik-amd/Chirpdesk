# Chirpdesk

**Your chats, out of the way.**

Chirpdesk is a Windows tray app that keeps your web messengers running in the background and turns
their notifications into real Windows toasts. One window, one tray icon, `Ctrl+K` when you need it.

It is not an Electron app and does not bundle a browser. It uses the WebView2 runtime that ships
with Windows and keeps each account in its own profile.

## Install

Download the latest **Chirpdesk-Setup-x.y.z.exe** from
[Releases](https://github.com/anik-amd/Chirpdesk/releases). The installer is per-user and does not
need admin rights.

A portable **Chirpdesk-x.y.z-portable.zip** is published with every release. Portable builds never
self-update; replace the folder to upgrade.

Requirements:

- Windows 10 1809 or later, 64-bit
- .NET 8 Desktop Runtime
- Microsoft Edge WebView2 Runtime (preinstalled on Windows 11)

The installer checks both and points you at the official download if one is missing.

## First run

1. Start Chirpdesk. It opens once to teach the tray, the palette and the plugin page.
2. Open **Plugins & services** and open WhatsApp, Telegram, Messenger or Facebook.
3. Scan the QR code or sign in in the window that opens. Close the window when you are done.

After that Chirpdesk lives in the tray. Closing the window keeps chats open in the background. Use **Quit** from the
tray menu to stop it.

## Everyday use

| Action | How |
|---|---|
| Show the window | Tray icon, or launch Chirpdesk again |
| Open the palette | `Ctrl+K` |
| List open tabs | `Ctrl+Shift+K` |
| Switch chats | `Ctrl+Tab` while the palette is open |
| Close a tab | `Ctrl+W` |
| Back / close a page | `Esc` |
| Second account | Plugins & services → Add account |
| Blocking for one tab | `Ctrl+K` → “Turn blocking off for this tab” |

The palette lists open chats and closed service plugins. Type to search tabs, plugins and
commands.

## What it does

- **Ready in the tray.** Services stay loaded so their WebSockets keep receiving messages. Pinned
  services open after login without showing a window.
- **Windows toasts.** Real Action Center notifications with the service icon, dedupe and a short
  warm-up gate so a reconnect cannot replay a backlog.
- **A minimal service sidebar.** Open chats sit on the left edge with a 1/2 badge when one plugin has
  two accounts. The **+** button opens the palette to open another chat; palette, plugins and
  settings are one click from the same strip. It can be always on, edge-hover or hidden in Settings.
- **Accounts stay separate.** Every account gets its own WebView2 profile and session. Two WhatsApp
  accounts can be signed in side by side.
- **Ad and tracker blocking.** Conservative request blocking plus cosmetic hiding, always fail-open,
  with a permanent allowlist for login, WebSocket and update traffic. Toggle it per tab.
- **Storage that explains itself.** Data is the session and is never trimmed. Cache is trimmed only
  after a profile sleeps. A leftover scan finds old profiles and sends them to the Recycle Bin.
- **Light and dark.** Catppuccin Latte for light, and Frappé, Macchiato or Mocha for dark, chosen
  separately. The mode follows Windows or stays where you put it. Page darkening is its own plugin.
- **Plugins.** Service definitions, filter packs, themes and tools share one manifest. Bundled
  plugins work out of the box; the signed plugin repository is not part of v0.1.

## Privacy

- No account, no telemetry and no Chirpdesk server.
- Chats run in WebView2 profiles under `%LOCALAPPDATA%\Chirpdesk\profiles`.
- Settings and logs are under `%APPDATA%\Chirpdesk`; logs never contain message bodies, contact
  names or full URLs with query strings.
- Blocking happens locally. No browsing data leaves the machine.

## Portable build

Unzip and run `Chirpdesk.exe`. The same data folders are used, so you can move between the installer
and the portable build without signing in again. Portable builds do not self-update.

## Build from source

```powershell
dotnet build
dotnet test --filter Category!=Integration
dotnet run --project src/Chirpdesk
```

Build the release artifacts:

```powershell
pwsh scripts/pack-release.ps1 -Version 0.1.0
```

This needs the .NET 8 SDK and Inno Setup 6. It writes the installer, the portable zip and the publish
folder to `dist/`.

## Known limits

- Sites that refuse embedded sign-in (some Google and Cloudflare flows) show an honest message and
  an **open in browser** action. Cookies cannot be moved between the browser and the webview.
- There is no global hotkey in v0.3. The tray icon, a second launch and the Command Bar are the
  entry points.
- Facebook and Messenger notify from a service worker, which WebView2 does not surface with a
  sender or body. They fall back to an unread-count toast; their page lists expose no accessible
  unread or preview hooks to read instead. WhatsApp has the same service-worker path, but its chat
  list is readable, so its toasts carry the message preview.
- Portable builds do not self-update.

## Docs

- [docs/manual-verification.md](docs/manual-verification.md) - what to test by hand
- [docs/plugins.md](docs/plugins.md) - plugin manifest and filter format
- [docs/updates.md](docs/updates.md) - release manifest and signing
- [docs/adr/](docs/adr/) - why the architecture looks like this
- [PLAN.md](PLAN.md) - the full product and engineering plan

## License

MIT. See [LICENSE](LICENSE).


## Security

Updates and plugin repositories are signed. The private signing keys stay offline; only the
pinned public keys ship in the app. Report vulnerabilities through GitHub private vulnerability
reporting as described in [SECURITY.md](SECURITY.md), not in a public issue.
