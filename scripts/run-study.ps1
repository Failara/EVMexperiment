param([Parameter(Mandatory)][string]$Plan)
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    & "$PSScriptRoot/run-series.ps1" -Plan $Plan
    if ($LASTEXITCODE -ne 0) { throw 'Study batch failed. Preserve diagnostics and fix the cause before resuming.' }
    $study = Get-Content $Plan -Raw | ConvertFrom-Json
    $statusPath = "results/raw/$($study.seriesId)/series_status.json"
    $seriesStatus = Get-Content $statusPath -Raw | ConvertFrom-Json
    if ($seriesStatus.status -ne 'COMPLETE') {
        Write-Output "Study batch finished with status $($seriesStatus.status). Run this command again later with the same plan to continue."
        return
    }
    $previousGc = $env:DOTNET_gcServer
    try {
        $env:DOTNET_gcServer = '1'
        foreach ($replicate in 1..$study.e0Replicates) {
            $e0Dataset = if ($study.e0DatasetPath) { $study.e0DatasetPath } else { 'data/processed/tuning.bin' }
            $runId = 'E0_primitives_r{0:00}_s240001' -f $replicate
            if (Test-Path "results/raw/$($study.seriesId)/E0/primitives/$runId/SUCCESS") { continue }
            if (Test-Path "results/raw/$($study.seriesId)/E0/primitives/$runId") { throw "Incomplete E0 calibration exists: $runId. Preserve diagnostics." }
            dotnet src/EvmMatching.Runner/bin/Release/net10.0/EvmMatching.Runner.dll calibrate $study.seriesId $replicate $e0Dataset
            if ($LASTEXITCODE -ne 0) { throw 'E0 failed; preserve diagnostics.' }
        }
    } finally { $env:DOTNET_gcServer = $previousGc }
    & "$PSScriptRoot/handoff.ps1"
} finally { Pop-Location }
