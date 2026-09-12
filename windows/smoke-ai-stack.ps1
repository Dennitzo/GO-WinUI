#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $DataRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'GO-AI-Stack'),
    [string] $ModelRoot,
    [string] $NativeModelRoot,
    [string] $ServerUrl = 'http://192.168.0.67:8080',
    [ValidateRange(1, 120)] [int] $WaitMinutes = 10,
    [switch] $IncludeInference
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$paths = Get-GoAiStackDefaults -DataRoot $DataRoot -ModelRoot $ModelRoot -NativeModelRoot $NativeModelRoot
if (-not (Test-Path -LiteralPath $paths.EnvironmentFile -PathType Leaf)) { Write-GoAiStackEnvironment -Paths $paths }

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

$nativeModels = Invoke-RestMethod -Uri ($ServerUrl.TrimEnd('/') + '/v1/models/status') -TimeoutSec 30
if (-not $nativeModels.providerReachable) { throw 'The native Unsloth / llama.cpp runtime is not reachable through the gateway.' }
foreach ($role in @('general', 'vision', 'embedding')) {
    if (@($nativeModels.models | Where-Object { $_.role -eq $role -and $_.downloaded }).Count -eq 0) {
        throw "No complete native $role model is available through the gateway."
    }
}
$codingModels = Invoke-RestMethod -Uri ($ServerUrl.TrimEnd('/') + '/v1/models/coding') -TimeoutSec 30
if (-not $codingModels.runtimeReachable -or @($codingModels.models).Count -eq 0) {
    throw 'The Coding model catalog does not contain a complete local text model.'
}

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
Write-Host "GO AI native model stack smoke passed. Readiness: $($ready.status)" -ForegroundColor Green
