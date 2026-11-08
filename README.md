# NextGen Island

Windows desktop Dynamic Island-inspired utility (WPF / .NET 8). Independent of the JARVIS project.

## Included
- Floating always-on-top pill with expand/collapse.
- Tray icon; closing the window hides it instead of exiting.
- Native Windows clipboard change listener.
- SQLite-backed text history that survives app close/reboot.
- Clipboard search, copy-back, pin/unpin, delete and clear-unpinned.
- Pause clipboard capture and start-with-Windows settings.
- CPU and RAM usage cards.
- GitHub Actions workflow for self-contained Windows x64 releases.

## Not yet implemented
Global media-session controls, GPU utilization/temperature, image/file clipboard formats, installer/MSIX, and encrypted clipboard storage. The music card is currently a placeholder. CPU/RAM are implemented; GPU is not claimed as working.

Clipboard capture runs while the app is running, including when hidden to tray. It cannot capture while the app is not running. Existing saved entries remain on disk.

## Build
Requires Windows 10/11 and .NET 8 SDK.

```powershell
dotnet restore
dotnet build .\src\NextGenIsland\NextGenIsland.csproj -c Release
dotnet run --project .\src\NextGenIsland\NextGenIsland.csproj
```

## Publish locally
```powershell
dotnet publish .\src\NextGenIsland\NextGenIsland.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
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
