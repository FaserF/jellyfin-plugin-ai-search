<#
.SYNOPSIS
    Local reusable .NET 10 builder & test runner for Jellyfin.Plugin.AiSearch using local Docker.
.DESCRIPTION
    Runs .NET 10 build, unit tests, and artifact packaging inside an isolated container
    without polluting or burdening the host system or remote VMs.
.EXAMPLE
    .\build-local.ps1
    .\build-local.ps1 -Package
#>
param (
    [switch]$Package,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $RepoRoot

Write-Host "🐳 Running local build via mcr.microsoft.com/dotnet/sdk:10.0..." -ForegroundColor Cyan

$packageCmd = if ($Package) {
    "mkdir -p /src/artifacts && cp Jellyfin.Plugin.AiSearch/bin/Release/net10.0/Jellyfin.Plugin.AiSearch.dll /src/artifacts/ && cp Jellyfin.Plugin.AiSearch/bin/Release/net10.0/Jellyfin.Plugin.AiSearch.pdb /src/artifacts/ && echo '✅ Artifacts packaged to /src/artifacts/'"
} else {
    "echo 'Build finished.'"
}

$testCmd = if (-not $SkipTests) {
    "dotnet run --project Jellyfin.Plugin.AiSearch.Tests -c Release --no-build"
} else {
    "echo 'Skipping tests.'"
}

docker run --rm `
    -v "${RepoRoot}:/src" `
    -w /src `
    mcr.microsoft.com/dotnet/sdk:10.0 `
    bash -c "set -e && dotnet restore && dotnet build -c Release --no-restore && $testCmd && $packageCmd"

Write-Host "✨ Local build completed successfully!" -ForegroundColor Green
