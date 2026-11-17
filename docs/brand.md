# Brand mark

The mark is a paper speech bubble on an ink tile, with one rounded green waveform (the chirp)
and an amber notification pin. It is drawn for the app's ink/paper palette rather than a generic
chat glyph.

## Assets

| File | Used for |
|---|---|
| `src/Chirpdesk/Assets/chirpdesk.svg` | source for the app tile |
| `src/Chirpdesk/Assets/chirpdesk.ico` | exe, taskbar and window icon (16-256 px) |
| `src/Chirpdesk/Assets/chirpdesk-tray.svg` | source for the transparent tray glyph |
| `src/Chirpdesk/Assets/chirpdesk-tray.ico` | tray, 16-32 px |
| `src/Chirpdesk/Assets/chirpdesk-tray-unread.svg` | tray with the red pin |
| `src/Chirpdesk/Assets/chirpdesk-tray-unread.ico` | tray unread state |
| `src/Chirpdesk/Assets/chirpdesk-512.png` | preview only, never embedded |

Palette: ink `#10171A`, paper `#F3F0E8`, chirp `#2F5A4C`, amber `#D8A45A`, unread `#E4574C`.

The tile carries the ink background because Windows uses it at every size. The tray glyph is
transparent and outlined in ink so it reads on both light and dark taskbars.

## Regenerating

Edit the SVG, then:

```powershell
pwsh scripts/make-icon.ps1
```

The script rasterises every size with ImageMagick/librsvg and rewrites the ICOs plus previews.
Commit the regenerated files. `--selftest` checks that the app icon has 16 and 256 px frames, the
window resolves its icon, and both tray variants load at 32 px.

## Rules

- Keep the waveform inside the bubble and at least 13 units thick on the 256 canvas; it is the
  only interior detail and must survive the 16 px frame.
- Do not add a gradient, drop shadow or second glyph. The tile must stay flat at tray size.
- The amber pin is a brand accent. The red pin is reserved for unread.
