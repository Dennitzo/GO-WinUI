#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $DataRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'GO-AI-Server'),

    [ValidateRange(1024, 65535)]
    [int] $Port = 1234,

    [ValidateRange(60, 86400)]
    [int] $TtlSeconds = 600
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$DataRoot = [IO.Path]::GetFullPath($DataRoot)
$logDirectory = Join-Path $DataRoot 'Logs'
$logPath = Join-Path $logDirectory 'lmstudio-startup.log'

function Write-LmStudioStartupLog {
    param([Parameter(Mandatory = $true)] [string] $Message)

    try {
        New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
        ('[{0}] {1}' -f [DateTimeOffset]::Now.ToString('o'), $Message) |
            Add-Content -LiteralPath $logPath -Encoding utf8
    }
    catch {
        # Startup logging is diagnostic and must not hide the original result.
    }
}

$previousElectronRunAsNode = $env:ELECTRON_RUN_AS_NODE
try {
    # This environment variable is useful for some development shells, but prevents
    # LM Studio's Electron-backed daemon bootstrap from starting normally.
    Remove-Item Env:ELECTRON_RUN_AS_NODE -ErrorAction SilentlyContinue
    $lms = Assert-GoCommand -Name 'lms'

    & $lms.Source daemon status *> $null
    if ($LASTEXITCODE -ne 0) {
        & $lms.Source daemon up | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "LM Studio daemon startup failed with exit code $LASTEXITCODE."
        }
    }

    & (Join-Path $PSScriptRoot 'configure-lmstudio.ps1') `
        -Port $Port `
        -TtlSeconds $TtlSeconds `
        -DataRoot $DataRoot `
        -SkipEstimates `
        -RequireAuthentication

    $token = Get-GoLmStudioToken -DataRoot $DataRoot
    $response = Invoke-RestMethod `
        -Method Get `
        -Uri ("http://127.0.0.1:{0}/api/v1/models" -f $Port) `
        -Headers (Get-GoLmStudioHeaders -Token $token) `
        -TimeoutSec 10
    if ($null -eq $response.models) {
        throw 'LM Studio returned no model list after startup.'
    }

    Write-LmStudioStartupLog 'LM Studio daemon and authenticated local-network server are ready.'
}
catch {
    Write-LmStudioStartupLog ("LM Studio startup failed ({0}): {1}" -f $_.Exception.GetType().Name, $_.Exception.Message)
    throw
}
finally {
    if ($null -ne $previousElectronRunAsNode) {
        $env:ELECTRON_RUN_AS_NODE = $previousElectronRunAsNode
    }
    else {
        Remove-Item Env:ELECTRON_RUN_AS_NODE -ErrorAction SilentlyContinue
    }
}
