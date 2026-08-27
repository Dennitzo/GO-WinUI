#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $DataRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'GO-AI-Stack'),
    [string] $ModelRoot,
    [string] $LmStudioModelRoot,
    [string] $ServerIp = '192.168.0.67',
    [string] $ImageVersion = '1.0.0',
    [string] $LegacyDatabasePath,
    [switch] $SkipBuild,
    [switch] $SkipStart,
    [switch] $SkipModelHashVerification
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

function Test-SqliteDatabase {
    param([Parameter(Mandatory = $true)] [string] $Path)

    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($null -eq $python) {
        throw 'Python is required once to validate the migrated SQLite database.'
    }
    & $python.Source -c "import sqlite3,sys; c=sqlite3.connect(sys.argv[1]); r=c.execute('PRAGMA integrity_check').fetchone()[0]; c.close(); print(r); raise SystemExit(0 if r=='ok' else 2)" $Path
    if ($LASTEXITCODE -ne 0) { throw "SQLite integrity check failed: $Path" }
}

$paths = Get-GoAiStackDefaults -DataRoot $DataRoot -ModelRoot $ModelRoot -LmStudioModelRoot $LmStudioModelRoot
$directories = @(
    $paths.ModelRoot,
    $paths.LmStudioModelRoot,
    (Join-Path $paths.DataRoot 'data\database'),
    (Join-Path $paths.DataRoot 'data\uploads'),
    (Join-Path $paths.DataRoot 'data\artifacts\worker'),
    (Join-Path $paths.DataRoot 'data\logs'),
    (Join-Path $paths.DataRoot 'migration-backups')
)
foreach ($directory in $directories) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }

$database = Join-Path $paths.DataRoot 'data\database\go-ai-server.db'
if (-not (Test-Path -LiteralPath $database -PathType Leaf) -and
    -not [string]::IsNullOrWhiteSpace($LegacyDatabasePath) -and
    (Test-Path -LiteralPath $LegacyDatabasePath -PathType Leaf)) {
    $resolvedLegacyDatabase = [IO.Path]::GetFullPath($LegacyDatabasePath)
    Test-SqliteDatabase -Path $resolvedLegacyDatabase
    $backupName = 'go-ai-server-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '.db'
    Copy-Item -LiteralPath $resolvedLegacyDatabase -Destination (Join-Path $paths.DataRoot "migration-backups\$backupName")
    Copy-Item -LiteralPath $resolvedLegacyDatabase -Destination $database
}
if (Test-Path -LiteralPath $database -PathType Leaf) { Test-SqliteDatabase -Path $database }

if (-not $SkipModelHashVerification) {
    $manifestPath = Resolve-GoRepositoryPath -RelativePath 'deploy\go-ai\models.manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    foreach ($entry in $manifest.models) {
        $file = Join-Path $paths.LmStudioModelRoot ([string]$entry.path -replace '/', '\')
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Pinned model is missing: $file" }
        if ((Get-Item -LiteralPath $file).Length -ne [long]$entry.length) { throw "Pinned model length mismatch: $file" }
        $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne ([string]$entry.sha256).ToLowerInvariant()) { throw "Pinned model SHA-256 mismatch: $file" }
        Write-Host "Verified $($entry.path)" -ForegroundColor DarkGray
    }
}

$requiredLmStudioResources = @(
    'lmstudio-community\Qwen3.8-27B-GGUF\Qwen3.8-27B-Q4_K_M.gguf',
    'lmstudio-community\Qwen3.8-27B-GGUF\mmproj-Qwen3.8-27B-BF16.gguf'
)
foreach ($relativePath in $requiredLmStudioResources) {
    $resource = Join-Path $paths.LmStudioModelRoot $relativePath
    if (-not (Test-Path -LiteralPath $resource -PathType Leaf)) {
        throw "Required LM Studio model resource is missing: $resource"
    }
}

$requiredWorkerResources = @(
    'speech\faster-whisper-large-v3',
    'speech\spkrec-ecapa-voxceleb',
    'speech\supertonic-3',
    'image\z-image\z_image_turbo-Q4_K.gguf',
    'image\z-image\ae.safetensors',
    'image\z-image\Qwen3-4B-Instruct-2507-Q4_K_M.gguf'
)
foreach ($relativePath in $requiredWorkerResources) {
    $resource = Join-Path $paths.ModelRoot $relativePath
    if (-not (Test-Path -LiteralPath $resource)) {
        throw "Required offline worker model resource is missing: $resource"
    }
}

Write-GoAiStackEnvironment -Paths $paths -ServerIp $ServerIp -ImageVersion $ImageVersion

$firewallName = 'GO AI Docker Gateway 8080'
if (Get-Command Get-NetFirewallRule -ErrorAction SilentlyContinue) {
    try {
        Get-NetFirewallRule -DisplayName $firewallName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        New-NetFirewallRule -DisplayName $firewallName -Direction Inbound -Action Allow -Protocol TCP -LocalPort 8080 -Profile Private -RemoteAddress LocalSubnet | Out-Null
    }
    catch {
        Write-Warning 'The private-network firewall rule could not be installed. Run deployment once from an elevated PowerShell.'
    }
}

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build-ai-stack.ps1') -DataRoot $paths.DataRoot -ModelRoot $paths.ModelRoot -ServerIp $ServerIp -ImageVersion $ImageVersion
}
if (-not $SkipStart) {
    & (Join-Path $PSScriptRoot 'start-ai-stack.ps1') -DataRoot $paths.DataRoot -ModelRoot $paths.ModelRoot -LmStudioModelRoot $paths.LmStudioModelRoot -ServerIp $ServerIp -ImageVersion $ImageVersion
}
Write-Host 'GO AI hybrid deployment completed: Docker gateway/workers plus LM Studio model runtime.' -ForegroundColor Green
