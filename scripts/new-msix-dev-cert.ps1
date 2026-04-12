<#
.SYNOPSIS
  Create a self-signed code-signing .pfx for local MSIX publish (dev only).

.DESCRIPTION
  Writes Desktop\HermesDesktop\packaging\dev-msix.pfx (gitignored) and optionally sets
  Package.appxmanifest + packaging\HermesDesktop.appinstaller Publisher to match the cert.

.PARAMETER UpdateManifests
  When set, sets Identity / MainPackage Publisher to the cert subject (default CN=AppPublisher) in:
  - Desktop\HermesDesktop\Package.appxmanifest
  - Desktop\HermesDesktop\packaging\HermesDesktop.appinstaller

.PARAMETER Password
  PFX password (default: dev).

.EXAMPLE
  .\scripts\new-msix-dev-cert.ps1 -UpdateManifests
  .\scripts\publish-msix.ps1 -CertificatePath "Desktop\HermesDesktop\packaging\dev-msix.pfx" -CertificatePassword dev
#>
param(
    [switch] $UpdateManifests,

    [string] $Password = "dev"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
# Must match Package.appxmanifest Identity Publisher (repo default: CN=AppPublisher).
$subject = "CN=AppPublisher"
$pfxDir = Join-Path $repoRoot "Desktop\HermesDesktop\packaging"
$pfxPath = Join-Path $pfxDir "dev-msix.pfx"

New-Item -ItemType Directory -Path $pfxDir -Force | Out-Null

Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq $subject } |
    ForEach-Object { Remove-Item $_.PSPath -Force }

$cert = New-SelfSignedCertificate `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -Subject $subject `
    -KeyExportPolicy Exportable `
    -KeyUsage DigitalSignature `
    -Type CodeSigning `
    -NotAfter (Get-Date).AddYears(5)

$sec = ConvertTo-SecureString -String $Password -AsPlainText -Force
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $sec | Out-Null

Write-Host "Created: $pfxPath" -ForegroundColor Green
Write-Host "Subject (must match manifest Publisher): $subject" -ForegroundColor Cyan
Write-Host "Password: $Password" -ForegroundColor Yellow

if ($UpdateManifests) {
    $manifestPath = Join-Path $repoRoot "Desktop\HermesDesktop\Package.appxmanifest"
    $m = [xml](Get-Content -LiteralPath $manifestPath)
    $id = @($m.GetElementsByTagName("Identity")) | Select-Object -First 1
    if ($null -eq $id) { throw "No Identity element in Package.appxmanifest" }
    $id.SetAttribute("Publisher", $subject)
    $m.Save($manifestPath)
    Write-Host "Updated Identity Publisher in Package.appxmanifest" -ForegroundColor Green

    $appInstPath = Join-Path $repoRoot "Desktop\HermesDesktop\packaging\HermesDesktop.appinstaller"
    $a = [xml](Get-Content -LiteralPath $appInstPath)
    $main = @($a.GetElementsByTagName("MainPackage")) | Select-Object -First 1
    if ($null -eq $main) { throw "No MainPackage in HermesDesktop.appinstaller" }
    $main.SetAttribute("Publisher", $subject)
    $a.Save($appInstPath)
    Write-Host "Updated MainPackage Publisher in HermesDesktop.appinstaller" -ForegroundColor Green
}
else {
    Write-Host "`nIf you do not use -UpdateManifests, set Identity Publisher in Package.appxmanifest to exactly:" -ForegroundColor Yellow
    Write-Host "  $subject" -ForegroundColor White
}

Write-Host "`nThen publish:" -ForegroundColor Cyan
Write-Host "  .\scripts\publish-msix.ps1 -CertificatePath `"$pfxPath`" -CertificatePassword $Password" -ForegroundColor White
