#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [switch] $SkipTests,

    [switch] $SkipPublish,

    [switch] $SkipDocker
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$serverProject = Resolve-GoRepositoryPath -RelativePath 'src\GoAi.Server.App\GoAi.Server.App.csproj'
$gatewayProject = Resolve-GoRepositoryPath -RelativePath 'src\GoAi.Gateway\GoAi.Gateway.csproj'
$clientProject = Resolve-GoRepositoryPath -RelativePath 'src\GoAi.Client\GoAi.Client.csproj'

Invoke-GoDotNet -CommandArguments @(
    'restore', $serverProject,
    '--runtime', 'win-x64',
    ("-p:Configuration={0}" -f $Configuration),
    '-p:Platform=x64',
    '--nologo'
)
Invoke-GoDotNet -CommandArguments @(
    'build', $gatewayProject,
    '--configuration', $Configuration,
    '--runtime', 'win-x64',
    '--nologo'
)
Invoke-GoDotNet -CommandArguments @(
    'build', $serverProject,
    '--configuration', $Configuration,
    '--no-restore',
    '-p:Platform=x64',
    '-p:RuntimeIdentifier=win-x64',
    '--nologo'
)
Invoke-GoDotNet -CommandArguments @(
    'build', $clientProject,
    '--configuration', $Configuration,
    '--nologo'
)

if (-not $SkipTests) {
    & (Join-Path $PSScriptRoot 'test-ai-server.ps1') -Configuration $Configuration
}
if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot 'publish-ai-server.ps1') `
        -Mode SingleFile `
        -RuntimeIdentifier win-x64 `
        -OutputDirectory (Resolve-GoRepositoryPath -RelativePath 'artifacts\go-ai-server\portable\win-x64')
}
if (-not $SkipDocker) {
    $dockerScript = Join-Path $PSScriptRoot 'build-ai-workers.ps1'
    if (Test-Path -LiteralPath $dockerScript -PathType Leaf) {
        & $dockerScript
    }
}

Write-Host 'GO AI Server build completed.' -ForegroundColor Green
