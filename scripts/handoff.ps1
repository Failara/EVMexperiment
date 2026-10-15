$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    & "$PSScriptRoot/validate-results.ps1"
    dotnet src/EvmMatching.Runner/bin/Release/net10.0/EvmMatching.Runner.dll handoff
    if ($LASTEXITCODE -ne 0) { throw 'Handoff generation failed.' }
    $manifest = Get-Content handoff_manifest.json -Raw | ConvertFrom-Json
    foreach ($extra in @('.gitignore','EXPERIMENT_STATUS.md','STUDY_PROTOCOL.md','verification_report.json')) {
        if ((Test-Path $extra) -and $extra -notin $manifest.files.path) {
            $manifest.files += @{path=$extra;sha256=(Get-FileHash -LiteralPath $extra -Algorithm SHA256).Hash.ToLowerInvariant()}
        }
    }
    $manifest | ConvertTo-Json -Depth 30 | Set-Content handoff_manifest.json -Encoding utf8NoBOM
} finally { Pop-Location }
