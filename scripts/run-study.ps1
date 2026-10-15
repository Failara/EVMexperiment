param([Parameter(Mandatory)][string]$Plan)
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    & "$PSScriptRoot/run-series.ps1" -Plan $Plan
    $study = Get-Content $Plan -Raw | ConvertFrom-Json
    $previousGc = $env:DOTNET_gcServer
    try {
        $env:DOTNET_gcServer = '1'
        foreach ($replicate in 1..$study.e0Replicates) {
            $e0Dataset = if ($study.e0DatasetPath) { $study.e0DatasetPath } else { 'data/processed/tuning.bin' }
            dotnet src/EvmMatching.Runner/bin/Release/net10.0/EvmMatching.Runner.dll calibrate $study.seriesId $replicate $e0Dataset
            if ($LASTEXITCODE -ne 0) { throw 'E0 failed; preserve diagnostics.' }
        }
    } finally { $env:DOTNET_gcServer = $previousGc }
    & "$PSScriptRoot/handoff.ps1"
} finally { Pop-Location }
