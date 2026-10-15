param(
    [Parameter(Mandatory)][string]$SeriesId,
    [switch]$Smoke,
    [switch]$IncludeFiveMillion,
    [switch]$Resume,
    [int]$Seed = 240001
)
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    if ($SeriesId -notmatch '^[A-Za-z0-9_-]+$') { throw 'Invalid series ID.' }
    if (Test-Path "configs/$SeriesId-plan.json") { throw 'Plan already exists. Use a new series ID.' }
    foreach ($directory in @('configs','data/processed','data/watchlists','data/updates')) {
        if (Test-Path $directory) {
            $partial = Get-ChildItem $directory -File -Filter "${SeriesId}_*" | Select-Object -First 1
            if ($partial -and -not $Resume) { throw "Study files already exist for $SeriesId in $directory. Use -Resume to validate and continue them." }
        }
    }
    $dataDirectory = if ($Smoke) { 'data/processed' } else { 'data/fullstudy/processed' }
    $manifest = Get-Content "$dataDirectory/dataset_manifest.json" -Raw | ConvertFrom-Json
    $wSizes = if ($Smoke) { @(100) } else { @(1000,10000,100000,1000000) }
    if ($IncludeFiveMillion -and -not $Smoke) { $wSizes += 5000000 }
    $maxW = ($wSizes | Measure-Object -Maximum).Maximum
    if ($manifest.counts.unique_addresses -le $maxW) { throw "The grid needs more than $maxW distinct real addresses; archive has $($manifest.counts.unique_addresses). Import a larger contiguous archive before preparing this study." }
    $baselineW = if ($Smoke) { 100 } else { 100000 }
    $seconds = if ($Smoke) { 1.0 } else { 300.0 }
    $qps = if ($Smoke) { 2000.0 } else { 100000.0 }
    $warmup = if ($Smoke) { 0.25 } else { 30.0 }
    $replicates = if ($Smoke) { 1 } else { 30 }
    $runner = 'src/EvmMatching.Runner/bin/Release/net10.0/EvmMatching.Runner.dll'
    $paths = [System.Collections.Generic.List[string]]::new()
    $canonicalFiles = @{}
    $simulationCache = @{}
    function Link-Duplicate([string]$Path) {
        if (-not (Test-Path -LiteralPath $Path)) { return }
        $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
        $key = "$( [IO.Path]::GetExtension($Path) ):$hash"
        if ($canonicalFiles.ContainsKey($key)) {
            $target = $canonicalFiles[$key]
            if ((Resolve-Path -LiteralPath $target).Path -ne (Resolve-Path -LiteralPath $Path).Path) {
                Remove-Item -LiteralPath $Path
                try { New-Item -ItemType HardLink -Path $Path -Target $target -ErrorAction Stop | Out-Null }
                catch { Copy-Item -LiteralPath $target -Destination $Path }
            }
        } else { $canonicalFiles[$key] = (Resolve-Path -LiteralPath $Path).Path }
    }
    function Add-Case([string]$Experiment, [string]$Label, [int]$WSize, [object[]]$Phases, [hashtable]$Changes = @{}) {
        $name = "$($SeriesId)_$($Experiment)_$Label"
        $simulation = @{name=$name;partition='measurement';dataDirectory=$dataDirectory;wSize=$WSize;seed=$Seed;queryRatePerSecond=$qps;phases=$Phases}
        $simulationPath = "configs/$name-simulation.json"
        $simulation | ConvertTo-Json -Depth 10 | Set-Content $simulationPath
        $keyPlan = @{partition='measurement';dataDirectory=$dataDirectory;wSize=$WSize;seed=$Seed;queryRatePerSecond=$qps;phases=$Phases} | ConvertTo-Json -Depth 10 -Compress
        if ($simulationCache.ContainsKey($keyPlan)) {
            $cached = $simulationCache[$keyPlan]
            $config = Get-Content $cached.config -Raw | ConvertFrom-Json
            $config.Scenario = $name
            Copy-Item -LiteralPath $cached.manifest -Destination "data/processed/${name}_manifest.json"
        } else {
            $path = "configs/$name.json"
            $manifestPath = "data/processed/${name}_manifest.json"
            $validExisting = $Resume -and (Test-Path $path) -and (Test-Path $manifestPath)
            if ($validExisting) {
                $existingConfig = Get-Content $path -Raw | ConvertFrom-Json
                $existingManifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
                foreach ($item in @(@($existingConfig.DatasetPath,$existingConfig.DatasetSha256),@($existingConfig.WatchlistPath,$existingConfig.WatchlistSha256),@($existingConfig.UpdatesPath,$existingConfig.UpdatesSha256))) {
                    if (-not (Test-Path $item[0]) -or (Get-FileHash -LiteralPath $item[0] -Algorithm SHA256).Hash -ne $item[1]) { $validExisting = $false; break }
                }
                if ($validExisting) {
                    foreach ($item in $existingManifest.files) {
                        if (-not (Test-Path $item.path) -or (Get-FileHash -LiteralPath $item.path -Algorithm SHA256).Hash -ne $item.sha256) { $validExisting = $false; break }
                    }
                }
                if ($validExisting) { $config = $existingConfig }
            }
            if (-not $validExisting) {
                foreach ($generated in @($path,$manifestPath,"data/processed/${name}_queries.bin.gz","data/watchlists/${name}.bin","data/updates/${name}.csv")) {
                    if (Test-Path $generated) { Remove-Item -LiteralPath $generated -Force }
                }
                dotnet $runner simulate $simulationPath
                if ($LASTEXITCODE -ne 0) { throw "Cannot prepare $name. No measurements were started." }
                Link-Duplicate "data/processed/${name}_queries.bin.gz"
                Link-Duplicate "data/watchlists/${name}.bin"
                Link-Duplicate "data/updates/${name}.csv"
                $config = Get-Content $path -Raw | ConvertFrom-Json
            }
            $simulationCache[$keyPlan] = @{config="configs/$name.json";manifest="data/processed/${name}_manifest.json"}
        }
        $path = "configs/$name.json"
        $config.seriesId=$SeriesId; $config.experiment=$Experiment; $config.scenario=$name; $config.warmupSeconds=$warmup
        if ($Smoke) { $config.controlIntervalMs=100; $config.fixedRebuildSeconds=0.2 }
        foreach ($key in $Changes.Keys) { $config.$key=$Changes[$key] }
        $config | ConvertTo-Json -Depth 10 | Set-Content $path
        $paths.Add($path)
    }
    function Phase([string]$Name, [double]$Hit, [double]$Rate, [double]$Duration) { @{name=$Name;hitRate=$Hit;updateRatePerSecond=$Rate;seconds=$Duration} }
    foreach ($w in $wSizes) { Add-Case E1 "w$w" $w @((Phase P1 0.01 0 $seconds)) }
    $hitRates = if ($Smoke) { @(0,0.5,1) } else { @(0,0.0001,0.001,0.01,0.05,0.1,0.25,0.5,0.75,1) }
    foreach ($hit in $hitRates) { $label = ([string]$hit).Replace('.','p'); Add-Case E2 "hit$label" $baselineW @((Phase P1 $hit 0 $seconds)) }
    $rates = if ($Smoke) { @(0,10) } else { @(0,10,100,1000,10000) }
    foreach ($rate in $rates) { Add-Case E3 "updates$rate" $baselineW @((Phase P1 0.01 $rate $seconds)) }
    $phaseSeconds = $seconds/5; $churn = if ($Smoke) { 100 } else { 1000 }
    $profiles = @((Phase P1 0.001 0 $phaseSeconds),(Phase P2 0.5 0 $phaseSeconds),(Phase P3 0.001 $churn $phaseSeconds),(Phase P4 0.5 $churn $phaseSeconds),(Phase P1 0.001 0 $phaseSeconds))
    Add-Case E4 P1-P2-P3-P4-P1 $baselineW $profiles
    $threadCounts = if ($Smoke) { @(1,2) } else { @(1,2,4,8,[Environment]::ProcessorCount) }
    foreach ($threads in $threadCounts | Where-Object { $_ -le [Environment]::ProcessorCount } | Sort-Object -Unique) { Add-Case E5 "threads$threads" $baselineW @((Phase P1 0.01 0 $seconds)) @{threads=$threads} }
    $sensitivity = if ($Smoke) { @{hysteresisIntervals=@(1,3)} } else { @{controlIntervalMs=@(100,250,500,1000,2000);hysteresisIntervals=@(1,2,3,5);predictionHorizon=@(1,5,10,30);rebuildHorizon=@(5,10,30,60);switchMarginNs=@(0,1000,1000000);rebuildMarginNs=@(0,10000,10000000)} }
    foreach ($parameter in $sensitivity.Keys | Sort-Object) {
        foreach ($value in $sensitivity[$parameter]) { $changes=@{}; $changes[$parameter]=$value; $label = "$parameter$value".Replace('.','p'); Add-Case E6 $label $baselineW $profiles $changes }
    }
    $plan=@{seriesId=$SeriesId;purpose=$(if($Smoke){'Reduced scheduled E1-E6 software smoke grid; not the full study.'}else{'Predeclared full scientific grid E1-E6 with continuous scheduled replay. Check archive size, pilot stability, and resources before running.'});configPaths=$paths.ToArray();methods=@('M1','M2','M3','M4','M5','M6');methodsByExperiment=@{E6=@('M4','M5','M6')};replicates=$replicates;orderSeed=$Seed;e0Replicates=$replicates;e0DatasetPath="$dataDirectory/tuning.bin";queryRatePerSecond=$qps;warmupSeconds=$warmup}
    $plan | ConvertTo-Json -Depth 20 | Set-Content "configs/$SeriesId-plan.json"
    Write-Output "Prepared $($paths.Count) scenarios in configs/$SeriesId-plan.json. Measurements have not started."
} finally { Pop-Location }
