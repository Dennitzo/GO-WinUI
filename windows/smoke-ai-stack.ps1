#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $DataRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'GO-AI-Stack'),
    [string] $ModelRoot,
    [string] $LmStudioModelRoot,
    [string] $ServerUrl = 'http://192.168.0.67:8080',
    [ValidateRange(1, 120)] [int] $WaitMinutes = 10,
    [switch] $IncludeInference
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$paths = Get-GoAiStackDefaults -DataRoot $DataRoot -ModelRoot $ModelRoot -LmStudioModelRoot $LmStudioModelRoot
if (-not (Test-Path -LiteralPath $paths.EnvironmentFile -PathType Leaf)) { Write-GoAiStackEnvironment -Paths $paths }

$lmStudio = Invoke-RestMethod -Uri 'http://127.0.0.1:1234/api/v1/models' -Method Get -TimeoutSec 10
foreach ($requiredModel in @('gpt-oss-120b', 'qwen3.8-27b')) {
    if (-not @($lmStudio.models | Where-Object { $_.key -eq $requiredModel })) {
        throw "Required LM Studio model is not installed: $requiredModel"
    }
}

$deadline = [DateTimeOffset]::UtcNow.AddMinutes($WaitMinutes)
$health = $null
do {
    try {
        $health = Invoke-RestMethod -Uri ($ServerUrl.TrimEnd('/') + '/v1/health/live') -TimeoutSec 5
        break
    }
    catch {
        Start-Sleep -Seconds 3
    }
} while ([DateTimeOffset]::UtcNow -lt $deadline)
if ($null -eq $health -or $health.status -ne 'live') { throw "Gateway did not become live at $ServerUrl." }

$ready = Invoke-RestMethod -Uri ($ServerUrl.TrimEnd('/') + '/v1/health/ready') -TimeoutSec 30
$capabilities = Invoke-RestMethod -Uri ($ServerUrl.TrimEnd('/') + '/v1/capabilities') -TimeoutSec 30
if ($capabilities.protocolVersion -ne '1.0') { throw 'Unexpected GO protocol version.' }

$docker = Resolve-GoDockerCommand
$published = @(& $docker compose --env-file $paths.EnvironmentFile -f $paths.ComposeFile ps --format json | ConvertFrom-Json)
$publicServices = @($published | Where-Object {
    @($_.Publishers | Where-Object { [int]$_.PublishedPort -gt 0 }).Count -gt 0
})
if ($publicServices.Count -ne 1 -or $publicServices[0].Service -ne 'caddy') {
    throw 'Only the Caddy service may publish a host port.'
}

if ($IncludeInference) {
    Invoke-GoDotNet -CommandArguments @(
        'run', '--project', (Resolve-GoRepositoryPath -RelativePath 'src\GoAi.SmokeClient\GoAi.SmokeClient.csproj'),
        '--configuration', 'Release', '--', 'run', '--server', $ServerUrl,
        '--mode', 'General', '--prompt', 'Antworte nur mit: GO AI bereit.'
    )
    $ready = Invoke-RestMethod -Uri ($ServerUrl.TrimEnd('/') + '/v1/health/ready') -TimeoutSec 30
}
Write-Host "GO AI hybrid stack smoke passed. Readiness: $($ready.status)" -ForegroundColor Green
