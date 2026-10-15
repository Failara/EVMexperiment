param([string]$SeriesId = 'pilot_fullstudy_20261015', [int]$Seed = 240001)
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    if ($SeriesId -notmatch '^pilot_[A-Za-z0-9_-]+$') { throw 'Pilot series ID must start with pilot_.' }
    $manifest = Get-Content data/fullstudy/processed/dataset_manifest.json -Raw | ConvertFrom-Json
    if ($manifest.counts.unique_addresses -le 1000000) { throw 'The full-size pilot needs more than one million distinct real addresses.' }
    $planPath = "configs/$SeriesId-plan.json"
    $runner = 'src/EvmMatching.Runner/bin/Release/net10.0/EvmMatching.Runner.dll'
    $configPaths = [System.Collections.Generic.List[string]]::new()
    foreach ($partition in @(@('tuning','tune'),@('validation','validate'))) {
        $name = "${SeriesId}_$($partition[1])_W1m"
        $configPath = "configs/$name.json"
        $simulationPath = "configs/$name-simulation.json"
        foreach ($path in @($configPath,$simulationPath,"data/processed/${name}_manifest.json")) {
            if (Test-Path -LiteralPath $path) { throw "Pilot files already exist: $path. Use a new pilot SeriesId." }
        }
        $simulation = @{name=$name;partition=$partition[0];dataDirectory='data/fullstudy/processed';wSize=1000000;seed=$Seed;queryRatePerSecond=100000.0;phases=@(@{name='P1';hitRate=0.01;updateRatePerSecond=0.0;seconds=30.0})}
        $simulation | ConvertTo-Json -Depth 10 | Set-Content $simulationPath
        dotnet $runner simulate $simulationPath
        if ($LASTEXITCODE -ne 0) { throw "Could not prepare full-size $($partition[1]) pilot." }
        $config = Get-Content $configPath -Raw | ConvertFrom-Json
        $config.seriesId=$SeriesId; $config.experiment='E1'; $config.scenario="W1m_P1_$($partition[1])"; $config.warmupSeconds=30.0; $config.measureSeconds=30.0
        $config | ConvertTo-Json -Depth 10 | Set-Content $configPath
        $configPaths.Add($configPath)
    }
    $plan = @{seriesId=$SeriesId;purpose='Short full-size tuning and validation partition pilot for warmup stability and memory; not part of the final measurement series.';configPaths=$configPaths.ToArray();methods=@('M1','M2','M3','M4');replicates=1;orderSeed=$Seed;warmupSeconds=30.0;measureSeconds=30.0}
    $plan | ConvertTo-Json -Depth 10 | Set-Content $planPath
    & "$PSScriptRoot/run-series.ps1" -Plan $planPath
    if ($LASTEXITCODE -ne 0) { throw 'Tuning pilot failed. Preserve diagnostics and do not start the final series.' }
    Write-Output "Full-size tuning pilot completed in results/raw/$SeriesId. Review interval stability, resource use, and zero-error SUCCESS markers before finalizing the main plan."
} finally { Pop-Location }
