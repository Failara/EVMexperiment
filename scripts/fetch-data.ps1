param([string]$Rpc = 'https://ethereum-rpc.publicnode.com', [long]$BlockFrom = 20000000, [long]$BlockTo = 20000019)
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    dotnet src/EvmMatching.Runner/bin/Release/net10.0/EvmMatching.Runner.dll fetch $Rpc $BlockFrom $BlockTo
    if ($LASTEXITCODE -ne 0) { throw 'Download failed. Saved raw responses were preserved.' }
} finally { Pop-Location }
