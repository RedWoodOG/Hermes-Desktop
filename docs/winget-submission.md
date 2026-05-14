# Winget submission runbook

This document describes how to publish Hermes Desktop to the Microsoft Winget
package index (`microsoft/winget-pkgs`). It is an **operator-facing** procedure —
end users install via `winget install VyreVaultStudios.HermesDesktop` once the
PR has merged. Run it once at first submission, then on every release tag.

## One-time setup (first submission only)

1. **Identity is immutable.** Before opening the first PR, double-check the
   identity fields in `Directory.Build.props`. After the first merge to
   `microsoft/winget-pkgs`, these cannot change without renaming the package:
   - `<HermesWingetPackageIdentifier>VyreVaultStudios.HermesDesktop</HermesWingetPackageIdentifier>`
   - `<HermesWingetPublisher>VyreVault Studios</HermesWingetPublisher>`
   - `<HermesWingetMoniker>hermes</HermesWingetMoniker>`

2. **Fork** `microsoft/winget-pkgs` to your GitHub account (one-time).

3. **Verify the first portable release** has a public `.zip` and a matching
   `.sha256` sidecar attached to the GitHub Release page. The CI pipeline at
   `.github/workflows/ci-release.yml` produces both automatically on `v*` tags.

## Per-release procedure

After CI completes for a new `vX.Y.Z` tag:

1. Open the run in GitHub Actions → `Release — Portable Zip` → the
   `generate_winget` job.
2. **Download the artifact** named `winget-manifests-X.Y.Z`. It contains three
   YAML files under
   `manifests/v/VyreVaultStudios/HermesDesktop/X.Y.Z/`:
   - `VyreVaultStudios.HermesDesktop.yaml` (version manifest)
   - `VyreVaultStudios.HermesDesktop.locale.en-US.yaml`
   - `VyreVaultStudios.HermesDesktop.installer.yaml`
3. Copy that directory into your fork of `microsoft/winget-pkgs` at the same
   relative path: `manifests/v/VyreVaultStudios/HermesDesktop/X.Y.Z/`.
4. Validate locally before pushing:
   ```pwsh
   winget validate --manifest manifests\v\VyreVaultStudios\HermesDesktop\X.Y.Z
   winget install --manifest manifests\v\VyreVaultStudios\HermesDesktop\X.Y.Z
   ```
5. Commit on a release branch:
   ```pwsh
   git checkout -b VyreVaultStudios.HermesDesktop-X.Y.Z
   git add manifests/v/VyreVaultStudios/HermesDesktop/X.Y.Z
   git commit -m "Add VyreVaultStudios.HermesDesktop version X.Y.Z"
   git push origin VyreVaultStudios.HermesDesktop-X.Y.Z
   ```
6. Open a PR against `microsoft/winget-pkgs` with the title
   `Add VyreVaultStudios.HermesDesktop version X.Y.Z`. The validation bot will
   run within minutes; if it stays green, a reviewer will merge within a day.

## Why `InstallerType: zip` + `NestedInstallerType: portable`?

The portable zip contains `HermesDesktop.exe` plus its WinUI 3 / .NET 10 sibling
files (`Microsoft.UI.Xaml.dll`, runtime DLLs, etc.). A plain
`InstallerType: portable` would copy only the exe — which would fail to launch
because the WinAppSDK runtime files would be missing.

The `zip + portable` combo extracts the entire archive into the user's Winget
package directory (`%LOCALAPPDATA%\Microsoft\WinGet\Packages\<pkg>\`), then
registers `HermesDesktop.exe` as the `hermes-desktop` PATH alias.

## Manual generation (without CI)

If you need to regenerate manifests outside CI (e.g. for a re-submission):

```pwsh
# Run from the repo root, after publishing a portable zip
.\scripts\Generate-WingetManifests.ps1 `
    -Version 2.5.4 `
    -InstallerUrl "https://github.com/RedWoodOG/Hermes-Desktop/releases/download/v2.5.4/HermesDesktop-portable-x64.zip" `
    -InstallerPath ".\Desktop\HermesDesktop\bin\HermesDesktop-portable-x64.zip" `
    -ReleaseNotesUrl "https://github.com/RedWoodOG/Hermes-Desktop/releases/tag/v2.5.4"
```

The script reads identity (publisher, license, moniker) from
`Directory.Build.props` so all release channels stay in lockstep with the
authoritative csproj metadata.

## Why an artifact and not a release asset?

The Winget manifests are *intermediate* — they go into `microsoft/winget-pkgs`
rather than to end users directly. Attaching them to the GitHub Release would
imply they are a download, which they are not. Operators retrieve them from the
Actions artifact when they're ready to submit.

## What happens to the in-app updater when running under Winget?

`UpdateService.IsRunningFromWingetInstall` detects when the executable lives
under `%LOCALAPPDATA%\Microsoft\WinGet\Packages\`. In that case the in-app check
is skipped entirely — the user is told to run `winget upgrade
VyreVaultStudios.HermesDesktop`. This prevents the in-app downloader from
writing into a directory that Winget owns.

## Future: MSIX channel

A signed MSIX channel is parked behind a code-signing cert procurement. Once
that lands, we will add `Installer.msix.template.yaml` and re-enable
`.github/workflows/ci-msix.yml.disabled`. The portable channel continues to ship
for users who prefer unpackaged binaries.
