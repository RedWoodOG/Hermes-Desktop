<#
.SYNOPSIS
  Publish Hermes Desktop to a fixed folder, then install shortcuts (Start Menu + optional Desktop).

.DESCRIPTION
  1) dotnet publish (self-contained payload from HermesDesktop.csproj)
  2) Mirror publish output into InstallDir (default: %LOCALAPPDATA%\Programs\HermesDesktop)
  3) Create "Hermes Desktop.lnk" with icon

  Updates: re-run this script after each publish; it overwrites files in InstallDir.

.PARAMETER Configuration
  Debug or Release (default Release).

.PARAMETER InstallDir
  Where the runnable app lives. Use a stable path so shortcuts never need to change.

.PARAMETER DesktopShortcut
  Also place a shortcut on the public Desktop.

.EXAMPLE
  .\scripts\deploy-desktop.ps1
  .\scripts\deploy-desktop.ps1 -Configuration Debug -DesktopShortcut
#>
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [string] $InstallDir = $(Join-Path $env:LOCALAPPDATA "Programs\HermesDesktop"),

    [switch] $DesktopShortcut
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$csproj = Join-Path $repoRoot "Desktop\HermesDesktop\HermesDesktop.csproj"
$publishAbs = Join-Path $repoRoot "artifacts\publish\HermesDesktop"

Write-Host "Publishing -> $publishAbs" -ForegroundColor Cyan
New-Item -ItemType Directory -Path $publishAbs -Force | Out-Null
# -o: stable path; Release csproj enables trimming — disable for sideload folder deploy reliability
dotnet publish $csproj `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishTrimmed=false `
    -o $publishAbs `
    -v:minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (-not (Test-Path (Join-Path $publishAbs "HermesDesktop.exe"))) {
    Write-Error "Publish output missing HermesDesktop.exe under $publishAbs"
}

Write-Host "Installing files -> $InstallDir" -ForegroundColor Cyan
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
robocopy $publishAbs $InstallDir /MIR /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -ge 8) {
    Write-Error "robocopy failed with exit $LASTEXITCODE"
}

$exePath = Join-Path $InstallDir "HermesDesktop.exe"
$iconPath = Join-Path $InstallDir "Assets\AppIcon.ico"
if (-not (Test-Path $iconPath)) {
    $iconPath = "$exePath,0"
}

function New-HermesShortcut([string] $shortcutPath) {
    $shell = New-Object -ComObject WScript.Shell
    $sc = $shell.CreateShortcut($shortcutPath)
    $sc.TargetPath = $exePath
    $sc.WorkingDirectory = $InstallDir
    $sc.IconLocation = $iconPath
    $sc.Description = "Hermes Desktop"
    $sc.Save()
}

$startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
New-Item -ItemType Directory -Path $startMenu -Force | Out-Null
$startLnk = Join-Path $startMenu "Hermes Desktop.lnk"
New-HermesShortcut $startLnk
Write-Host "Start Menu: $startLnk" -ForegroundColor Green

if ($DesktopShortcut) {
    $desk = [Environment]::GetFolderPath("CommonDesktopDirectory")
    if (-not $desk) { $desk = Join-Path $env:PUBLIC "Desktop" }
    $deskLnk = Join-Path $desk "Hermes Desktop.lnk"
    New-HermesShortcut $deskLnk
    Write-Host "Desktop: $deskLnk" -ForegroundColor Green
}

Write-Host "Done. Launch from Start or: `"$exePath`"" -ForegroundColor Green
