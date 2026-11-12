# Builds a self-contained release and (optionally) the installer.
# Usage:  .\publish.ps1            -> .\artifacts\publish + NextGenIsland-win-x64.zip
#         .\publish.ps1 -Installer -> also builds .\artifacts\NextGenIsland-Setup.exe (needs Inno Setup 6)
param([switch]$Installer)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$out = Join-Path $PSScriptRoot "artifacts\publish"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish .\NextGenIsland.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$zip = Join-Path $PSScriptRoot "artifacts\NextGenIsland-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "Zip:       $zip"

if ($Installer) {
    $iscc = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw "Inno Setup 6 not found. Install it from https://jrsoftware.org/isinfo.php" }
    & $iscc ".\installer\NextGenIsland.iss"
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
    Write-Host "Installer: $PSScriptRoot\artifacts\NextGenIsland-Setup.exe"
}
