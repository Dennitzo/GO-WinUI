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

$protocolProject = Resolve-GoRepositoryPath -RelativePath 'src\GoWinUI.BricsCad.Protocol\GoWinUI.BricsCad.Protocol.csproj'
$protocolSmokeProject = Resolve-GoRepositoryPath -RelativePath 'windows\.protocol-smoke\ProtocolSmoke.csproj'
$pluginProject = Resolve-GoRepositoryPath -RelativePath 'src\GoWinUI.BricsCad.Plugin\GoWinUI.BricsCad.Plugin.csproj'
$testProject = Resolve-GoRepositoryPath -RelativePath 'tests\GoWinUI.Tests\GoWinUI.Tests.csproj'

if (-not $NoRestore) {
    Invoke-GoDotNet -CommandArguments @('restore', $protocolProject, '--nologo')
    Invoke-GoDotNet -CommandArguments @('restore', $protocolSmokeProject, '--nologo')
    Invoke-GoDotNet -CommandArguments @('restore', $pluginProject, '--nologo')
    Invoke-GoDotNet -CommandArguments @('restore', $testProject, '--nologo')
}

Invoke-GoDotNet -CommandArguments @('build', $protocolProject, '--configuration', $Configuration, '--no-restore', '--nologo')
$protocolSmokeBase = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'GO-WinUI-ProtocolSmoke'))
$protocolSmokeRoot = [IO.Path]::GetFullPath((Join-Path $protocolSmokeBase ([Guid]::NewGuid().ToString('N'))))
if (-not [string]::Equals([IO.Path]::GetDirectoryName($protocolSmokeRoot), $protocolSmokeBase, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe protocol smoke directory: $protocolSmokeRoot"
}
$previousBridgeDirectory = $env:GO_BRIDGE_DIRECTORY
$env:GO_BRIDGE_DIRECTORY = $protocolSmokeRoot
try {
    Invoke-GoDotNet -CommandArguments @('run', '--project', $protocolSmokeProject, '--configuration', $Configuration, '--no-restore', '--nologo')
}
finally {
    $env:GO_BRIDGE_DIRECTORY = $previousBridgeDirectory
    if (Test-Path -LiteralPath $protocolSmokeRoot) {
        for ($attempt = 1; $attempt -le 20; $attempt++) {
            try {
                Remove-Item -LiteralPath $protocolSmokeRoot -Recurse -Force -ErrorAction Stop
                break
            }
            catch {
                if ($attempt -eq 20) {
                    throw
                }
                Start-Sleep -Milliseconds 250
            }
        }
    }
}
# The default plugin build is intentionally the dependency-free marker build.
Invoke-GoDotNet -CommandArguments @('build', $pluginProject, '--configuration', $Configuration, '--no-restore', '--nologo')
Invoke-GoDotNet -CommandArguments @(
    'test', $testProject,
    '--configuration', $Configuration,
    '--no-restore',
    '--nologo',
    '--logger', 'console;verbosity=normal'
)

$contractPath = Resolve-GoRepositoryPath -RelativePath 'contracts\bricscad-dotnet-tools-v2.json'
$contract = Get-Content -LiteralPath $contractPath -Raw | ConvertFrom-Json
if ($contract.protocol -ne 4 -or
    $contract.bridgeBuild -ne 'bridge-json-v4' -or
    $contract.contractVersion -ne 'bricscad-dotnet-tools-v2') {
    throw 'BricsCAD contract metadata is not protocol v4.'
}

Write-Host ("Tests passed. BricsCAD contract SHA-256: " + (Get-GoContractHash)) -ForegroundColor Green
