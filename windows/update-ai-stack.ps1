#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $DataRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'GO-AI-Stack'),
    [string] $ModelRoot,
    [string] $NativeModelRoot,
    [string] $ServerIp = '192.168.0.67',
    [string] $ImageVersion = '1.0.0',
    [switch] $Pull
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build-ai-stack.ps1') -DataRoot $DataRoot -ModelRoot $ModelRoot -NativeModelRoot $NativeModelRoot -ServerIp $ServerIp -ImageVersion $ImageVersion -Pull:$Pull
& (Join-Path $PSScriptRoot 'start-ai-stack.ps1') -DataRoot $DataRoot -ModelRoot $ModelRoot -NativeModelRoot $NativeModelRoot -ServerIp $ServerIp -ImageVersion $ImageVersion
