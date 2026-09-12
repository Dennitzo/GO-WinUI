#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $DataRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'GO-AI-Stack'),
    [string] $ModelRoot,
    [string] $NativeModelRoot,
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

$paths = Get-GoAiStackDefaults -DataRoot $DataRoot -ModelRoot $ModelRoot -NativeModelRoot $NativeModelRoot
$directories = @(
    $paths.ModelRoot,
    $paths.NativeModelRoot,
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
        $repository = [string]$entry.repository
        $relativePath = ([string]$entry.path).Substring($repository.Length + 1)
        $candidatePaths = @(Resolve-GoNativeModelFile -NativeModelRoot $paths.NativeModelRoot -Repository $repository -Revision $entry.revision -FileName $relativePath)
        # Some repositories publish split GGUFs inside a quantization directory.
        # Accept both that native layout and already migrated flat snapshots.
        if ($relativePath -match '^([^/]+)-\d{5}-of-\d{5}\.gguf$') {
            $nestedPath = $Matches[1] + '/' + $relativePath
            $candidatePaths += Resolve-GoNativeModelFile -NativeModelRoot $paths.NativeModelRoot -Repository $repository -Revision $entry.revision -FileName $nestedPath
        }
        foreach ($file in $candidatePaths) {
            # The manifest contains historical alternatives; uninstalled models are optional.
            if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { continue }
            if ((Get-Item -LiteralPath $file).Length -ne [long]$entry.length) { throw "Pinned model length mismatch: $file" }
            $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($hash -ne ([string]$entry.sha256).ToLowerInvariant()) { throw "Pinned model SHA-256 mismatch: $file" }
            Write-Host "Verified $file" -ForegroundColor DarkGray
        }
    }
}

$nativeModels = @(Get-GoNativeModelCatalog -NativeModelRoot $paths.NativeModelRoot)
foreach ($role in @('general', 'vision', 'embedding')) {
    if (@($nativeModels | Where-Object { $_.role -eq $role }).Count -eq 0) {
        throw "No complete native $role model is installed in $($paths.NativeModelRoot). Vision models require a matching projector."
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
    & (Join-Path $PSScriptRoot 'build-ai-stack.ps1') -DataRoot $paths.DataRoot -ModelRoot $paths.ModelRoot -NativeModelRoot $paths.NativeModelRoot -ServerIp $ServerIp -ImageVersion $ImageVersion
}
if (-not $SkipStart) {
    & (Join-Path $PSScriptRoot 'start-ai-stack.ps1') -DataRoot $paths.DataRoot -ModelRoot $paths.ModelRoot -NativeModelRoot $paths.NativeModelRoot -ServerIp $ServerIp -ImageVersion $ImageVersion
}
Write-Host 'GO AI deployment completed: Docker gateway/workers plus native Unsloth / llama.cpp models.' -ForegroundColor Green
