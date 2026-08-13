#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$testProject = Resolve-GoRepositoryPath -RelativePath 'tests\GoAi.Server.Tests\GoAi.Server.Tests.csproj'
$smokeProject = Resolve-GoRepositoryPath -RelativePath 'src\GoAi.SmokeClient\GoAi.SmokeClient.csproj'
Invoke-GoDotNet -CommandArguments @(
    'test', $testProject,
    '--configuration', $Configuration,
    '--framework', 'net10.0-windows10.0.19041.0',
    '-p:Platform=x64',
    '--nologo'
)
Invoke-GoDotNet -CommandArguments @(
    'build', $smokeProject,
    '--configuration', $Configuration,
    '--nologo'
)

$eventSchema = Resolve-GoRepositoryPath -RelativePath 'openapi\run-events-v1.schema.json'
$openApi = Resolve-GoRepositoryPath -RelativePath 'openapi\go-ai-v1.yaml'
Get-Content -LiteralPath $eventSchema -Raw -Encoding utf8 | ConvertFrom-Json | Out-Null
$openApiText = Get-Content -LiteralPath $openApi -Raw -Encoding utf8
foreach ($required in @('openapi: 3.1.0', '/v1/runs:', '/v1/audio/transcriptions:', '/v1/images/generations:', 'X-GO-AI-Key')) {
    if ($openApiText.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "OpenAPI validation failed; missing token: $required"
    }
}

Write-Host 'GO AI Server tests completed.' -ForegroundColor Green
