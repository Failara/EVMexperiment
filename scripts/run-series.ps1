param([string]$Plan = 'configs/qualification-plan.json')
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
$previousWorkloadResolver = $env:MSBuildEnableWorkloadResolver
$previousNugetPackages = $env:NUGET_PACKAGES
try {
    $env:MSBuildEnableWorkloadResolver = 'false'
    $env:NUGET_PACKAGES = Join-Path (Get-Location) '.nuget/packages'
    dotnet build -c Release -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    dotnet test -c Release --no-build -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Correctness tests failed. Measurements are prohibited.' }
    dotnet src/EvmMatching.Runner/bin/Release/net10.0/EvmMatching.Runner.dll run-series $Plan
    if ($LASTEXITCODE -ne 0) { throw 'Series stopped. Preserve the failed run and use a new seriesId after fixing the cause.' }
} finally {
    $env:MSBuildEnableWorkloadResolver = $previousWorkloadResolver
    $env:NUGET_PACKAGES = $previousNugetPackages
    Pop-Location
}
