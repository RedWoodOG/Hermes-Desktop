# From repo root: .\run-desktop.ps1
# Optional: .\run-desktop.ps1 -Rebuild   (full rebuild first; note -t:Rebuild not Rebuild.)
param(
    [switch] $Rebuild
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if ($Rebuild) {
    dotnet build HermesDesktop.sln -t:Rebuild
}

dotnet run --project "Desktop\HermesDesktop\HermesDesktop.csproj" @args
