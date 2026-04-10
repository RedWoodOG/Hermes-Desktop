# Hermes Desktop — One-Click Build, Register, and Launch
# Usage: powershell -ExecutionPolicy Bypass -File run-dev.ps1

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host ""
Write-Host "=== Hermes Desktop — Dev Launcher ===" -ForegroundColor Cyan
Write-Host ""

# Step 1: Build
Write-Host "[1/3] Building..." -ForegroundColor Yellow
$buildResult = & dotnet build "$repoRoot\Desktop\HermesDesktop\HermesDesktop.csproj" -c Debug 2>&1
$buildExitCode = $LASTEXITCODE

if ($buildExitCode -ne 0) {
    Write-Host "Build FAILED!" -ForegroundColor Red
    $buildResult | ForEach-Object { Write-Host $_ }
    exit 1
}

$warnings = ($buildResult | Select-String "warning" | Measure-Object).Count
$errors = ($buildResult | Select-String "error" | Measure-Object).Count
Write-Host "  Build succeeded ($warnings warnings, $errors errors)" -ForegroundColor Green

# Step 2: Register MSIX
Write-Host "[2/3] Registering app package..." -ForegroundColor Yellow
$manifestPath = "$repoRoot\Desktop\HermesDesktop\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\AppxManifest.xml"
if (-not (Test-Path $manifestPath)) {
    Write-Host "ERROR: AppxManifest.xml not found at expected path." -ForegroundColor Red
    Write-Host "  Expected: $manifestPath" -ForegroundColor Red
    Write-Host "  Try building with: dotnet build -r win-x64" -ForegroundColor Yellow
    exit 1
}
Add-AppxPackage -Register $manifestPath 2>$null
Write-Host "  Registered." -ForegroundColor Green

# Step 3: Launch
Write-Host "[3/3] Launching Hermes Desktop..." -ForegroundColor Yellow

# Check for known overlay software that interferes with WinUI
$overlayApps = @("MSIAfterburner", "RivaTuner", "RTSS")
$runningOverlays = Get-Process -Name $overlayApps -ErrorAction SilentlyContinue
if ($runningOverlays) {
    $names = ($runningOverlays | Select-Object -ExpandProperty ProcessName -Unique) -join ", "
    Write-Host "  WARNING: Detected overlay/injection software that can interfere with WinUI startup: $names" -ForegroundColor Red
    Write-Host "  WARNING: If Hermes Desktop fails to show a window, close those apps and try again." -ForegroundColor Red
}

Start-Process "shell:AppsFolder\EDC29F63-281C-4D34-8723-155C8122DEA2_1z32rh13vfry6!App"

# Wait a moment and check if the process started
Start-Sleep -Seconds 3
$proc = Get-Process -Name "HermesDesktop" -ErrorAction SilentlyContinue
if ($proc) {
    Write-Host "  Hermes Desktop is running (PID: $($proc.Id))" -ForegroundColor Green
} else {
    Write-Host "  WARNING: Hermes Desktop did not present a visible window after launch." -ForegroundColor Red
    Write-Host "  WARNING: Check C:\ProgramData\Microsoft\Windows\WER\ReportArchive and" -ForegroundColor Yellow
    Write-Host "  %LOCALAPPDATA%\hermes\hermes-cs\logs\desktop-startup.log for crash details." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Done." -ForegroundColor Cyan
