<#
.SYNOPSIS
  Build a signed Hermes Desktop MSIX (single-project packaging).

.PARAMETER CertificatePath
  Path to a .pfx whose subject matches Package.appxmanifest Identity Publisher.

.PARAMETER CertificatePassword
  SecureString or plain string password for the PFX (omit if unencrypted).

.PARAMETER Configuration
  Release (default) or Debug.

.EXAMPLE
  # One-time dev cert + manifest alignment, then publish:
  .\scripts\new-msix-dev-cert.ps1 -UpdateManifests
  .\scripts\publish-msix.ps1 -CertificatePath "Desktop\HermesDesktop\packaging\dev-msix.pfx" -CertificatePassword dev

.EXAMPLE
  .\scripts\publish-msix.ps1 -CertificatePath "$env:USERPROFILE\certs\HermesDesktop.pfx"
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $CertificatePath,

    $CertificatePassword,

    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if (-not (Test-Path $CertificatePath)) {
    Write-Host ""
    Write-Host "Certificate not found: $CertificatePath" -ForegroundColor Red
    Write-Host "The path in the docs was an example - use a real .pfx on disk." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Quick dev setup (self-signed; local machine only):" -ForegroundColor Cyan
    Write-Host "  .\scripts\new-msix-dev-cert.ps1 -UpdateManifests"
    $devPfx = Join-Path $repoRoot "Desktop\HermesDesktop\packaging\dev-msix.pfx"
    Write-Host ('  .\scripts\publish-msix.ps1 -CertificatePath "' + $devPfx + '" -CertificatePassword dev')
    Write-Host ""
    exit 1
}

$csproj = Join-Path $repoRoot "Desktop\HermesDesktop\HermesDesktop.csproj"
$plain = $null
if ($null -ne $CertificatePassword) {
    if ($CertificatePassword -is [securestring]) {
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($CertificatePassword)
        try { $plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    }
    else {
        $plain = [string]$CertificatePassword
    }
}

$publishArgs = @(
    "publish", $csproj,
    "-c", $Configuration,
    "-r", "win-x64",
    "-p:HermesMsixPublish=true",
    "-p:PackageCertificateKeyFile=$CertificatePath",
    "-p:PublishTrimmed=false",
    "-v:minimal"
)
if ($null -ne $plain) {
    $publishArgs += "-p:PackageCertificatePassword=$plain"
}

Write-Host "Publishing MSIX (signed)..." -ForegroundColor Cyan
dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$appPackages = Get-ChildItem -Path (Join-Path $repoRoot "Desktop\HermesDesktop\bin") -Recurse -Filter "*.msix" -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\AppPackages\\' } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 3

if ($appPackages) {
    Write-Host ""
    Write-Host "MSIX output:" -ForegroundColor Green
    $appPackages | ForEach-Object { Write-Host "  $($_.FullName)" }
}
else {
    Write-Host ""
    Write-Host "Publish finished; search Desktop\HermesDesktop\bin\...\AppPackages\ for .msix" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Next: upload the .msix and packaging\HermesDesktop.appinstaller to HTTPS; Publisher must match the cert." -ForegroundColor Cyan
$docUrl = "https://learn.microsoft.com/windows/msix/app-installer/how-to-create-appinstaller-file"
Write-Host $docUrl -ForegroundColor DarkGray
