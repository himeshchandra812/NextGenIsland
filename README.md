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
| Utility | Brightness and volume sliders, Wi-Fi, Bluetooth, mute and quiet tiles, battery, Windows power mode |
| Shelf | Drop files on the island to park them, click to open, drag out to copy |
| Reminders | Quick reminders with times and repeats |
| Agents | Shows whether Claude, Codex, Antigravity or Hermes processes are running |
| Widgets | Date, clock, 25-minute focus timer, weather for your city, CPU and RAM sparklines |
| Web | Pinned sites that open in a browser drawer under the island (WebView2) |
| Calls | Microphone mute, speaker and microphone test |

Also:
- **Shapes:** Pill (default) or Notch.
- **Themes:** Liquid Glass, Wallpaper (tinted from your desktop wallpaper), Black, Graphite, Acrylic.
- **Key pop-up:** pressing the volume or brightness keys shows a slider on the island (can be turned off in Settings).
- **Full-screen apps:** the island shrinks to a thin line while a full-screen app is in front.
- **Settings:** show or hide tabs, start with Windows, weather city, shape, theme, hover and animation speed, pill content
  (CPU and temperature, clock, or both), 12/24-hour clock, Fahrenheit, top gap and opacity. Tray icon included.

## Install

Download `NextGenIsland-Setup.exe` or the `NextGenIsland-win-x64.zip` from the
[Releases](../../releases) page. The Web tab needs the Microsoft WebView2 Runtime, which ships with
Windows 11 and recent Windows 10.

**Windows SmartScreen:** the app is not code-signed yet, so Windows may show "Windows protected your PC" the
first time. Click **More info**, then **Run anyway**. You can check the download against the source in this repo.

Launching the app a second time just brings the running island forward. Closing the island window keeps it
running in the tray; use the tray menu (Open, Settings, Exit) to control it.

## Troubleshooting

- **Something looks wrong or the app closed:** open Settings, scroll to **About** and click **Open log**.
  Include that file when you report an issue.
- **Brightness slider does nothing:** brightness control works on laptop and built-in displays only; most external
  monitors do not expose it to Windows.
- **Notifications from other apps do not appear:** Windows does not give plain desktop apps access to other apps'
  notifications. The Notifications tab shows NextGen Island's own alerts (reminders, focus timer, battery).
- **The Web tab is blank:** install the [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/).
- **Reset everything:** exit the app and delete `%LOCALAPPDATA%\NextGenIsland\settings.json`.

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

Pushing a tag such as `v1.2.0` also builds and publishes the zip to GitHub Releases through
`.github/workflows/windows-release.yml`.

## Data and privacy

Everything stays on your PC under `%LOCALAPPDATA%\NextGenIsland`: clipboard history (`clipboard.db` and the
`clips` folder), settings, shelf and reminders, and the web drawer's browser profile. The only network calls are
for weather (Open-Meteo), lyrics (LRCLIB) and the pages you open in the Web tab. There is no telemetry.
Clipboard history can contain passwords, so use "Pause clipboard history" in Settings before copying secrets.
Uninstalling does not delete this folder.

## Not included

Phone transfer, night light, Focus assist toggles, gaming overlay and stream tools.

## License

MIT
