#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [switch] $SkipTests,

    [switch] $SkipPublish,

    [Alias('WithBricsCadPlugin')]
    [switch] $IncludeBricsCadPlugin,

    [string] $BricsCadInstallDir
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$appProject = Resolve-GoRepositoryPath -RelativePath 'src\GoWinUI.App\GoWinUI.App.csproj'
$protocolProject = Resolve-GoRepositoryPath -RelativePath 'src\GoWinUI.BricsCad.Protocol\GoWinUI.BricsCad.Protocol.csproj'

Invoke-GoDotNet -CommandArguments @(
    'restore', $appProject,
    '--runtime', 'win-x64',
    ("-p:Configuration={0}" -f $Configuration),
    '-p:Platform=x64',
    '--nologo'
)
Invoke-GoDotNet -CommandArguments @(
    'build', $appProject,
    '--configuration', $Configuration,
    '--no-restore',
    '-p:Platform=x64',
    '-p:RuntimeIdentifier=win-x64',
    '--nologo'
)
Invoke-GoDotNet -CommandArguments @(
    'build', $protocolProject,
    '--configuration', $Configuration,
    '--framework', 'net10.0',
    '--nologo'
)

if (-not $SkipTests) {
    & (Join-Path $PSScriptRoot 'test.ps1') -Configuration $Configuration
}
if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot 'publish.ps1') `
        -Mode SingleFile `
        -RuntimeIdentifier win-x64 `
        -OutputDirectory (Resolve-GoRepositoryPath -RelativePath 'artifacts\portable\win-x64')
}
if ($IncludeBricsCadPlugin) {
    & (Join-Path $PSScriptRoot 'build-bricscad-plugin.ps1') `
        -Configuration $Configuration `
        -BricsCadInstallDir $BricsCadInstallDir
}

Write-Host 'GO build completed.' -ForegroundColor Green
