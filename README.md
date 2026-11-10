# NextGen Island

Windows desktop Dynamic Island-inspired utility (WPF / .NET 8). Independent of the JARVIS project.

## Included
Ten tabs in one glass island (hover to expand, icon-only nav, anchored at top, not draggable):
- Media: now playing, controls, seeking, album-adaptive colours.
- Notifications: banners and history.
- Clipboard: SQLite text history with search, pin, delete.
- System monitor: CPU, RAM, GPU, battery, network.
- File Shelf: drop files on the island, click to open, drag out to copy.
- Reminders: quick notes (no alarms or snooze yet).
- AI Agents: shows whether Claude, Codex, Antigravity or Hermes processes are running (by process name).
- Widgets: clock and 25 minute focus timer.
- Web Drawer: four pinned sites opening in the default browser.
- Themes: Black, Graphite, Acrylic, Liquid Glass (saved).
- Settings > Visible Cards shows or hides tabs. Tray icon, start with Windows.

## Not yet implemented
Stream Tools, lyrics, weather, recurring reminders, image/file clipboard, real blur-behind for Acrylic, installer/MSIX.

Clipboard capture runs while the app is running, including when hidden to tray. It cannot capture while the app is not running. Existing saved entries remain on disk.

## Build
Requires Windows 10/11 and .NET 8 SDK.

```powershell
dotnet restore
dotnet build .\NextGenIsland.csproj -c Release
dotnet run --project .\NextGenIsland.csproj
```

## Publish locally
```powershell
dotnet publish .\NextGenIsland.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## Push to GitHub
Create an empty GitHub repository, then run in this folder:

```powershell
git init
git add .
git commit -m "Initial NextGen Island app"
git branch -M main
git remote add origin https://github.com/YOUR_USERNAME/NextGenIsland.git
git push -u origin main
git tag v0.1.0
git push origin v0.1.0
```

Pushing a `v*` tag triggers `.github/workflows/windows-release.yml` and publishes a self-contained Windows x64 ZIP to GitHub Releases.

## Data and privacy
Local DB: `%LOCALAPPDATA%\NextGenIsland\clipboard.db`. No cloud upload or telemetry. Clipboard may contain secrets; pause monitoring before copying sensitive content. Removing the app does not remove the database. To erase it, delete the `NextGenIsland` folder under `%LOCALAPPDATA%`.

MIT license.
