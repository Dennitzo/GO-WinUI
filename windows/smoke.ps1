#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PublishDirectory,

    [string] $ManifestPath,

    [ValidateSet('Folder', 'SingleFile')]
    [string] $Mode = 'Folder'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$PublishDirectory = Assert-GoArtifactPath -Path $PublishDirectory
if (-not (Test-Path -LiteralPath $PublishDirectory -PathType Container)) {
    throw "Publish directory does not exist: $PublishDirectory"
}

$requiredFiles = @('GO.exe')
foreach ($file in $requiredFiles) {
    $path = Join-Path $PublishDirectory $file
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Smoke check failed; required file is missing: $path"
    }
}

if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = $PublishDirectory + '.manifest.json'
}
$ManifestPath = Assert-GoArtifactPath -Path $ManifestPath
if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
    throw "Smoke check failed; publish manifest is missing: $ManifestPath"
}
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
if ($manifest.mode -ne $Mode -or $manifest.bricsCadContractSha256 -ne (Get-GoContractHash)) {
    throw 'Smoke check failed; publish manifest does not match mode or BricsCAD contract.'
}

if ($Mode -eq 'Folder') {
    $webIndex = Join-Path $PublishDirectory 'Assets\Web\index.html'
    if (-not (Test-Path -LiteralPath $webIndex -PathType Leaf)) {
        throw "Smoke check failed; offline web UI is missing: $webIndex"
    }

    $sqlite = Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File -Filter 'e_sqlite3.dll' -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $sqlite) {
        throw 'Smoke check failed; native local SQLite runtime e_sqlite3.dll is missing.'
    }
}
else {
    $sidecarDlls = @(Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File -Filter '*.dll' -ErrorAction SilentlyContinue)
    if ($sidecarDlls.Count -ne 0) {
        throw ("Smoke check failed; SingleFile app still requires DLL sidecars: " +
            (($sidecarDlls | ForEach-Object { $_.FullName }) -join ', '))
    }


    $runtimeSidecars = @(Get-ChildItem -LiteralPath $PublishDirectory -File | Where-Object { $_.Name -ne 'GO.exe' })
    if ($runtimeSidecars.Count -ne 0) {
        throw ("Smoke check failed; SingleFile directory contains runtime sidecars: " +
            (($runtimeSidecars | ForEach-Object { $_.FullName }) -join ', '))
    }
}

$forbiddenCadFiles = @('BrxMgd.dll', 'TD_Mgd.dll', 'TD_MgdBrep.dll', 'GOBricsCad.dll')
foreach ($file in $forbiddenCadFiles) {
    if (Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File -Filter $file -ErrorAction SilentlyContinue) {
        throw "Smoke check failed; optional BricsCAD dependency leaked into the app artifact: $file"
    }
}

$smokeBase = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'GO-WinUI-Smoke'))
$smokeRoot = [IO.Path]::GetFullPath((Join-Path $smokeBase ([Guid]::NewGuid().ToString('N'))))
if (-not [string]::Equals([IO.Path]::GetDirectoryName($smokeRoot), $smokeBase, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe smoke directory: $smokeRoot"
}
New-Item -ItemType Directory -Path $smokeRoot -Force | Out-Null
$previousDataDirectory = $env:GO_DATA_DIRECTORY
$previousBridgeDirectory = $env:GO_BRIDGE_DIRECTORY
$previousInstanceKey = $env:GO_SMOKE_INSTANCE_KEY
$env:GO_DATA_DIRECTORY = Join-Path $smokeRoot 'Data'
$env:GO_BRIDGE_DIRECTORY = Join-Path $smokeRoot 'Bridge'
$env:GO_SMOKE_INSTANCE_KEY = [Guid]::NewGuid().ToString('N')
$process = $null
try {
    $process = Start-Process -FilePath (Join-Path $PublishDirectory 'GO.exe') -PassThru -WindowStyle Hidden
    $runtimeFiles = @(
        (Join-Path $env:GO_DATA_DIRECTORY 'GO.db'),
        (Join-Path $env:GO_DATA_DIRECTORY 'settings.json'),
        (Join-Path $env:GO_BRIDGE_DIRECTORY 'active.json')
    )
    $webViewData = Join-Path $env:GO_DATA_DIRECTORY 'WebView2'
    $requiresWebViewInitialization = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq
        [Runtime.InteropServices.Architecture]::X64
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline -and -not $process.HasExited) {
        $startupReady = (@($runtimeFiles | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) }).Count -eq 0) -and
            ((-not $requiresWebViewInitialization) -or (Test-Path -LiteralPath $webViewData -PathType Container))
        if ($startupReady) {
            break
        }
        Start-Sleep -Milliseconds 250
    }

    if ($process.HasExited) {
        throw "Runtime smoke failed; GO exited early with code $($process.ExitCode)."
    }
    foreach ($runtimeFile in $runtimeFiles) {
        if (-not (Test-Path -LiteralPath $runtimeFile -PathType Leaf)) {
            throw "Runtime smoke failed; startup output is missing: $runtimeFile"
        }
    }
    if ($requiresWebViewInitialization -and -not (Test-Path -LiteralPath $webViewData -PathType Container)) {
        throw "Runtime smoke failed; WebView2 did not initialize: $webViewData"
    }
    if (-not $requiresWebViewInitialization -and -not (Test-Path -LiteralPath $webViewData -PathType Container)) {
        Write-Warning 'WebView2 x64 initialization was not asserted on the ARM64 build host; validate it on the supported x64 target.'
    }

    if (-not $process.CloseMainWindow() -or -not $process.WaitForExit(5000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $process.WaitForExit(5000) | Out-Null
    }
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $process.WaitForExit(5000) | Out-Null
    }
    $env:GO_DATA_DIRECTORY = $previousDataDirectory
    $env:GO_BRIDGE_DIRECTORY = $previousBridgeDirectory
    $env:GO_SMOKE_INSTANCE_KEY = $previousInstanceKey
    $smokeRoot = [IO.Path]::GetFullPath($smokeRoot)
    if (-not [string]::Equals([IO.Path]::GetDirectoryName($smokeRoot), $smokeBase, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove unsafe smoke directory: $smokeRoot"
    }
    if (Test-Path -LiteralPath $smokeRoot) {
        for ($attempt = 1; $attempt -le 20; $attempt++) {
            try {
                Remove-Item -LiteralPath $smokeRoot -Recurse -Force -ErrorAction Stop
                break
            }
            catch {
                if ($attempt -eq 20) {
                    throw
                }
                Start-Sleep -Milliseconds 250
            }
        }
    }
}

Write-Host "Smoke checks passed: $PublishDirectory" -ForegroundColor Green
