#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [switch] $NoRestore
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$testProject = Resolve-GoRepositoryPath -RelativePath 'tests\GoWinUI.Tests\GoWinUI.Tests.csproj'
if (-not $NoRestore) {
    Invoke-GoDotNet -CommandArguments @('restore', $testProject, '--nologo')
}
Invoke-GoDotNet -CommandArguments @(
    'test', $testProject,
    '--configuration', $Configuration,
    '--no-restore',
    '--nologo',
    '--logger', 'console;verbosity=normal'
)
Write-Host 'Tests passed.' -ForegroundColor Green