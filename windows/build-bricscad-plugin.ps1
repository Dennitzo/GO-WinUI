#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $BricsCadInstallDir,

    [string] $OutputDirectory
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$installDirectory = Resolve-GoBricsCadInstallDirectory -RequestedPath $BricsCadInstallDir
$project = Resolve-GoRepositoryPath -RelativePath 'src\GoWinUI.BricsCad.Plugin\GoWinUI.BricsCad.Plugin.csproj'
$pluginReadme = Resolve-GoRepositoryPath -RelativePath 'src\GoWinUI.BricsCad.Plugin\README.md'
$contract = Resolve-GoRepositoryPath -RelativePath 'contracts\bricscad-dotnet-tools-v2.json'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Resolve-GoRepositoryPath -RelativePath 'artifacts\windows\bricscad-v26'
}
$OutputDirectory = Reset-GoArtifactDirectory -Path $OutputDirectory
$buildId = Get-GoBuildId
$builtAt = Get-GoBuiltAt

$properties = @(
    '-p:BuildBricsCadPlugin=true',
    ("-p:BricsCadInstallDir={0}" -f $installDirectory),
    ("-p:GoBuildId={0}" -f $buildId),
    ("-p:GoBuiltAt={0}" -f $builtAt),
    '-p:Platform=x64'
)
Invoke-GoDotNet -CommandArguments (@('restore', $project, '--nologo') + $properties)
Invoke-GoDotNet -CommandArguments (@(
    'build', $project,
    '--configuration', $Configuration,
    '--no-restore',
    '--output', $OutputDirectory,
    '--nologo'
) + $properties)

$pluginAssembly = Join-Path $OutputDirectory 'GOBricsCad.dll'
$protocolAssembly = Join-Path $OutputDirectory 'GoWinUI.BricsCad.Protocol.dll'
if (-not (Test-Path -LiteralPath $pluginAssembly -PathType Leaf) -or
    -not (Test-Path -LiteralPath $protocolAssembly -PathType Leaf)) {
    throw 'BricsCAD artifact is incomplete; plugin or protocol assembly is missing.'
}

foreach ($hostAssembly in @('BrxMgd.dll', 'TD_Mgd.dll', 'TD_MgdBrep.dll', 'TD_MgdDbConstraints.dll')) {
    if (Test-Path -LiteralPath (Join-Path $OutputDirectory $hostAssembly)) {
        throw "BricsCAD host assembly must not be redistributed: $hostAssembly"
    }
}

Copy-Item -LiteralPath $contract -Destination (Join-Path $OutputDirectory 'bricscad-dotnet-tools-v2.json') -Force
Copy-Item -LiteralPath $pluginReadme -Destination (Join-Path $OutputDirectory 'README.md') -Force
$manifest = [ordered]@{
    schema = 'go.bricscad.plugin.artifact.v1'
    product = 'GO BricsCAD V26 .NET Plugin'
    targetFramework = 'net8.0-windows'
    platform = 'x64'
    buildId = $buildId
    builtAtUtc = $builtAt
    protocol = 4
    bridgeBuild = 'bridge-json-v4'
    contractVersion = 'bricscad-dotnet-tools-v2'
    contractSha256 = Get-GoContractHash
    entryAssembly = 'GOBricsCad.dll'
    pingCommand = 'GOPING'
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'plugin-manifest.json') -Encoding UTF8

$zipPath = Resolve-GoRepositoryPath -RelativePath ("artifacts\windows\GO-BricsCAD-V26-{0}.zip" -f $buildId)
$zipPath = Assert-GoArtifactPath -Path $zipPath
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -Path (Join-Path $OutputDirectory '*') -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host "BricsCAD V26 plugin artifact: $OutputDirectory" -ForegroundColor Green
Write-Host "BricsCAD V26 plugin archive:  $zipPath" -ForegroundColor Green
