#requires -Version 5.1
[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$docker = Resolve-GoDockerCommand
$validationRoot = Reset-GoArtifactDirectory -Path (Resolve-GoRepositoryPath -RelativePath 'artifacts\go-ai-server\compose-validation')
$directories = @(
    'Artifacts\worker', 'Cache\searxng',
    'Caddy\config', 'Caddy\config\caddy', 'Caddy\data', 'Caddy\data\caddy', 'Config\searxng',
    'Models', 'Secrets', 'Uploads', 'Voices'
)
foreach ($relative in $directories) {
    New-Item -ItemType Directory -Path (Join-Path $validationRoot $relative) -Force | Out-Null
}

foreach ($worker in @('speech', 'media', 'image')) {
    New-GoRandomSecret | Set-Content -LiteralPath (Join-Path $validationRoot ("Secrets\{0}-worker.key" -f $worker)) -Encoding ascii -NoNewline
}
$template = Get-Content -LiteralPath (Resolve-GoRepositoryPath -RelativePath 'deploy\go-ai\searxng\settings.yml.template') -Raw -Encoding utf8
$template.Replace('__SEARXNG_SECRET__', (New-GoRandomSecret)) |
    Set-Content -LiteralPath (Join-Path $validationRoot 'Config\searxng\settings.yml') -Encoding utf8

$previousDataRoot = $env:GO_AI_DATA_ROOT
$previousImageVersion = $env:GO_AI_IMAGE_VERSION
$previousExpectedLanIp = $env:GO_AI_EXPECTED_LAN_IP
try {
    $env:GO_AI_DATA_ROOT = $validationRoot.Replace('\', '/')
    $env:GO_AI_IMAGE_VERSION = 'validation'
    $env:GO_AI_EXPECTED_LAN_IP = '192.168.0.67'
    $compose = Resolve-GoRepositoryPath -RelativePath 'deploy\go-ai\compose.yaml'
    & $docker compose --file $compose config --quiet
    if ($LASTEXITCODE -ne 0) {
        throw "Docker Compose validation failed with exit code $LASTEXITCODE."
    }
}
finally {
    $env:GO_AI_DATA_ROOT = $previousDataRoot
    $env:GO_AI_IMAGE_VERSION = $previousImageVersion
    $env:GO_AI_EXPECTED_LAN_IP = $previousExpectedLanIp
}

Write-Host 'GO AI Docker Compose configuration is valid.' -ForegroundColor Green
