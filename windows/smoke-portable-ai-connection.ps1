#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $PublishDirectory,
    [string] $ServerUrl = 'http://192.168.0.67:8080',
    [string] $GatewayContainer = 'go-ai-gateway',
    [int] $WaitSeconds = 20
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    $PublishDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\portable\win-x64'
}

$publishRoot = [IO.Path]::GetFullPath($PublishDirectory)
$executable = Join-Path $publishRoot 'GO.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Portable GO executable is missing: $executable"
}
$serverUri = $null
$hasServerUri = [Uri]::TryCreate($ServerUrl.TrimEnd('/') + '/', [UriKind]::Absolute, [ref]$serverUri)
if (-not $hasServerUri) {
    throw "Invalid Docker gateway URL: $ServerUrl"
}
if ($serverUri.Scheme -notin @('http', 'https')) {
    throw "Invalid Docker gateway URL: $ServerUrl"
}

$live = Invoke-RestMethod -Uri ("{0}v1/health/live" -f $serverUri) -TimeoutSec 10
if ($live.protocolVersion -ne '1.0') {
    throw "Unexpected gateway protocol: $($live.protocolVersion)"
}

$smokeBase = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'GO-Portable-Connection-Smoke'))
$smokeRoot = [IO.Path]::GetFullPath((Join-Path $smokeBase ([Guid]::NewGuid().ToString('N'))))
if (-not [string]::Equals([IO.Path]::GetDirectoryName($smokeRoot), $smokeBase, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe portable connection smoke path: $smokeRoot"
}
New-Item -ItemType Directory -Path $smokeRoot -Force | Out-Null

$previousDataDirectory = $env:GO_DATA_DIRECTORY
$previousBridgeDirectory = $env:GO_BRIDGE_DIRECTORY
$previousInstanceKey = $env:GO_SMOKE_INSTANCE_KEY
$env:GO_DATA_DIRECTORY = Join-Path $smokeRoot 'Data'
$env:GO_BRIDGE_DIRECTORY = Join-Path $smokeRoot 'Bridge'
$env:GO_SMOKE_INSTANCE_KEY = [Guid]::NewGuid().ToString('N')
$startedAt = [DateTimeOffset]::UtcNow.ToString('o')
$process = $null
try {
    $process = Start-Process -FilePath $executable -PassThru -WindowStyle Hidden
    $settingsPath = Join-Path $env:GO_DATA_DIRECTORY 'settings.json'
    $deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
    $settings = $null
    while ([DateTime]::UtcNow -lt $deadline -and -not $process.HasExited) {
        if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
            $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
            if ($settings.version -eq 15 -and $settings.isAiConnectionEnabled -eq $true) {
                break
            }
        }
        Start-Sleep -Milliseconds 250
    }

    if ($process.HasExited) {
        throw "Portable GO exited during startup with code $($process.ExitCode)."
    }
    if ($null -eq $settings) {
        throw 'Portable GO did not persist its initial settings.'
    }
    if ($settings.version -ne 15 -or $settings.isAiConnectionEnabled -ne $true) {
        throw 'Portable GO did not start in online connection mode.'
    }
    if (-not [string]::Equals($settings.goAiServerUrl, $serverUri.ToString().TrimEnd('/'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Portable GO used an unexpected gateway URL: $($settings.goAiServerUrl)"
    }

    Start-Sleep -Seconds 5
    if (Get-Command docker -ErrorAction SilentlyContinue) {
        $container = docker ps --filter "name=^/${GatewayContainer}$" --format '{{.Names}}'
        if ($container -eq $GatewayContainer) {
            $gatewayLog = docker logs --since $startedAt $GatewayContainer 2>&1 | Out-String
            if ($gatewayLog -notmatch '/v1/capabilities') {
                throw 'Portable GO did not initiate a capability request at the Docker gateway.'
            }
            if ($gatewayLog -notmatch '/v1/health/ready') {
                throw 'Portable GO did not initiate a readiness request at the Docker gateway.'
            }
        }
    }

    Write-Host "Portable GO initiated its Docker connection: $($settings.goAiServerUrl)" -ForegroundColor Green
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        if (-not $process.CloseMainWindow() -or -not $process.WaitForExit(5000)) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            $process.WaitForExit(5000) | Out-Null
        }
    }
    $env:GO_DATA_DIRECTORY = $previousDataDirectory
    $env:GO_BRIDGE_DIRECTORY = $previousBridgeDirectory
    $env:GO_SMOKE_INSTANCE_KEY = $previousInstanceKey
    $resolvedSmokeRoot = [IO.Path]::GetFullPath($smokeRoot)
    if (-not [string]::Equals([IO.Path]::GetDirectoryName($resolvedSmokeRoot), $smokeBase, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing unsafe smoke cleanup: $resolvedSmokeRoot"
    }
    if (Test-Path -LiteralPath $resolvedSmokeRoot) {
        Remove-Item -LiteralPath $resolvedSmokeRoot -Recurse -Force
    }
}
