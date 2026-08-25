#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $DataRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'GO-AI-Stack'),
    [string] $ModelRoot,
    [string] $ServerIp = '192.168.0.67',
    [string] $ImageVersion = '1.0.0'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$paths = Get-GoAiStackDefaults -DataRoot $DataRoot -ModelRoot $ModelRoot
Write-GoAiStackEnvironment -Paths $paths -ServerIp $ServerIp -ImageVersion $ImageVersion
Invoke-GoAiCompose -Paths $paths -Arguments @('up', '-d', '--remove-orphans')
Write-Host "GO AI stack is starting at http://${ServerIp}:8080." -ForegroundColor Green
