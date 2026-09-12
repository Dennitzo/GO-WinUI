#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $SourceRoot,
    [string] $DestinationRoot = (Join-Path $env:USERPROFILE '.cache\huggingface\hub'),
    [string] $ManifestPath,
    [string] $ReportPath,
    [switch] $Apply
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ManifestPath)) { $ManifestPath = Join-Path $PSScriptRoot '..\deploy\go-ai\models.manifest.json' }
if ([string]::IsNullOrWhiteSpace($ReportPath)) { $ReportPath = Join-Path $PSScriptRoot '..\artifacts\coding-validation\model-migration.json' }
$sourceBase = [IO.Path]::GetFullPath($SourceRoot).TrimEnd('\')
$destinationBase = [IO.Path]::GetFullPath($DestinationRoot).TrimEnd('\')
$reportFile = [IO.Path]::GetFullPath($ReportPath)

function Assert-MigrationPath([string] $Path, [string] $Root) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($Root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Migration path escapes its verified root: $full"
    }
    $part = $full
    while ($part) {
        if (Test-Path -LiteralPath $part) {
            $item = Get-Item -LiteralPath $part -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Migration refuses a reparse point: $part"
            }
        }
        $part = [IO.Path]::GetDirectoryName($part)
    }
    return $full
}

function Get-MigrationFileId([string] $Path) {
    $result = & fsutil.exe file queryfileid $Path 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Cannot read the NTFS file identity: $Path ($result)" }
    $match = [regex]::Match(($result -join ' '), '0x[0-9a-fA-F]+')
    if (-not $match.Success) { throw "Unknown file identity response for $Path" }
    return $match.Value.ToLowerInvariant()
}

if (-not (Test-Path -LiteralPath $sourceBase -PathType Container)) { throw "Source model root is missing: $sourceBase" }
if ([string]::Equals($sourceBase, $destinationBase, [StringComparison]::OrdinalIgnoreCase) -or
    $destinationBase.StartsWith($sourceBase + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $sourceBase.StartsWith($destinationBase + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Source and destination must be separate model roots.'
}
if (-not [string]::Equals([IO.Path]::GetPathRoot($sourceBase), [IO.Path]::GetPathRoot($destinationBase), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'This migration requires a rename on the same Windows volume; cross-volume copying is not supported.'
}
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$files = @(Get-ChildItem -LiteralPath $sourceBase -Recurse -File -Force)
if ($files.Count -eq 0) { Write-Host "No model files remain in $sourceBase"; return }
$plan = @()
foreach ($file in $files) {
    $source = Assert-MigrationPath $file.FullName $sourceBase
    $relative = $source.Substring($sourceBase.Length + 1).Replace('\', '/')
    $entries = @($manifest.models | Where-Object { $_.path -ceq $relative })
    if ($entries.Count -ne 1) { throw "No unique pinned manifest entry for $relative; nothing has been moved." }
    $entry = $entries[0]
    if ([long]$entry.length -ne $file.Length -or [string]$entry.revision -notmatch '^[0-9a-f]{40}$' -or
        [string]$entry.sha256 -notmatch '^[0-9a-f]{64}$' -or [string]$entry.repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
        throw "Invalid metadata or file size for $relative; nothing has been moved."
    }
    $prefix = [string]$entry.repository + '/'
    if (-not $relative.StartsWith($prefix, [StringComparison]::Ordinal)) { throw "Repository/path mismatch: $relative" }
    $inside = $relative.Substring($prefix.Length).Replace('/', '\')
    $repoDirectory = Join-Path $destinationBase ('models--' + ([string]$entry.repository).Replace('/', '--'))
    $destination = Assert-MigrationPath (Join-Path $repoDirectory ("snapshots\$($entry.revision)\$inside")) $destinationBase
    if (Test-Path -LiteralPath $destination) { throw "Destination already exists; no overwrite or deletion: $destination" }
    $plan += [pscustomobject]@{
        source = $source; destination = $destination; repository = [string]$entry.repository
        revision = [string]$entry.revision; length = $file.Length; sha256 = [string]$entry.sha256
        fileId = Get-MigrationFileId $source; moved = $false
    }
}

# Validate every byte before the first move, against the pinned upstream LFS hashes.
foreach ($item in $plan) {
    Write-Host "Verifying SHA-256: $($item.source)"
    $stream = [IO.File]::OpenRead($item.source)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose(); $stream.Dispose() }
    if ($hash -cne $item.sha256) { throw "SHA-256 mismatch: $($item.source); nothing has been moved." }
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($reportFile)) | Out-Null
$report = [ordered]@{
    schema = 'go.native-model-migration.v1'; createdAt = [DateTimeOffset]::UtcNow.ToString('O')
    sourceRoot = $sourceBase; destinationRoot = $destinationBase; state = 'verified'; files = $plan
}
[IO.File]::WriteAllText($reportFile, ($report | ConvertTo-Json -Depth 6))
if (-not $Apply) { Write-Host "Verified migration plan: $reportFile. Use -Apply to move the verified files."; return }

try {
    foreach ($item in $plan) {
        $source = Assert-MigrationPath $item.source $sourceBase
        $destination = Assert-MigrationPath $item.destination $destinationBase
        if ((Get-MigrationFileId $source) -cne $item.fileId -or (Get-Item -LiteralPath $source).Length -ne $item.length) {
            throw "Source identity changed during preflight: $source"
        }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        Move-Item -LiteralPath $source -Destination $destination -ErrorAction Stop
        $item.moved = $true
        if ((Get-MigrationFileId $destination) -cne $item.fileId -or (Get-Item -LiteralPath $destination).Length -ne $item.length) {
            throw "File identity changed after native move: $destination"
        }
        Write-Host "Moved without copying: $destination"
    }
    foreach ($group in ($plan | Group-Object repository)) {
        $entry = $group.Group[0]
        $repoDirectory = Join-Path $destinationBase ('models--' + $entry.repository.Replace('/', '--'))
        $reference = Assert-MigrationPath (Join-Path $repoDirectory 'refs\main') $destinationBase
        if (-not (Test-Path -LiteralPath $reference)) {
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($reference)) | Out-Null
            # HF cache refs are exact revision names, with no newline or BOM.
            [IO.File]::WriteAllText($reference, $entry.revision, (New-Object Text.UTF8Encoding($false)))
        }
    }
    $report.state = 'completed'
}
catch {
    $migrationError = $_
    foreach ($item in @($plan | Where-Object { $_.moved })) {
        $source = Assert-MigrationPath $item.source $sourceBase
        $destination = Assert-MigrationPath $item.destination $destinationBase
        if (-not (Test-Path -LiteralPath $source) -and (Test-Path -LiteralPath $destination) -and
            (Get-MigrationFileId $destination) -ceq $item.fileId) {
            Move-Item -LiteralPath $destination -Destination $source -ErrorAction Stop
            $item.moved = $false
        }
    }
    $report.state = 'failed-rolled-back'
    throw $migrationError
}
finally { [IO.File]::WriteAllText($reportFile, ($report | ConvertTo-Json -Depth 6)) }
Write-Host "Model migration completed: $reportFile"
