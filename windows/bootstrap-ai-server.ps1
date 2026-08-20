#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $DataRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'GO-AI-Server'),

    [string] $ExpectedLanIp = '192.168.0.67',

    [switch] $SkipDockerGpuTest
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

if (-not [Environment]::Is64BitOperatingSystem) {
    throw 'GO AI Server requires 64-bit Windows.'
}
$DataRoot = [IO.Path]::GetFullPath($DataRoot)
if ([string]::Equals($DataRoot, [IO.Path]::GetPathRoot($DataRoot), [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe data root: $DataRoot"
}

Assert-GoCommand -Name 'dotnet' | Out-Null
$dotnetVersion = (& dotnet --version).Trim()
if (-not $dotnetVersion.StartsWith('10.', [StringComparison]::Ordinal)) {
    throw ".NET 10 SDK is required; detected $dotnetVersion."
}
$docker = Resolve-GoDockerCommand
& $docker version --format '{{.Server.Version}}' | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Docker Desktop engine is not ready.'
}

$settingsStore = Join-Path $env:APPDATA 'Docker\settings-store.json'
if (-not (Test-Path -LiteralPath $settingsStore -PathType Leaf)) {
    throw "Docker Desktop settings file is missing: $settingsStore"
}
$dockerSettings = Get-Content -LiteralPath $settingsStore -Raw -Encoding utf8 | ConvertFrom-Json
if ($dockerSettings.HostNetworkingEnabled -ne $true) {
    throw 'Docker Desktop host networking is disabled. Enable Resources > Network > Host networking and restart Docker Desktop.'
}
if ($dockerSettings.AutoStart -ne $true) {
    throw 'Docker Desktop must be configured to start automatically after the AMD user signs in.'
}

$directories = @(
    'Artifacts', 'Artifacts\worker', 'Cache\searxng',
    'Caddy\config', 'Caddy\config\caddy', 'Caddy\data', 'Caddy\data\caddy',
    'Config', 'Config\searxng', 'Data', 'Logs', 'Models', 'Secrets', 'Uploads'
)
foreach ($relative in $directories) {
    New-Item -ItemType Directory -Path (Join-Path $DataRoot $relative) -Force | Out-Null
}
foreach ($worker in @('speech', 'media', 'image', 'video')) {
    $keyPath = Join-Path $DataRoot ("Secrets\{0}-worker.key" -f $worker)
    if (-not (Test-Path -LiteralPath $keyPath -PathType Leaf)) {
        New-GoRandomSecret | Set-Content -LiteralPath $keyPath -Encoding ascii -NoNewline
    }
}

$searxSettings = Join-Path $DataRoot 'Config\searxng\settings.yml'
if (-not (Test-Path -LiteralPath $searxSettings -PathType Leaf)) {
    $template = Get-Content -LiteralPath (Resolve-GoRepositoryPath -RelativePath 'deploy\go-ai\searxng\settings.yml.template') -Raw -Encoding utf8
    $template.Replace('__SEARXNG_SECRET__', (New-GoRandomSecret)) |
        Set-Content -LiteralPath $searxSettings -Encoding utf8
}

$composeEnvironment = @(
    ('GO_AI_DATA_ROOT={0}' -f $DataRoot.Replace('\', '/')),
    'GO_AI_IMAGE_VERSION=1.0.0',
    ('GO_AI_EXPECTED_LAN_IP={0}' -f $ExpectedLanIp)
) -join [Environment]::NewLine
$composeEnvironment | Set-Content -LiteralPath (Join-Path $DataRoot 'Config\compose.env') -Encoding ascii

$addresses = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop |
    Where-Object { $_.IPAddress -eq $ExpectedLanIp -and $_.AddressState -eq 'Preferred' }
if (@($addresses).Count -eq 0) {
    throw "Expected LAN IP $ExpectedLanIp is not active. Update DHCP/reservation or GO_AI_EXPECTED_LAN_IP before deployment."
}

$profiles = Get-NetConnectionProfile -ErrorAction Stop |
    Where-Object { $_.IPv4Connectivity -ne 'Disconnected' }
if (@($profiles | Where-Object NetworkCategory -eq 'Private').Count -eq 0) {
    Write-Warning 'The active network profile is not Private. Deployment readiness will remain false until it is changed.'
}

Assert-GoCommand -Name 'lms' | Out-Null
try {
    $lmToken = Get-GoLmStudioToken -DataRoot $DataRoot
    $lmHeaders = Get-GoLmStudioHeaders -Token $lmToken
    $lm = Invoke-RestMethod -Method Get -Uri 'http://127.0.0.1:1234/api/v1/models' -Headers $lmHeaders -TimeoutSec 5
    if ($null -eq $lm.models) {
        throw 'LM Studio returned no model list.'
    }
}
catch {
    if ($null -ne $_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 401) {
        Write-Warning 'LM Studio is reachable and requires authentication, but no usable DPAPI token is configured yet.'
    }
    else {
        Write-Warning 'LM Studio is currently stopped. This is expected when the GO-AI-Server app is closed; the app starts it after launch.'
    }
}

if (-not $SkipDockerGpuTest) {
    $gpuImage = 'nvidia/cuda@sha256:133c78a0575303be34164d0b90137a042172bdf60696af01a3c424ab402d86e2'
    & $docker run --rm --gpus all $gpuImage nvidia-smi --query-gpu=name,memory.total --format=csv,noheader
    if ($LASTEXITCODE -ne 0) {
        throw 'Docker GPU passthrough check failed.'
    }
}

& (Join-Path $PSScriptRoot 'validate-ai-server-compose.ps1')
Write-Host "GO AI Server bootstrap checks passed. Data root: $DataRoot" -ForegroundColor Green
