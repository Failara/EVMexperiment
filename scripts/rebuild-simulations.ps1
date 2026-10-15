$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    $runner = 'src/EvmMatching.Runner/bin/Release/net10.0/EvmMatching.Runner.dll'
    foreach ($file in Get-ChildItem configs -Filter '*-simulation.json' | Sort-Object Name) {
        $simulation = Get-Content $file.FullName -Raw | ConvertFrom-Json
        $configPath = "configs/$($simulation.name).json"
        $savedBytes = if (Test-Path $configPath) { [IO.File]::ReadAllBytes((Join-Path $PWD $configPath)) } else { $null }
        $frozen = if ($savedBytes) { [Text.Encoding]::UTF8.GetString($savedBytes) | ConvertFrom-Json } else { $null }
        dotnet $runner simulate "configs/$($file.Name)"
        if ($LASTEXITCODE -ne 0) { throw "Simulation failed: $($file.Name)" }
        if ($frozen) {
            $regenerated = Get-Content $configPath -Raw | ConvertFrom-Json
            foreach ($field in @('datasetSha256','watchlistSha256','updatesSha256')) {
                if ($frozen.$field -ne $regenerated.$field) { throw "Reproducibility failure in $field for $configPath" }
            }
            [IO.File]::WriteAllBytes((Join-Path $PWD $configPath), $savedBytes)
        }
    }
} finally { Pop-Location }
