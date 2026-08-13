#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PublishDirectory,

    [string] $ManifestPath,

    [ValidateSet('Folder', 'SingleFile')]
    [string] $Mode = 'Folder',

    [switch] $LiveModels,

    [string] $DataRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'GO-AI-Server'),

    [string] $ServerUrl = 'https://192.168.0.67:8443',

    [string] $ApiKeyPath
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$PublishDirectory = Assert-GoArtifactPath -Path $PublishDirectory
$executable = Join-Path $PublishDirectory 'GO-AI-Server.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Smoke check failed; executable is missing: $executable"
}

if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = $PublishDirectory + '.manifest.json'
}
$ManifestPath = Assert-GoArtifactPath -Path $ManifestPath
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
if ($manifest.schema -ne 'go.ai.server.windows.publish.v1' -or $manifest.mode -ne $Mode) {
    throw 'Smoke check failed; server publish manifest is invalid.'
}
if ((Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.executableSha256) {
    throw 'Smoke check failed; server executable hash differs from the manifest.'
}
$gatewayExecutable = Join-Path $PublishDirectory 'gateway\GoAi.Gateway.exe'
if (-not (Test-Path -LiteralPath $gatewayExecutable -PathType Leaf)) {
    throw "Smoke check failed; gateway executable is missing: $gatewayExecutable"
}
if ((Get-FileHash -LiteralPath $gatewayExecutable -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.gatewayExecutableSha256) {
    throw 'Smoke check failed; gateway executable hash differs from the manifest.'
}
$requiredDeploymentFiles = @(
    'GO-AI-SERVER.md',
    'scripts\deploy-ai-server.ps1',
    'scripts\start-lmstudio-server.ps1',
    'scripts\refresh-lmstudio-model-catalog.ps1',
    'deploy\go-ai\compose.yaml',
    'deploy\go-ai\caddy\Caddyfile'
)
foreach ($relativePath in $requiredDeploymentFiles) {
    $requiredPath = Join-Path $PublishDirectory $relativePath
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Smoke check failed; deployment file is missing: $requiredPath"
    }
}

$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()

$smokeBase = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'GO-AI-Server-Smoke'))
$smokeRoot = [IO.Path]::GetFullPath((Join-Path $smokeBase ([Guid]::NewGuid().ToString('N'))))
if (-not [string]::Equals([IO.Path]::GetDirectoryName($smokeRoot), $smokeBase, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe smoke directory: $smokeRoot"
}
New-Item -ItemType Directory -Path $smokeRoot -Force | Out-Null

$previousDataDirectory = $env:GO_AI_DATA_DIRECTORY
$previousGatewayPort = $env:GO_AI_GATEWAY_PORT
$previousInstanceKey = $env:GO_AI_SMOKE_INSTANCE_KEY
$previousExpectedIp = $env:GO_AI_EXPECTED_LAN_IP
$env:GO_AI_DATA_DIRECTORY = Join-Path $smokeRoot 'Data'
$env:GO_AI_GATEWAY_PORT = [string]$port
$env:GO_AI_SMOKE_INSTANCE_KEY = [Guid]::NewGuid().ToString('N')
$env:GO_AI_EXPECTED_LAN_IP = '192.168.0.67'
$process = $null
try {
    $process = Start-Process -FilePath $gatewayExecutable -PassThru -WindowStyle Hidden
    $liveUri = "http://127.0.0.1:$port/v1/health/live"
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    $live = $null
    while ([DateTime]::UtcNow -lt $deadline -and -not $process.HasExited) {
        try {
            $live = Invoke-RestMethod -Method Get -Uri $liveUri -TimeoutSec 2
            if ($live.status -eq 'live') {
                break
            }
        }
        catch {
            Start-Sleep -Milliseconds 250
        }
    }

    if ($process.HasExited) {
        throw "Runtime smoke failed; GO-AI-Server exited early with code $($process.ExitCode)."
    }
    if ($null -eq $live -or $live.status -ne 'live' -or $live.protocolVersion -ne '1.0') {
        throw "Runtime smoke failed; live endpoint was not ready: $liveUri"
    }
    $database = Join-Path $env:GO_AI_DATA_DIRECTORY 'Data\go-ai-server.db'
    if (-not (Test-Path -LiteralPath $database -PathType Leaf)) {
        throw "Runtime smoke failed; SQLite database is missing: $database"
    }

    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    $process.WaitForExit(5000) | Out-Null
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $process.WaitForExit(5000) | Out-Null
    }
    $env:GO_AI_DATA_DIRECTORY = $previousDataDirectory
    $env:GO_AI_GATEWAY_PORT = $previousGatewayPort
    $env:GO_AI_SMOKE_INSTANCE_KEY = $previousInstanceKey
    $env:GO_AI_EXPECTED_LAN_IP = $previousExpectedIp
    if (Test-Path -LiteralPath $smokeRoot) {
        for ($attempt = 1; $attempt -le 20; $attempt++) {
            try {
                Remove-Item -LiteralPath $smokeRoot -Recurse -Force -ErrorAction Stop
                break
            }
            catch {
                if ($attempt -eq 20) { throw }
                Start-Sleep -Milliseconds 250
            }
        }
    }
}

Write-Host "GO AI Server smoke checks passed: $PublishDirectory" -ForegroundColor Green
if ($LiveModels) {
    $liveArguments = @{
        DataRoot = $DataRoot
        ServerUrl = $ServerUrl
        SmokeClientPath = (Join-Path $PublishDirectory 'smoke-client\win-x64\GoAi.SmokeClient.exe')
    }
    if (-not [string]::IsNullOrWhiteSpace($ApiKeyPath)) {
        $liveArguments.ApiKeyPath = $ApiKeyPath
    }
    & (Join-Path $PSScriptRoot 'live-smoke-ai-server.ps1') @liveArguments
}
