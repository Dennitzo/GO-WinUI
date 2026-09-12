#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $DataRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'GO-AI-Stack'),
    [string] $ModelRoot,
    [Alias('CodingModelRoot')][string] $NativeModelRoot,
    [string] $ServerIp = '192.168.0.67',
    [string] $ImageVersion = '1.0.0'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$paths = Get-GoAiStackDefaults -DataRoot $DataRoot -ModelRoot $ModelRoot -NativeModelRoot $NativeModelRoot
& (Join-Path $PSScriptRoot 'manage-coding-llama.ps1') -Action Start -ModelRoot $paths.NativeModelRoot
Write-GoAiStackEnvironment -Paths $paths -ServerIp $ServerIp -ImageVersion $ImageVersion
Invoke-GoAiCompose -Paths $paths -Arguments @('up', '-d', '--remove-orphans')
Write-Host "GO AI stack is starting at http://${ServerIp}:8080; all language, vision and embedding models use native Windows llama.cpp on port 8081." -ForegroundColor Green
