#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $DataRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'GO-AI-Stack'),
    [string] $ModelRoot,
    [string] $NativeModelRoot
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$paths = Get-GoAiStackDefaults -DataRoot $DataRoot -ModelRoot $ModelRoot -NativeModelRoot $NativeModelRoot
if (-not (Test-Path -LiteralPath $paths.EnvironmentFile -PathType Leaf)) {
    Write-GoAiStackEnvironment -Paths $paths
}
try {
    Invoke-GoAiCompose -Paths $paths -Arguments @('down', '--remove-orphans')
}
finally {
    # The native runtime must stop even if Docker Desktop is unavailable.
    & (Join-Path $PSScriptRoot 'manage-coding-llama.ps1') -Action Stop
}

Write-Host 'GO AI stack stopped; Docker workers and the owned native Windows model runtime released their GPU allocations.' -ForegroundColor Green
