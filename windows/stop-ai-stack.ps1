#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $DataRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'GO-AI-Stack'),
    [string] $ModelRoot
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$paths = Get-GoAiStackDefaults -DataRoot $DataRoot -ModelRoot $ModelRoot
if (-not (Test-Path -LiteralPath $paths.EnvironmentFile -PathType Leaf)) {
    Write-GoAiStackEnvironment -Paths $paths
}
Invoke-GoAiCompose -Paths $paths -Arguments @('down', '--remove-orphans')
Write-Host 'GO AI stack stopped; all container GPU allocations were released.' -ForegroundColor Green
