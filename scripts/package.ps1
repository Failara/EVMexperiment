$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    & "$PSScriptRoot/handoff.ps1"
    Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
    $archivePath = Join-Path $PWD 'artifacts/EvmAddressExperiment.zip'
    New-Item -ItemType Directory -Force (Split-Path $archivePath -Parent) | Out-Null
    if (Test-Path -LiteralPath $archivePath) { throw 'Archive exists; move it aside before packaging again.' }
    $manifest = Get-Content handoff_manifest.json -Raw | ConvertFrom-Json
    $paths = @($manifest.files.path) + @('handoff_manifest.json')
    $archive = [IO.Compression.ZipArchive]::new([IO.File]::Create($archivePath),[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($path in $paths | Sort-Object -Unique) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $PWD $path), ('EvmAddressExperiment/' + $path.Replace('\','/')), [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $archive.Dispose() }
    Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
} finally { Pop-Location }
