#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateRange(1024, 65535)]
    [int] $Port = 1234,

    [ValidateRange(60, 86400)]
    [int] $TtlSeconds = 600,

    [string] $DataRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'GO-AI-Server'),

    [switch] $SkipEstimates,

    [switch] $RequireAuthentication
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

function Set-JsonProperty {
    param(
        [Parameter(Mandatory = $true)] $Object,
        [Parameter(Mandatory = $true)] [string] $Name,
        [Parameter(Mandatory = $true)] $Value
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        $Object | Add-Member -MemberType NoteProperty -Name $Name -Value $Value
    }
    else {
        $property.Value = $Value
    }
}

function Write-JsonUtf8NoBom {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] $Value,
        [ValidateRange(2, 100)] [int] $Depth = 30
    )

    $json = $Value | ConvertTo-Json -Depth $Depth
    [IO.File]::WriteAllText(
        [IO.Path]::GetFullPath($Path),
        $json,
        [Text.UTF8Encoding]::new($false))
}

$lms = Assert-GoCommand -Name 'lms'
$previousElectronRunAsNode = $env:ELECTRON_RUN_AS_NODE
Remove-Item Env:ELECTRON_RUN_AS_NODE -ErrorAction SilentlyContinue
try {
$lmRoot = Join-Path $env:USERPROFILE '.lmstudio'
$httpConfigPath = Join-Path $lmRoot '.internal\http-server-config.json'
$settingsPath = Join-Path $lmRoot 'settings.json'
foreach ($path in @($httpConfigPath, $settingsPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "LM Studio configuration is missing: $path"
    }
}
$lmToken = Get-GoLmStudioToken -DataRoot $DataRoot
if ($RequireAuthentication -and [string]::IsNullOrWhiteSpace($lmToken)) {
    throw 'LM Studio authentication is required, but the DPAPI-protected token is missing. Store it with the GO AI Server security view first.'
}
$lmHeaders = Get-GoLmStudioHeaders -Token $lmToken

$backupRoot = Join-Path ([IO.Path]::GetFullPath($DataRoot)) ('Config\backups\lmstudio-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
Copy-Item -LiteralPath $httpConfigPath -Destination (Join-Path $backupRoot 'http-server-config.json')
Copy-Item -LiteralPath $settingsPath -Destination (Join-Path $backupRoot 'settings.json')

& $lms.Source daemon status *> $null
if ($LASTEXITCODE -ne 0) {
    & $lms.Source daemon up | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "LM Studio daemon startup failed with exit code $LASTEXITCODE."
    }
}

$serverStatus = $null
try {
    $serverStatus = & $lms.Source server status --json --quiet | ConvertFrom-Json
}
catch {
    $serverStatus = $null
}
$existingLanListener = @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue |
    Where-Object { $_.LocalAddress -in @('0.0.0.0', '::') }).Count -gt 0
$serverAlreadyOnLan = $null -ne $serverStatus `
    -and $serverStatus.running -eq $true `
    -and [int]$serverStatus.port -eq $Port `
    -and $existingLanListener

if (-not $serverAlreadyOnLan) {
    # A one-time restart is required when an older GO configuration still binds
    # LM Studio to loopback. Once the LAN listener is active, later GO starts do
    # not interrupt applications that already use LM Studio.
    & $lms.Source server stop | Out-Null
}

$httpConfig = Get-Content -LiteralPath $httpConfigPath -Raw -Encoding utf8 | ConvertFrom-Json
Set-JsonProperty $httpConfig 'autoStartOnLaunch' $true
Set-JsonProperty $httpConfig 'port' $Port
Set-JsonProperty $httpConfig 'cors' $false
Set-JsonProperty $httpConfig 'logSensitiveData' $false
Set-JsonProperty $httpConfig 'logIncomingTokens' $false
Set-JsonProperty $httpConfig 'verbose' $false
Set-JsonProperty $httpConfig 'networkInterface' '0.0.0.0'
Set-JsonProperty $httpConfig 'justInTimeModelLoading' $true
Set-JsonProperty $httpConfig 'fileLoggingMode' 'succinct'
Write-JsonUtf8NoBom -Path $httpConfigPath -Value $httpConfig -Depth 20

$settings = Get-Content -LiteralPath $settingsPath -Raw -Encoding utf8 | ConvertFrom-Json
if ($null -eq $settings.PSObject.Properties['developer']) {
    Set-JsonProperty $settings 'developer' ([PSCustomObject]@{})
}
# gpt-oss-20b remains resident while the Speech worker handles voice control,
# live captions or translation. Qwen3-Coder-Next and other heavy profiles are still
# switched explicitly and exclusively by the gateway scheduler.
Set-JsonProperty $settings.developer 'unloadPreviousJITModelOnLoad' $false
Set-JsonProperty $settings.developer 'runtimeLogVerbosityLevel' 1
Set-JsonProperty $settings.developer 'jitModelTTL' ([PSCustomObject]@{
    enabled = $true
    ttlSeconds = $TtlSeconds
})
Write-JsonUtf8NoBom -Path $settingsPath -Value $settings -Depth 30

if (-not $serverAlreadyOnLan) {
    & $lms.Source server start --port $Port --bind 0.0.0.0 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "LM Studio server start failed with exit code $LASTEXITCODE."
    }
}

# The CLI owns bind/CORS/start state and may rewrite the JSON file while starting.
# Reapply the privacy-only values after startup; the live listener is verified below.
$httpConfig = Get-Content -LiteralPath $httpConfigPath -Raw -Encoding utf8 | ConvertFrom-Json
Set-JsonProperty $httpConfig 'autoStartOnLaunch' $true
Set-JsonProperty $httpConfig 'cors' $false
Set-JsonProperty $httpConfig 'logSensitiveData' $false
Set-JsonProperty $httpConfig 'logIncomingTokens' $false
Set-JsonProperty $httpConfig 'verbose' $false
Set-JsonProperty $httpConfig 'networkInterface' '0.0.0.0'
Write-JsonUtf8NoBom -Path $httpConfigPath -Value $httpConfig -Depth 20

$deadline = [DateTime]::UtcNow.AddSeconds(30)
$models = $null
while ([DateTime]::UtcNow -lt $deadline) {
    try {
        $models = Invoke-RestMethod -Method Get -Uri ("http://127.0.0.1:{0}/api/v1/models" -f $Port) -Headers $lmHeaders -TimeoutSec 3
        break
    }
    catch {
        Start-Sleep -Milliseconds 500
    }
}
if ($null -eq $models) {
    throw 'LM Studio did not become reachable after applying the hardened configuration.'
}

# lms ls performs LM Studio's on-disk model discovery. Running it on every
# configured startup keeps verified GGUF files visible in the My Models UI.
& (Join-Path $PSScriptRoot 'refresh-lmstudio-model-catalog.ps1')

$listeners = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction Stop
$lanListeners = @($listeners | Where-Object { $_.LocalAddress -in @('0.0.0.0', '::') })
if ($lanListeners.Count -eq 0) {
    throw "LM Studio does not expose port $Port to the local network."
}

if (-not $SkipEstimates) {
    & $lms.Source load 'openai/gpt-oss-20b' --gpu max --context-length 131072 --parallel 1 --ttl $TtlSeconds --estimate-only -y
    if ($LASTEXITCODE -ne 0) { throw 'gpt-oss-20b load estimate failed.' }
    & $lms.Source load 'qwen3-coder-next' --gpu max --context-length 262144 --parallel 1 --ttl $TtlSeconds --estimate-only -y
    if ($LASTEXITCODE -ne 0) { throw 'Qwen3-Coder-Next Q6_K load estimate failed.' }
}

$authenticationEnabled = $false
try {
    $response = Invoke-WebRequest -UseBasicParsing -Method Get -Uri ("http://127.0.0.1:{0}/v1/models" -f $Port) -TimeoutSec 5
    $authenticationEnabled = $response.StatusCode -eq 401
}
catch {
    if ($null -ne $_.Exception.Response) {
        $authenticationEnabled = [int]$_.Exception.Response.StatusCode -eq 401
    }
}

if (-not $authenticationEnabled) {
    $message = 'LM Studio authentication is not enabled. Enable Developer > Server Settings > Require Authentication and create a token; then store it in the GO AI Server security view.'
    if ($RequireAuthentication) {
        throw $message
    }
    Write-Warning $message
}

Write-Host "LM Studio serves the local network on port $Port with authentication; JIT TTL is $TtlSeconds seconds. Backup: $backupRoot" -ForegroundColor Green
}
finally {
    if ($null -ne $previousElectronRunAsNode) {
        $env:ELECTRON_RUN_AS_NODE = $previousElectronRunAsNode
    }
    else {
        Remove-Item Env:ELECTRON_RUN_AS_NODE -ErrorAction SilentlyContinue
    }
}
