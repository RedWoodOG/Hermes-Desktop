<#
.SYNOPSIS
  Produce a complete Winget manifest trio (Version, Locale, Installer) for a Hermes Desktop release.

.DESCRIPTION
  Reads templates from build/winget/, substitutes per-release placeholders, and writes the
  rendered YAML files under dist/winget/manifests/v/VyreVaultStudios/HermesDesktop/<version>/.

  Per-release inputs (Version, InstallerUrl, InstallerSha256) are passed in by the caller —
  the immutable identity (PackageIdentifier, Publisher, Moniker, License) comes from
  Directory.Build.props via a tiny `dotnet msbuild -getProperty:...` shellout. This keeps the
  Winget submission in lockstep with whatever csproj+appxmanifest are publishing.

.PARAMETER Version
  Semver string without any "v" prefix, for example "2.5.4".

.PARAMETER InstallerUrl
  Public HTTPS URL of the portable zip on the GitHub Release.

.PARAMETER InstallerSha256
  Lowercase hex SHA-256 of the portable zip. If empty and -InstallerPath is provided,
  the script computes it from the local file.

.PARAMETER InstallerPath
  Local path to the portable zip. Used to compute the sha256 if InstallerSha256 is empty.

.PARAMETER ReleaseNotesUrl
  Full HTTPS URL of the GitHub Release notes page.

.PARAMETER OutputRoot
  Destination root. Defaults to dist/winget/. Existing files at the target version path
  are overwritten — operators run this once per release.

.EXAMPLE
  .\scripts\Generate-WingetManifests.ps1 -Version 2.5.4 `
    -InstallerUrl "https://github.com/RedWoodOG/Hermes-Desktop/releases/download/v2.5.4/HermesDesktop-portable-x64.zip" `
    -InstallerPath ".\Desktop\HermesDesktop\bin\HermesDesktop-portable-x64.zip" `
    -ReleaseNotesUrl "https://github.com/RedWoodOG/Hermes-Desktop/releases/tag/v2.5.4"
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $InstallerUrl,

    [string] $InstallerSha256,

    [string] $InstallerPath,

    [Parameter(Mandatory)]
    [string] $ReleaseNotesUrl,

    [string] $OutputRoot
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if (-not $OutputRoot) {
    $OutputRoot = Join-Path $repoRoot "dist\winget"
}

# --- 1. Resolve identity from Directory.Build.props (single source of truth) ---
$propsPath = Join-Path $repoRoot "Directory.Build.props"
if (-not (Test-Path $propsPath)) {
    throw "Directory.Build.props not found at $propsPath"
}

[xml] $props = Get-Content $propsPath
$identity = @{
    PACKAGE_IDENTIFIER       = $props.Project.PropertyGroup.HermesWingetPackageIdentifier
    PACKAGE_NAME             = $props.Project.PropertyGroup.HermesWingetPackageName
    PUBLISHER                = $props.Project.PropertyGroup.HermesWingetPublisher
    PUBLISHER_URL            = $props.Project.PropertyGroup.HermesWingetPublisherUrl
    LICENSE                  = $props.Project.PropertyGroup.HermesWingetLicense
    LICENSE_URL              = $props.Project.PropertyGroup.HermesWingetLicenseUrl
    MONIKER                  = $props.Project.PropertyGroup.HermesWingetMoniker
    SHORT_DESCRIPTION        = $props.Project.PropertyGroup.HermesWingetShortDescription
    PORTABLE_COMMAND_ALIAS   = $props.Project.PropertyGroup.HermesWingetPortableCommandAlias
}

foreach ($key in $identity.Keys) {
    if ([string]::IsNullOrWhiteSpace($identity[$key])) {
        throw "Directory.Build.props is missing $key"
    }
}

# --- 2. Resolve the installer SHA-256 ---
if (-not $InstallerSha256) {
    if (-not $InstallerPath) {
        throw "Either -InstallerSha256 or -InstallerPath must be provided."
    }
    if (-not (Test-Path $InstallerPath)) {
        throw "Installer file not found at $InstallerPath"
    }
    $hash = Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256
    $InstallerSha256 = $hash.Hash.ToUpperInvariant()
} else {
    $InstallerSha256 = $InstallerSha256.ToUpperInvariant()
}

if ($InstallerSha256 -notmatch '^[0-9A-F]{64}$') {
    throw "InstallerSha256 must be a 64-character hex digest. Got: $InstallerSha256"
}

# --- 3. Substitute placeholders ---
$values = $identity + @{
    VERSION            = $Version
    INSTALLER_URL      = $InstallerUrl
    INSTALLER_SHA256   = $InstallerSha256
    RELEASE_DATE       = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
    RELEASE_NOTES_URL  = $ReleaseNotesUrl
}

function Render-Template {
    param([string] $TemplatePath, [hashtable] $Values)

    $content = Get-Content -LiteralPath $TemplatePath -Raw

    # Validate every placeholder has a value before substituting.
    $tokens = [regex]::Matches($content, '\{\{([A-Z0-9_]+)\}\}') |
        ForEach-Object { $_.Groups[1].Value } |
        Sort-Object -Unique
    foreach ($t in $tokens) {
        if (-not $Values.ContainsKey($t) -or [string]::IsNullOrWhiteSpace($Values[$t])) {
            throw "Template $TemplatePath references {{$t}} but no value was provided."
        }
    }

    foreach ($entry in $Values.GetEnumerator()) {
        $content = $content.Replace("{{$($entry.Key)}}", $entry.Value)
    }
    return $content
}

$buildWinget = Join-Path $repoRoot "build\winget"
$versionYaml   = Render-Template (Join-Path $buildWinget "Version.template.yaml") $values
$localeYaml    = Render-Template (Join-Path $buildWinget "Locale.en-US.template.yaml") $values
$installerYaml = Render-Template (Join-Path $buildWinget "Installer.template.yaml") $values

# --- 4. Compute the destination directory (Winget sharding) ---
$publisherSegment = $identity.PACKAGE_IDENTIFIER.Split('.')[0]
$firstLetter = $publisherSegment.Substring(0, 1).ToLowerInvariant()
$pkgPath = $identity.PACKAGE_IDENTIFIER.Replace('.', '\')
$destDir = Join-Path $OutputRoot ("manifests\$firstLetter\$pkgPath\$Version")
New-Item -ItemType Directory -Force -Path $destDir | Out-Null

$pkgId = $identity.PACKAGE_IDENTIFIER
Set-Content -LiteralPath (Join-Path $destDir "$pkgId.yaml")                -Value $versionYaml   -Encoding UTF8
Set-Content -LiteralPath (Join-Path $destDir "$pkgId.locale.en-US.yaml")   -Value $localeYaml    -Encoding UTF8
Set-Content -LiteralPath (Join-Path $destDir "$pkgId.installer.yaml")      -Value $installerYaml -Encoding UTF8

Write-Host ""
Write-Host "Winget manifests written to: $destDir" -ForegroundColor Green
Write-Host "  $pkgId.yaml"
Write-Host "  $pkgId.locale.en-US.yaml"
Write-Host "  $pkgId.installer.yaml"
Write-Host ""
Write-Host "Next step: clone microsoft/winget-pkgs, copy this directory into manifests/, and open a PR."
Write-Host "See docs/winget-submission.md for the operator runbook." -ForegroundColor DarkGray
