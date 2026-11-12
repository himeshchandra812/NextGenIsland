# NextGen Island

A Dynamic Island for Windows 11, built with WPF and .NET 8. A small pill sits at the top of your screen
showing CPU load and temperature; hover to expand it into ten tabs. Inspired by NotchPixel.

## Features

| Tab | What it does |
| --- | --- |
| Media | Now playing from any Windows media app, transport controls, seeking, live lyrics, colours taken from the album art |
| Notifications | Banners on the pill, history (3 at a time, scroll the mouse wheel for more), quiet mode, clear all |
| Clipboard | Searchable history of text, images and files, pin and filter, click to copy back |
| System | CPU, RAM, GPU (load, temperature, VRAM), battery and network speed |
| Shelf | Drop files on the island to park them, click to open, drag out to copy |
| Reminders | Quick reminders with times and repeats |
| Agents | Shows whether Claude, Codex, Antigravity or Hermes processes are running |
| Widgets | Date, clock, 25-minute focus timer, weather for your city, CPU and RAM sparklines |
| Web | Pinned sites that open in a browser drawer under the island (WebView2) |
| Calls | Microphone mute, speaker and microphone test |

Also:
- **Shapes:** Pill (default) or Notch.
- **Themes:** Liquid Glass, Wallpaper (tinted from your desktop wallpaper), Black, Graphite, Acrylic.
- **Full-screen apps:** the island shrinks to a thin line while a full-screen app is in front.
- **Settings:** show or hide tabs, start with Windows, weather city, shape and theme. Tray icon included.

## Install

Download `NextGenIsland-Setup.exe` or the `NextGenIsland-win-x64.zip` from the
[Releases](../../releases) page. The Web tab needs the Microsoft WebView2 Runtime, which ships with
Windows 11 and recent Windows 10.

## Build from source

Requires Windows 10/11 and the .NET 8 SDK.

```powershell
dotnet build -c Release
dotnet run -c Release
```

## Make a release

```powershell
.\publish.ps1              # self-contained build and zip in .\artifacts
.\publish.ps1 -Installer   # also builds NextGenIsland-Setup.exe (needs Inno Setup 6)
```

Pushing a tag such as `v1.0.0` also builds and publishes the zip to GitHub Releases through
`.github/workflows/windows-release.yml`.

## Data and privacy

Everything stays on your PC under `%LOCALAPPDATA%\NextGenIsland`: clipboard history (`clipboard.db` and the
`clips` folder), settings, shelf and reminders, and the web drawer's browser profile. The only network calls are
for weather (Open-Meteo), lyrics (LRCLIB) and the pages you open in the Web tab. There is no telemetry.
Clipboard history can contain passwords, so use "Pause clipboard history" in Settings before copying secrets.
Uninstalling does not delete this folder.

## Not included

Phone transfer, global volume, brightness, Wi-Fi and Bluetooth controls, gaming overlay and stream tools.

## License

MIT
