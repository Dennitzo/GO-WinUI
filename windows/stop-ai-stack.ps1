#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $DataRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'GO-AI-Stack'),
    [string] $ModelRoot,
    [string] $LmStudioModelRoot
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$paths = Get-GoAiStackDefaults -DataRoot $DataRoot -ModelRoot $ModelRoot -LmStudioModelRoot $LmStudioModelRoot
if (-not (Test-Path -LiteralPath $paths.EnvironmentFile -PathType Leaf)) {
    Write-GoAiStackEnvironment -Paths $paths
}
Invoke-GoAiCompose -Paths $paths -Arguments @('down', '--remove-orphans')

$managedModels = @(
    'gpt-oss-120b',
    'qwen/qwen3.8-27b',
    'qwen3-coder-next',
    'qwen3-vl-30b-a3b-instruct',
    'text-embedding-bge-m3'
)
try {
    $catalog = Invoke-RestMethod -Uri 'http://127.0.0.1:1234/api/v1/models' -Method Get -TimeoutSec 5
    foreach ($model in @($catalog.models | Where-Object { $_.key -in $managedModels })) {
        foreach ($instance in @($model.loaded_instances)) {
            if (-not [string]::IsNullOrWhiteSpace([string]$instance.id)) {
                $body = @{ instance_id = [string]$instance.id } | ConvertTo-Json -Compress
                Invoke-RestMethod -Uri 'http://127.0.0.1:1234/api/v1/models/unload' -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 120 | Out-Null
            }
        }
    }
}
catch {
    Write-Warning "GO models could not be unloaded from LM Studio: $($_.Exception.Message)"
}

Write-Host 'GO AI stack stopped; Docker workers and GO-managed LM Studio models released their GPU allocations. The LM Studio server remains available.' -ForegroundColor Green
