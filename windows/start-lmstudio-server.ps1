#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateRange(1, 65535)]
    [int] $Port = 1234,
    [string] $BindAddress = '0.0.0.0'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$lms = Resolve-GoLmStudioCommand
& $lms daemon status *> $null
if ($LASTEXITCODE -ne 0) {
    & $lms daemon up
    if ($LASTEXITCODE -ne 0) { throw 'LM Studio daemon could not be started.' }
}

$mustRestart = $true
if (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue) {
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue)
    $mustRestart = -not ($listeners | Where-Object {
        $_.LocalAddress -in @($BindAddress, '0.0.0.0', '::')
    })
}

if ($mustRestart) {
    # lms.exe writes the successful stop message to stderr. PowerShell turns
    # that native stderr record into a terminating RemoteException while the
    # script uses ErrorActionPreference=Stop, so isolate and validate by exit
    # code instead of treating the output channel as failure semantics.
    $previousErrorActionPreference = $ErrorActionPreference
    $stopExitCode = -1
    try {
        $ErrorActionPreference = 'Continue'
        & $lms server stop *> $null
        $stopExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($stopExitCode -ne 0) {
        Write-Verbose "LM Studio server stop returned exit code $stopExitCode; startup will still be attempted."
    }
    & $lms server start --port $Port --bind $BindAddress
    if ($LASTEXITCODE -ne 0) { throw 'LM Studio model server could not be started.' }
}

$deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
do {
    try {
        $catalog = Invoke-RestMethod -Uri "http://127.0.0.1:${Port}/api/v1/models" -Method Get -TimeoutSec 3
        if ($null -ne $catalog.models) {
            Write-Host "LM Studio is serving $($catalog.models.Count) models on ${BindAddress}:${Port}." -ForegroundColor Green
            return
        }
    }
    catch {
        if ([DateTimeOffset]::UtcNow -ge $deadline) { throw }
    }
    Start-Sleep -Milliseconds 500
} while ([DateTimeOffset]::UtcNow -lt $deadline)

throw 'LM Studio did not expose its model catalog within 30 seconds.'
