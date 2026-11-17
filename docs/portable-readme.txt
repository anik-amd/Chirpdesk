Chirpdesk portable
==================

Chirpdesk keeps web chat clients warm in the Windows tray and turns their notifications into
Windows toasts.

Requirements
------------
- Windows 10 1809 or later, 64-bit.
- .NET 8 Desktop Runtime:
  https://dotnet.microsoft.com/download/dotnet/8.0
- Microsoft Edge WebView2 Evergreen Runtime:
  https://go.microsoft.com/fwlink/p/?LinkId=2124703

Run
---
Start Chirpdesk.exe. The window stays hidden; use the tray icon or press Ctrl+P after the window
is shown. On first run the onboarding page explains the tray, the palette and the plugin page.

Portable builds never self-update. To upgrade, download a newer zip and replace the files; your
settings and chat profiles live outside this folder and are kept.

Data
----
%APPDATA%\Chirpdesk          settings and logs
%LOCALAPPDATA%\Chirpdesk     WebView2 profiles

Uninstalling is just deleting this folder. Delete the two data folders as well if you want a
clean start; chat profiles are not backed up automatically.
