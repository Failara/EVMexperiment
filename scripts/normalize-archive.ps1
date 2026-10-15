$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    $runner = 'src/EvmMatching.Runner/bin/Release/net10.0/EvmMatching.Runner.dll'
    $canonical = @('events.jsonl.zst','queries.bin','tuning.bin','validation.bin','measurement.bin','rejected_records.json','dataset_manifest.json')
    $archive = 'data/archive/qualification_20block/processed'
    New-Item -ItemType Directory -Force $archive | Out-Null
    foreach ($leaf in $canonical) {
        $source = "data/processed/$leaf"
        $saved = Join-Path $archive $leaf
        if (-not (Test-Path -LiteralPath $saved) -and (Test-Path -LiteralPath $source)) { Copy-Item -LiteralPath $source -Destination $saved }
    }
    $sourceBackup = 'data/archive/qualification_20block/source.json'
    if (-not (Test-Path -LiteralPath $sourceBackup) -and (Test-Path -LiteralPath 'data/raw/source.json')) { Copy-Item -LiteralPath 'data/raw/source.json' -Destination $sourceBackup }
    if (-not (Test-Path -LiteralPath (Join-Path $archive 'dataset_manifest.json'))) { throw 'Could not preserve the original qualification dataset.' }

    try {
        dotnet $runner normalize
        if ($LASTEXITCODE -ne 0) { throw 'Archive normalization failed. Check that every block in source.json is present.' }
        $manifestPath = 'data/processed/dataset_manifest.json'
        $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
        # Keep the full raw archive and address/query partitions. The expanded JSONL event stream is derivable from raw blocks and would duplicate substantial storage.
        $canonicalData = @('queries.bin','tuning.bin','validation.bin','measurement.bin','rejected_records.json')
        $manifest.files = @($manifest.files | Where-Object { (Split-Path $_.path -Leaf) -in $canonicalData })
        $manifest | Add-Member -NotePropertyName source -NotePropertyValue @{path='data/raw/source.json';sha256=(Get-FileHash -LiteralPath 'data/raw/source.json' -Algorithm SHA256).Hash.ToLowerInvariant()} -Force
        if (Test-Path -LiteralPath 'data/raw/fetch_log.jsonl') { $manifest | Add-Member -NotePropertyName retrieval_history -NotePropertyValue @{path='data/raw/fetch_log.jsonl';sha256=(Get-FileHash -LiteralPath 'data/raw/fetch_log.jsonl' -Algorithm SHA256).Hash.ToLowerInvariant()} -Force }
        if (Test-Path -LiteralPath 'data/raw/provider_ranges.json') { $manifest | Add-Member -NotePropertyName provider_ranges -NotePropertyValue @{path='data/raw/provider_ranges.json';sha256=(Get-FileHash -LiteralPath 'data/raw/provider_ranges.json' -Algorithm SHA256).Hash.ToLowerInvariant()} -Force }
        $fullDirectory = 'data/fullstudy/processed'
        New-Item -ItemType Directory -Force $fullDirectory | Out-Null
        foreach ($leaf in $canonicalData) { Copy-Item -LiteralPath "data/processed/$leaf" -Destination "$fullDirectory/$leaf" -Force }
        foreach ($file in $manifest.files) { $file.path = $file.path.Replace('data/processed/','data/fullstudy/processed/') }
        $manifest | ConvertTo-Json -Depth 20 | Set-Content "$fullDirectory/dataset_manifest.json" -Encoding utf8NoBOM
    } finally {
        foreach ($leaf in $canonical) {
            $saved = Join-Path $archive $leaf
            if (Test-Path -LiteralPath $saved) { Copy-Item -LiteralPath $saved -Destination "data/processed/$leaf" -Force }
        }
    }
    $fullManifest = Get-Content data/fullstudy/processed/dataset_manifest.json -Raw | ConvertFrom-Json
    Write-Output "Normalized blocks $($fullManifest.block_from)-$($fullManifest.block_to): $($fullManifest.counts.unique_addresses) distinct real addresses. Original qualification inputs were restored."
} finally { Pop-Location }
