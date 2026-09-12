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
. (Join-Path $PSScriptRoot 'common.ps1')

$paths = Get-GoAiStackDefaults -DataRoot $DataRoot -ModelRoot $ModelRoot -NativeModelRoot $NativeModelRoot
Write-GoAiStackEnvironment -Paths $paths -ServerIp $ServerIp -ImageVersion $ImageVersion

Invoke-GoDotNet -CommandArguments @(
    'test', (Resolve-GoRepositoryPath -RelativePath 'tests\GoAi.Server.Tests\GoAi.Server.Tests.csproj'),
    '--configuration', 'Release', '--nologo'
)
Invoke-GoAiCompose -Paths $paths -Arguments @('config', '--quiet')
$arguments = @('build')
if ($Pull) { $arguments += '--pull' }
Invoke-GoAiCompose -Paths $paths -Arguments $arguments
Write-Host 'GO AI Docker gateway and worker images were built successfully. Language, vision and embedding models use the native Windows runtime.' -ForegroundColor Green
