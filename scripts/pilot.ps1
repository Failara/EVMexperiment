param([string]$Plan = 'configs/pilot-plan-v2.json')
& "$PSScriptRoot/run-series.ps1" -Plan $Plan
