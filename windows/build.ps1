#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [switch] $SkipTests,

    [switch] $SkipPublish
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$appProject = Resolve-GoRepositoryPath -RelativePath 'src\GoWinUI.App\GoWinUI.App.csproj'

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
if (-not $SkipTests) {
    & (Join-Path $PSScriptRoot 'test.ps1') -Configuration $Configuration
    # Includes DeepSeek integrated-vision routing and native projector/preset tests.
    & (Join-Path $PSScriptRoot 'test-agent-context.ps1') -Configuration $Configuration -SkipClientTests
}
if (-not $SkipPublish) {
    # Publish smoke verifies the bundled native catalog against current sources,
    # including DeepSeek vision support; stale runtime assets fail the build.
    & (Join-Path $PSScriptRoot 'publish.ps1') `
        -Mode SingleFile `
        -RuntimeIdentifier win-x64 `
        -OutputDirectory (Resolve-GoRepositoryPath -RelativePath 'artifacts\portable\win-x64')
}
Write-Host 'GO build completed.' -ForegroundColor Green
