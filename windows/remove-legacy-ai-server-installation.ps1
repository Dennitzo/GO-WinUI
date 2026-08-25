#requires -Version 5.1
#requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string] $ActiveStackRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'GO-AI-Stack'),
    [switch] $Force
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

try {

$programFilesRoot = [IO.Path]::GetFullPath([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles))
$programDataRoot = [IO.Path]::GetFullPath([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData))
$activeRoot = [IO.Path]::GetFullPath($ActiveStackRoot)
$legacyProgramFiles = [IO.Path]::GetFullPath((Join-Path $programFilesRoot 'GO-AI-Server'))
$legacyProgramData = [IO.Path]::GetFullPath((Join-Path $programDataRoot 'GO-AI-Server'))
$activeDatabase = Join-Path $activeRoot 'data\database\go-ai-server.db'
$legacyDatabase = Join-Path $legacyProgramData 'Data\go-ai-server.db'
$migrationDirectory = Join-Path $activeRoot 'migration-backups'

function Assert-DirectChild {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [string] $Parent
    )

    $resolvedParent = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if (-not $resolvedPath.StartsWith($resolvedParent, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetDirectoryName($resolvedPath).TrimEnd('\') -ne $resolvedParent.TrimEnd('\')) {
        throw "Refusing unsafe legacy cleanup target: $resolvedPath"
    }
}

Assert-DirectChild -Path $legacyProgramFiles -Parent $programFilesRoot
Assert-DirectChild -Path $legacyProgramData -Parent $programDataRoot

if (-not (Test-Path -LiteralPath $activeDatabase -PathType Leaf)) {
    throw "The active Docker database is missing: $activeDatabase"
}

if (Test-Path -LiteralPath $legacyDatabase -PathType Leaf) {
    $matchingBackup = Get-ChildItem -LiteralPath $migrationDirectory -File -Filter 'go-ai-server-*.db' -ErrorAction SilentlyContinue |
        Where-Object { $_.Length -eq (Get-Item -LiteralPath $legacyDatabase).Length } |
        Where-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $legacyDatabase -Algorithm SHA256).Hash } |
        Select-Object -First 1
    if ($null -eq $matchingBackup) {
        throw 'The legacy database has no byte-identical migration backup in the active Docker data root.'
    }
}

$legacyProcesses = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
    -not [string]::IsNullOrWhiteSpace($_.ExecutablePath) -and
    ($_.ExecutablePath.StartsWith($legacyProgramFiles + '\', [StringComparison]::OrdinalIgnoreCase) -or
     $_.ExecutablePath.StartsWith($legacyProgramData + '\', [StringComparison]::OrdinalIgnoreCase))
}
if ($null -ne $legacyProcesses) {
    throw 'A legacy GO AI Server process is still running. Stop it before cleanup.'
}

foreach ($target in @($legacyProgramFiles, $legacyProgramData)) {
    if (-not (Test-Path -LiteralPath $target)) {
        Write-Host "Already removed: $target" -ForegroundColor DarkGray
        continue
    }

    if ($Force -or $PSCmdlet.ShouldProcess($target, 'Permanently remove legacy Windows GO AI Server installation')) {
        Remove-Item -LiteralPath $target -Recurse -Force
        Write-Host "Removed: $target" -ForegroundColor Green
    }
}

& (Join-Path $PSScriptRoot 'remove-legacy-ai-server-autostart.ps1')

foreach ($target in @($legacyProgramFiles, $legacyProgramData)) {
    if (Test-Path -LiteralPath $target) {
        throw "Legacy installation path still exists: $target"
    }
}

Write-Host "Active Docker stack data retained: $activeRoot" -ForegroundColor Cyan
}
catch {
    $diagnosticDirectory = Join-Path ([IO.Path]::GetFullPath($ActiveStackRoot)) 'data\logs'
    New-Item -ItemType Directory -Path $diagnosticDirectory -Force | Out-Null
    $diagnosticPath = Join-Path $diagnosticDirectory 'legacy-installation-cleanup-error.log'
    [IO.File]::WriteAllText($diagnosticPath, ($_ | Format-List * -Force | Out-String))
    throw
}
