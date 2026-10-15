$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    $success = 0; $failed = 0
    foreach ($file in Get-ChildItem results/raw -Filter run_summary.json -Recurse) {
        $directory = $file.Directory.FullName
        $summary = Get-Content $file.FullName -Raw | ConvertFrom-Json
        if ($summary.status -eq 'SUCCESS') {
            foreach ($required in @('config.json','environment.json','interval_metrics.csv','stdout.log','stderr.log','SUCCESS')) {
                if (-not (Test-Path -LiteralPath (Join-Path $directory $required))) { throw "Missing $required in $directory" }
            }
            if ($summary.fn_count -ne 0 -or $summary.final_fp_count -ne 0) { throw 'Incorrect result was marked successful.' }
            $config = Get-Content (Join-Path $directory 'config.json') -Raw | ConvertFrom-Json
            foreach ($inputName in @('dataset','watchlist','updates')) {
                $path = $config."$($inputName)Path"; $expected = $config."$($inputName)Sha256"
                if ($path -and (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw "Input hash mismatch: $path" }
            }
            if ($summary.experiment -ne 'E0') {
                $intervals = @(Import-Csv (Join-Path $directory 'interval_metrics.csv'))
                if (($intervals | Measure-Object query_count -Sum).Sum -ne $summary.query_count) { throw 'Interval query counts do not sum to summary.' }
                if (($intervals | Measure-Object update_count -Sum).Sum -ne $summary.update_count) { throw 'Interval update counts do not sum to summary.' }
                $end = 0
                foreach ($interval in $intervals) { if ([long]$interval.start_ns -ne $end -or [long]$interval.end_ns -lt $end) { throw 'Non-contiguous intervals.' }; $end = [long]$interval.end_ns }
            }
            $success++
        }
    }
    foreach ($failure in Get-ChildItem results/raw -Filter failure.json -Recurse) {
        if (Test-Path -LiteralPath (Join-Path $failure.Directory.FullName 'SUCCESS')) { throw 'Failed run has SUCCESS.' }
        $failed++
    }
    # Compare every group of method runs within the same replicate/scenario.
    $configs = @(Get-ChildItem results/raw -Filter config.json -Recurse | ForEach-Object { $c = Get-Content $_.FullName -Raw | ConvertFrom-Json; if ($c.method -ne 'primitives') { $c } })
    foreach ($group in $configs | Group-Object seriesId,experiment,scenario,replicate) {
        foreach ($field in @('seed','datasetSha256','watchlistSha256','updatesSha256','threads','gcMode')) {
            if (@($group.Group.$field | Sort-Object -Unique).Count -ne 1) { throw "Inconsistent $field in comparison." }
        }
    }
    Write-Output "Validated successful runs: $success; preserved failed runs: $failed"
} finally { Pop-Location }
