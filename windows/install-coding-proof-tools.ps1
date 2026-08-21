[CmdletBinding()]
param(
    [string]$LeanToolchain = 'leanprover/lean4:v4.30.0',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest

$elanVersion = 'v4.2.3'
$elanArchiveUrl = "https://github.com/leanprover/elan/releases/download/$elanVersion/elan-x86_64-pc-windows-msvc.zip"
$elanArchiveSha256 = 'be5e92a2dfdd8176099b2db0b810c27237c9054f1e5db1126f4f2a1134773b25'
$elanBin = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.elan\bin'
$elanExe = Join-Path $elanBin 'elan.exe'

if ($Force -or -not (Test-Path -LiteralPath $elanExe -PathType Leaf)) {
    $temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("go-lean-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    try {
        $archive = Join-Path $temporaryRoot 'elan.zip'
        Invoke-WebRequest -Uri $elanArchiveUrl -OutFile $archive -UseBasicParsing
        $actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -ne $elanArchiveSha256) {
            throw "Elan SHA-256 stimmt nicht: erwartet $elanArchiveSha256, erhalten $actualHash"
        }
        Expand-Archive -LiteralPath $archive -DestinationPath $temporaryRoot -Force
        $installer = Get-ChildItem -LiteralPath $temporaryRoot -Filter 'elan-init.exe' -Recurse | Select-Object -First 1
        if (-not $installer) {
            throw 'elan-init.exe fehlt im verifizierten Archiv.'
        }
        & $installer.FullName -y --default-toolchain none
        if ($LASTEXITCODE -ne 0) {
            throw "Elan-Installation fehlgeschlagen (Exit $LASTEXITCODE)."
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryRoot) {
            $separator = [System.IO.Path]::DirectorySeparatorChar
            $resolvedTempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd($separator) + $separator
            $resolvedTemporaryRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
            if (-not $resolvedTemporaryRoot.StartsWith($resolvedTempBase, [StringComparison]::OrdinalIgnoreCase) -or
                -not ([System.IO.Path]::GetFileName($resolvedTemporaryRoot)).StartsWith('go-lean-', [StringComparison]::Ordinal)) {
                throw "Unsicheres temporäres Löschziel abgewiesen: $resolvedTemporaryRoot"
            }
            Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
        }
    }
}

if (-not (Test-Path -LiteralPath $elanExe -PathType Leaf)) {
    throw "Elan wurde nicht unter $elanExe installiert."
}

$installedToolchains = @(& $elanExe toolchain list)
if ($LASTEXITCODE -ne 0) {
    throw "Installierte Lean-Toolchains konnten nicht ermittelt werden (Exit $LASTEXITCODE)."
}
$hasPinnedToolchain = $installedToolchains | Where-Object {
    ($_ -split '\s+', 2)[0] -eq $LeanToolchain
} | Select-Object -First 1
if (-not $hasPinnedToolchain) {
    & $elanExe toolchain install $LeanToolchain
    if ($LASTEXITCODE -ne 0) {
        throw "Lean-Toolchain $LeanToolchain konnte nicht installiert werden (Exit $LASTEXITCODE)."
    }
}
else {
    Write-Host "Lean-Toolchain bereits installiert: $LeanToolchain"
}
& $elanExe default $LeanToolchain
if ($LASTEXITCODE -ne 0) {
    throw "Lean-Toolchain $LeanToolchain konnte nicht als Standard gesetzt werden."
}

$leanExe = Join-Path $elanBin 'lean.exe'
$lakeExe = Join-Path $elanBin 'lake.exe'
& $leanExe --version
if ($LASTEXITCODE -ne 0) { throw 'Lean-Smoketest fehlgeschlagen.' }
& $lakeExe --version
if ($LASTEXITCODE -ne 0) { throw 'Lake-Smoketest fehlgeschlagen.' }

Write-Host "Formale Coding-Beweise verwenden $LeanToolchain über $elanBin"
