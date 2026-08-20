#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $GatewayPath = (Join-Path $PSScriptRoot '..\artifacts\go-ai-server\candidate\win-x64\gateway\GoAi.Gateway.exe'),

    [string] $SmokeClientPath = (Join-Path $PSScriptRoot '..\artifacts\go-ai-server\candidate\win-x64\smoke-client\win-x64\GoAi.SmokeClient.exe'),

    [string] $ProviderDataRoot = 'C:\ProgramData\GO-AI-Server',

    [ValidateRange(1024, 65535)]
    [int] $GatewayPort = 7090
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$GatewayPath = [IO.Path]::GetFullPath($GatewayPath)
$SmokeClientPath = [IO.Path]::GetFullPath($SmokeClientPath)
$ProviderDataRoot = [IO.Path]::GetFullPath($ProviderDataRoot)
foreach ($requiredFile in @(
    $GatewayPath,
    $SmokeClientPath,
    (Join-Path $ProviderDataRoot 'Caddy\data\caddy\pki\authorities\local\root.crt')
)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Contained live-smoke prerequisite is missing: $requiredFile"
    }
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$testRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ("GO-AI-Contained-Live-{0}" -f $stamp)))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$stdout = Join-Path $testRoot 'gateway.out.log'
$stderr = Join-Path $testRoot 'gateway.err.log'
$serverUrl = "http://127.0.0.1:$GatewayPort"
$environmentNames = @(
    'GO_AI_DATA_DIRECTORY',
    'GO_AI_PROVIDER_DATA_DIRECTORY',
    'GO_AI_WORKER_DATA_DIRECTORY',
    'GO_AI_GATEWAY_PORT',
    'GO_AI_EXPECTED_LAN_IP',
    'GO_AI_PUBLIC_URL'
)
$savedEnvironment = @{}
foreach ($name in $environmentNames) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

$gateway = $null
try {
    $env:GO_AI_DATA_DIRECTORY = $testRoot
    $env:GO_AI_PROVIDER_DATA_DIRECTORY = $ProviderDataRoot
    $env:GO_AI_WORKER_DATA_DIRECTORY = $ProviderDataRoot
    $env:GO_AI_GATEWAY_PORT = [string]$GatewayPort
    $env:GO_AI_EXPECTED_LAN_IP = '192.168.0.67'
    $env:GO_AI_PUBLIC_URL = $serverUrl

    $gateway = Start-Process `
        -FilePath $GatewayPath `
        -PassThru `
        -WindowStyle Hidden `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr

    $live = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if ($gateway.HasExited) {
            throw "Contained gateway exited early with code $($gateway.ExitCode)."
        }
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri "$serverUrl/v1/health/live" -TimeoutSec 2
            if ([int]$response.StatusCode -eq 200) {
                $live = $true
                break
            }
        }
        catch {
            # Startup polling is expected to fail until Kestrel is listening.
        }
        Start-Sleep -Milliseconds 500
    }
    if (-not $live) {
        throw 'Contained gateway did not become live.'
    }

    $ready = $false
    $lastReadiness = 'Noch keine Readiness-Antwort empfangen.'
    for ($attempt = 0; $attempt -lt 120; $attempt++) {
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri "$serverUrl/v1/health/ready" -TimeoutSec 5
            $lastReadiness = $response.Content
            if ([int]$response.StatusCode -eq 200) {
                $ready = $true
                break
            }
        }
        catch {
            if ($null -ne $_.Exception.Response) {
                $reader = New-Object IO.StreamReader $_.Exception.Response.GetResponseStream()
                try {
                    $lastReadiness = $reader.ReadToEnd()
                }
                finally {
                    $reader.Dispose()
                }
            }
            else {
                $lastReadiness = $_.Exception.Message
            }
        }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) {
        throw "Contained gateway did not become ready: $lastReadiness"
    }

    $smokeArguments = @{
        DataRoot = $testRoot
        ServerUrl = $serverUrl
        ApiKeyPath = (Join-Path $testRoot 'Secrets\bootstrap-client-key.once')
        RootCertificatePath = (Join-Path $ProviderDataRoot 'Caddy\data\caddy\pki\authorities\local\root.crt')
        SmokeClientPath = $SmokeClientPath
        OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\go-ai-server\live-smoke-contained')
    }
    & (Join-Path $PSScriptRoot 'live-smoke-ai-server.ps1') @smokeArguments
}
finally {
    if ($null -ne $gateway -and -not $gateway.HasExited) {
        # Windows PowerShell 5.1 exposes Process.Kill() without the newer
        # descendant-process overload.
        $gateway.Kill()
        $gateway.WaitForExit()
    }
    foreach ($entry in $savedEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }

    Write-Output ("CONTAINED_ROOT={0}" -f $testRoot)
    if (Test-Path -LiteralPath $stdout -PathType Leaf) {
        Get-Content -LiteralPath $stdout -Tail 100
    }
    if (Test-Path -LiteralPath $stderr -PathType Leaf) {
        Get-Content -LiteralPath $stderr -Tail 100
    }
}
