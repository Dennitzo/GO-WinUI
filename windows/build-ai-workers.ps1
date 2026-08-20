#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('all', 'media', 'speech', 'image')]
    [string[]] $Worker = @('all'),

    [string] $ImageVersion = '1.0.0',

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$')]
    [string] $BuilderName = 'goai-builder',

    [switch] $NoPull
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

& (Join-Path $PSScriptRoot 'validate-ai-server-compose.ps1')
$docker = Resolve-GoDockerCommand
$null = & $docker buildx inspect $BuilderName 2>$null
if ($LASTEXITCODE -ne 0) {
    & $docker buildx create --name $BuilderName --driver docker-container | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Docker Buildx builder '$BuilderName' could not be created."
    }
}
& $docker buildx inspect $BuilderName --bootstrap | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Docker Buildx builder '$BuilderName' is not ready."
}
$compose = Resolve-GoRepositoryPath -RelativePath 'deploy\go-ai\compose.yaml'
$validationRoot = Resolve-GoRepositoryPath -RelativePath 'artifacts\go-ai-server\compose-validation'
$previousDataRoot = $env:GO_AI_DATA_ROOT
$previousImageVersion = $env:GO_AI_IMAGE_VERSION
try {
    $env:GO_AI_DATA_ROOT = $validationRoot.Replace('\', '/')
    $env:GO_AI_IMAGE_VERSION = $ImageVersion
    $targets = if ($Worker -contains 'all') { @('media', 'speech', 'image') } else { $Worker }
    $arguments = @(
        'compose', '--file', $compose, 'build',
        '--builder', $BuilderName,
        '--provenance=false',
        '--sbom=false'
    )
    if (-not $NoPull) {
        $arguments += '--pull'
    }
    $arguments += $targets
    & $docker @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "GO AI worker image build failed with exit code $LASTEXITCODE."
    }
}
finally {
    $env:GO_AI_DATA_ROOT = $previousDataRoot
    $env:GO_AI_IMAGE_VERSION = $previousImageVersion
}

Write-Host "GO AI worker images built with version $ImageVersion." -ForegroundColor Green
