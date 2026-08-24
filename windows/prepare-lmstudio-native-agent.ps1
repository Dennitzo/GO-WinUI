#requires -Version 5.1
[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$workerDirectory = Resolve-GoRepositoryPath -RelativePath 'workers\lmstudio-native-agent'
$packageLock = Join-Path $workerDirectory 'package-lock.json'
$sdkEntry = Join-Path $workerDirectory 'node_modules\@lmstudio\sdk\dist\index.cjs'
$sdkPackage = Join-Path $workerDirectory 'node_modules\@lmstudio\sdk\package.json'
$workerScript = Join-Path $workerDirectory 'worker.cjs'

if (-not (Test-Path -LiteralPath $workerScript -PathType Leaf)) {
    throw "Native LM Studio agent worker is missing: $workerScript"
}
if (-not (Test-Path -LiteralPath $packageLock -PathType Leaf)) {
    throw "Native LM Studio agent package lock is missing: $packageLock"
}
if ($null -eq (Get-Command node.exe -ErrorAction SilentlyContinue)) {
    throw 'Node.js is required to prepare the native LM Studio SDK worker.'
}
if ($null -eq (Get-Command npm.cmd -ErrorAction SilentlyContinue)) {
    throw 'npm is required to prepare the native LM Studio SDK worker.'
}

$restoreRequired = -not (Test-Path -LiteralPath $sdkEntry -PathType Leaf) `
    -or -not (Test-Path -LiteralPath $sdkPackage -PathType Leaf) `
    -or (Get-Item -LiteralPath $packageLock).LastWriteTimeUtc -gt (Get-Item -LiteralPath $sdkPackage).LastWriteTimeUtc
if ($restoreRequired) {
    Push-Location $workerDirectory
    try {
        & npm.cmd ci --omit=dev --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) {
            throw "npm ci for the native LM Studio SDK worker failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

if (-not (Test-Path -LiteralPath $sdkEntry -PathType Leaf)) {
    throw "Native LM Studio SDK entry point was not restored: $sdkEntry"
}

Write-Host 'Native LM Studio SDK worker is ready.' -ForegroundColor Green
