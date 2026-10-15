$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    $runner = 'src/EvmMatching.Runner/bin/Release/net10.0/EvmMatching.Runner.dll'
    dotnet $runner normalize
    if ($LASTEXITCODE -ne 0) { throw 'Normalization failed.' }
    foreach ($scenario in @(@('tuning','pilot-static',0),@('tuning','pilot-updates',10),@('measurement','qualification-static',0),@('measurement','qualification-updates',10))) {
        dotnet $runner prepare $scenario[0] $scenario[1] 100 0.01 $scenario[2] 1 240001
        if ($LASTEXITCODE -ne 0) { throw "Preparation failed: $($scenario[1])" }
        $path = "configs/$($scenario[1]).json"
        $config = Get-Content $path -Raw | ConvertFrom-Json
        $config.controlIntervalMs = 100; $config.fixedRebuildSeconds = 0.5
        $config.warmupSeconds = 1; $config.measureSeconds = 3
        if ($scenario[0] -eq 'measurement' -and $scenario[1] -eq 'qualification-updates') {
            $config.seriesId = 'qualification_v2'; $config.experiment = 'qualification'
            $config.warmupSeconds = 0.25; $config.measureSeconds = 0.5
        }
        $config | ConvertTo-Json -Depth 10 | Set-Content $path -Encoding utf8NoBOM
    }
    if (-not (Test-Path configs/final-experiment.json)) { Copy-Item -LiteralPath configs/qualification-updates.json -Destination configs/final-experiment.json }
    & "$PSScriptRoot/rebuild-simulations.ps1"
    # Normalize the manifest to canonical dataset products only, independent of existing scenarios.
    $manifestPath = 'data/processed/dataset_manifest.json'
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $canonical = @('events.jsonl.zst','queries.bin','tuning.bin','validation.bin','measurement.bin','rejected_records.json')
    $manifest.files = @($manifest.files | Where-Object { (Split-Path $_.path -Leaf) -in $canonical })
    $manifest | ConvertTo-Json -Depth 20 | Set-Content $manifestPath -Encoding utf8NoBOM
} finally { Pop-Location }
