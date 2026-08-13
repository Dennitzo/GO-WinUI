#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $DataRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'GO-AI-Server'),

    [string] $OutputDirectory,

    [string] $ServerUrl = 'https://192.168.0.67:8443',

    [switch] $KeepServerBootstrapKey
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Resolve-GoRepositoryPath -RelativePath 'artifacts\go-ai-server\client-bundle'
}
$DataRoot = [IO.Path]::GetFullPath($DataRoot)
$rootCertificate = Join-Path $DataRoot 'Caddy\data\caddy\pki\authorities\local\root.crt'
$bootstrapKeyPath = Join-Path $DataRoot 'Secrets\bootstrap-client-key.once'
if (-not (Test-Path -LiteralPath $rootCertificate -PathType Leaf)) {
    throw "Caddy root certificate is missing: $rootCertificate"
}
if (-not (Test-Path -LiteralPath $bootstrapKeyPath -PathType Leaf)) {
    throw 'The one-time bootstrap client key is no longer available. Create a new client key in the server security view.'
}

$OutputDirectory = Assert-GoArtifactPath -Path $OutputDirectory
$OutputDirectory = Reset-GoArtifactDirectory -Path $OutputDirectory
$keyOutputPath = Assert-GoArtifactPath -Path (Join-Path (Split-Path $OutputDirectory -Parent) 'go-ai-client-key.once.txt')
if (Test-Path -LiteralPath $keyOutputPath) {
    Remove-Item -LiteralPath $keyOutputPath -Force
}

$apiKey = (Get-Content -LiteralPath $bootstrapKeyPath -Raw -Encoding ascii).Trim()
if ([string]::IsNullOrWhiteSpace($apiKey)) {
    throw 'The one-time bootstrap client key file is empty.'
}

$capabilities = Invoke-RestMethod -Method Get -Uri 'http://127.0.0.1:7080/v1/capabilities' -Headers @{
    'X-GO-AI-Key' = $apiKey
} -TimeoutSec 15

$certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($rootCertificate)
try {
    $fingerprint = $certificate.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256).ToLowerInvariant()
}
finally {
    $certificate.Dispose()
}

Copy-Item -LiteralPath $rootCertificate -Destination (Join-Path $OutputDirectory 'go-ai-root.crt')
$connection = [ordered]@{
    schema = 'go.ai.connection.v1'
    protocolVersion = '1.0'
    serverUrl = $ServerUrl
    expectedLanIp = ([Uri]$ServerUrl).Host
    caCertificate = 'go-ai-root.crt'
    caSha256Fingerprint = $fingerprint
    capabilitySnapshot = 'capabilities.json'
    exportedAtUtc = [DateTime]::UtcNow.ToString('o')
}
$connection | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'connection.json') -Encoding utf8
$capabilities | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'capabilities.json') -Encoding utf8
$apiKey | Set-Content -LiteralPath $keyOutputPath -Encoding ascii -NoNewline

if (-not $KeepServerBootstrapKey) {
    Remove-Item -LiteralPath $bootstrapKeyPath -Force
}

Write-Host "Client connection bundle: $OutputDirectory" -ForegroundColor Green
Write-Host "One-time client key (separate): $keyOutputPath" -ForegroundColor Yellow
