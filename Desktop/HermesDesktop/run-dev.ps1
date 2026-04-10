param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [ValidateSet("x64", "x86", "ARM64")]
    [string]$Platform = "x64",
    [switch]$ShowLocalDetails
)

$dotnet = "C:\Program Files\dotnet\dotnet.exe"
$projectFile = Join-Path $PSScriptRoot "HermesDesktop.csproj"
[xml]$projectXml = Get-Content -Path $projectFile
$targetFramework = $projectXml.Project.PropertyGroup.TargetFramework | Select-Object -First 1

$rid = switch ($Platform) {
    "x86" { "win-x86" }
    "ARM64" { "win-arm64" }
    default { "win-x64" }
}
$outputDir = Join-Path $PSScriptRoot "bin\$Platform\$Configuration\$targetFramework\$rid"
$showLocalDetailsFlagPath = Join-Path $outputDir "show-local-details.flag"
$packageName = "EDC29F63-281C-4D34-8723-155C8122DEA2"

Get-Process HermesDesktop -ErrorAction SilentlyContinue | Stop-Process -Force

$buildSucceeded = $false
for ($attempt = 1; $attempt -le 3; $attempt++) {
    & $dotnet build $projectFile -c $Configuration -p:Platform=$Platform
    if ($LASTEXITCODE -eq 0) {
        $buildSucceeded = $true
        break
    }

    Start-Sleep -Seconds 2
}

if (-not $buildSucceeded) {
    exit $LASTEXITCODE
}

if ($ShowLocalDetails) {
    Set-Content -Path $showLocalDetailsFlagPath -Value "show-local-details" -Encoding ascii
} elseif (Test-Path $showLocalDetailsFlagPath) {
    Remove-Item -LiteralPath $showLocalDetailsFlagPath -Force
}

$manifestPath = Join-Path $outputDir "AppxManifest.xml"
Add-AppxPackage -Register $manifestPath -ForceApplicationShutdown -ForceUpdateFromAnyVersion

$package = Get-AppxPackage | Where-Object { $_.Name -eq $packageName } | Select-Object -First 1
if (-not $package) {
    throw "Hermes Desktop package is not registered."
}

$expectedInstallLocation = [System.IO.Path]::GetFullPath($outputDir).TrimEnd('\')
$actualInstallLocation = $package.InstallLocation.TrimEnd('\')
$installLocationMatches = [string]::Equals(
    $expectedInstallLocation,
    $actualInstallLocation,
    [System.StringComparison]::OrdinalIgnoreCase)

if (-not $installLocationMatches) {
    Write-Warning "Hermes Desktop is still registered from '$actualInstallLocation' instead of '$expectedInstallLocation'."
    Write-Warning "If you moved or recloned the repo, remove the old registration once and rerun:"
    Write-Warning "  Get-AppxPackage | Where-Object { `$_.Name -eq '$packageName' } | Remove-AppxPackage"
}

$overlayProcesses =
    @("RTSS", "MSIAfterburner") |
    ForEach-Object { Get-Process $_ -ErrorAction SilentlyContinue } |
    Where-Object { $_ } |
    Select-Object -ExpandProperty ProcessName -Unique

if ($overlayProcesses) {
    Write-Warning "Detected overlay/injection software that can interfere with WinUI startup: $($overlayProcesses -join ', ')."
    Write-Warning "If Hermes Desktop fails to show a window, close those apps and try again."
}

Start-Process explorer.exe "shell:AppsFolder\$($package.PackageFamilyName)!App"
if ($installLocationMatches) {
    Start-Sleep -Seconds 3

    $visibleWindow =
        Get-Process HermesDesktop -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } |
        Select-Object -First 1

    if (-not $visibleWindow) {
        Write-Warning "Hermes Desktop did not present a visible window after launch."
        Write-Warning "Check C:\ProgramData\Microsoft\Windows\WER\ReportArchive and %LOCALAPPDATA%\hermes\hermes-cs\logs\desktop-startup.log for crash details."
    }
}
else {
    Write-Warning "Skipped visible-window verification because Windows launched a different registered package path."
}
