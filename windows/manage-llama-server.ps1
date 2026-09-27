#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Start', 'Stop', 'Status', 'Update', 'ConfigureFirewall')]
    [string] $Action = 'Start',

    [string] $ModelPath = 'C:\Users\AMD\.lmstudio\models\lmstudio-community\Qwen3.8-27B-GGUF\Qwen3.8-27B-Q8_0.gguf',

    [string] $InstallRoot = (Join-Path $env:LOCALAPPDATA 'GO-LlamaServer'),

    [ValidateRange(1, 65535)]
    [int] $Port = 8081,

    [ValidateRange(512, 1048576)]
    [int] $ContextSize = 65536,

    [string] $CudaVersion = '12.4',

    [string] $TensorSplit = '1,1',

    [string] $ResolvedPathFile,

    [string] $GitHubApiBase = 'https://api.github.com',

    [switch] $SkipFirewall,

    [ValidateRange(1, 3600)]
    [int] $StartupTimeoutSeconds = 1200
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repository = 'ggml-org/llama.cpp'
$firewallRuleName = "GO llama.cpp Server $Port (Private LAN)"
$pidPath = Join-Path $InstallRoot 'llama-server.pid'
$statePath = Join-Path $InstallRoot 'server-state.json'
$logDirectory = Join-Path $InstallRoot 'logs'
$updateLock = $null

function Enter-UpdateLock {
    New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
    $lockPath = Join-Path $InstallRoot 'update.lock'
    $deadline = [DateTimeOffset]::Now.AddMinutes(2)
    while ([DateTimeOffset]::Now -lt $deadline) {
        try {
            return [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        } catch [IO.IOException] {
            Start-Sleep -Milliseconds 500
        }
    }
    throw "Another llama.cpp update still owns '$lockPath'."
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal] $identity
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-GitHubRequest {
    param([Parameter(Mandatory = $true)] [string] $Uri)

    return Invoke-RestMethod -Uri $Uri -Headers @{
        Accept = 'application/vnd.github+json'
        'User-Agent' = 'GO-WinUI-llama-server-installer'
        'X-GitHub-Api-Version' = '2022-11-28'
    } -TimeoutSec 60
}

function Get-Utf8ResponseText {
    param([Parameter(Mandatory = $true)] $Response)

    if ($Response.Content -is [byte[]]) {
        return [Text.Encoding]::UTF8.GetString([byte[]] $Response.Content)
    }
    return [string] $Response.Content
}

function Get-LatestBinaryRelease {
    $apiBase = $GitHubApiBase.TrimEnd('/')
    $release = Invoke-GitHubRequest -Uri "$apiBase/repos/$repository/releases/latest"
    $pointerAsset = @($release.assets | Where-Object { $_.name -eq 'nightly-tag.txt' } | Select-Object -First 1)
    if ($pointerAsset.Count -eq 0) {
        return $release
    }

    $pointerResponse = Invoke-WebRequest -Uri $pointerAsset[0].browser_download_url -UseBasicParsing -TimeoutSec 60
    $tag = (Get-Utf8ResponseText -Response $pointerResponse).Trim()
    if ([string]::IsNullOrWhiteSpace($tag) -or $tag -notmatch '^[A-Za-z0-9._-]+$') {
        throw "The llama.cpp nightly release pointer returned an invalid tag: '$tag'."
    }

    return Invoke-GitHubRequest -Uri "$apiBase/repos/$repository/releases/tags/$tag"
}

function Get-RequiredAsset {
    param(
        [Parameter(Mandatory = $true)] $Release,
        [Parameter(Mandatory = $true)] [string] $Name
    )

    $asset = @($Release.assets | Where-Object { $_.name -eq $Name } | Select-Object -First 1)
    if ($asset.Count -ne 1) {
        throw "The release '$($Release.tag_name)' does not contain '$Name'."
    }
    return $asset[0]
}

function Assert-AssetDigest {
    param(
        [Parameter(Mandatory = $true)] $Asset,
        [Parameter(Mandatory = $true)] [string] $Path
    )

    $digest = [string] $Asset.digest
    if ([string]::IsNullOrWhiteSpace($digest)) {
        throw "GitHub did not publish a SHA-256 digest for '$($Asset.name)'. Automatic installation was refused."
    }
    if ($digest -notmatch '^sha256:(?<hash>[0-9a-fA-F]{64})$') {
        throw "Unsupported GitHub asset digest: $digest"
    }

    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if (-not [string]::Equals($actual, $Matches.hash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "SHA-256 mismatch for '$Path'. Expected $($Matches.hash), got $actual."
    }
}

function Receive-ReleaseAsset {
    param(
        [Parameter(Mandatory = $true)] $Asset,
        [Parameter(Mandatory = $true)] [string] $Destination
    )

    if (Test-Path -LiteralPath $Destination -PathType Leaf) {
        $existing = Get-Item -LiteralPath $Destination
        if ($existing.Length -eq [long] $Asset.size) {
            Assert-AssetDigest -Asset $Asset -Path $Destination
            Write-Host "Release asset already present: $Destination" -ForegroundColor DarkGray
            return
        }
        throw "An incomplete release asset exists at '$Destination'. Remove it before retrying."
    }

    $partial = "$Destination.part"
    if (-not (Test-Path -LiteralPath $partial -PathType Leaf)) {
        New-Item -ItemType File -Path $partial -Force | Out-Null
    }
    if ((Get-Item -LiteralPath $partial).Length -gt [long] $Asset.size) {
        $quarantine = "$partial.corrupt-" + [DateTimeOffset]::UtcNow.ToString('yyyyMMddHHmmss')
        Move-Item -LiteralPath $partial -Destination $quarantine
        New-Item -ItemType File -Path $partial -Force | Out-Null
        Write-Warning "An oversized partial download was quarantined as '$quarantine'."
    }

    Write-Host "Downloading $($Asset.name) ..." -ForegroundColor Cyan
    & curl.exe @(
        '--location',
        '--fail',
        '--show-error',
        '--retry', '8',
        '--retry-all-errors',
        '--retry-delay', '5',
        '--connect-timeout', '30',
        '--speed-limit', '1024',
        '--speed-time', '120',
        '--continue-at', '-',
        '--output', $partial,
        [string] $Asset.browser_download_url
    )
    if ($LASTEXITCODE -ne 0) {
        throw "Download failed with curl exit code ${LASTEXITCODE}: $($Asset.name)"
    }
    if ((Get-Item -LiteralPath $partial).Length -ne [long] $Asset.size) {
        throw "Downloaded asset length does not match GitHub metadata: $($Asset.name)"
    }

    Assert-AssetDigest -Asset $Asset -Path $partial
    Move-Item -LiteralPath $partial -Destination $Destination
}

function Install-LatestLlamaServer {
    New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
    $release = Get-LatestBinaryRelease
    $tag = [string] $release.tag_name
    if ($tag -notmatch '^[A-Za-z0-9._-]+$') {
        throw "The release contains an invalid tag: '$tag'."
    }

    $binaryName = "llama-$tag-bin-win-cuda-$CudaVersion-x64.zip"
    $cudaName = "cudart-llama-bin-win-cuda-$CudaVersion-x64.zip"
    $binaryAsset = Get-RequiredAsset -Release $release -Name $binaryName
    $cudaAsset = Get-RequiredAsset -Release $release -Name $cudaName
    $downloadDirectory = Join-Path $InstallRoot 'downloads'
    $versionDirectory = Join-Path (Join-Path $InstallRoot 'versions') $tag
    $binaryZip = Join-Path $downloadDirectory $binaryName
    $cudaZip = Join-Path $downloadDirectory $cudaName
    New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null

    $installedServer = Get-ChildItem -LiteralPath $versionDirectory -Filter 'llama-server.exe' -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $installedServer) {
        if (Test-Path -LiteralPath $versionDirectory) {
            throw "An incomplete version directory exists: $versionDirectory"
        }

        Receive-ReleaseAsset -Asset $binaryAsset -Destination $binaryZip
        Receive-ReleaseAsset -Asset $cudaAsset -Destination $cudaZip

        $staging = Join-Path (Join-Path $InstallRoot 'staging') ("$tag-" + [Guid]::NewGuid().ToString('N'))
        $binaryStaging = Join-Path $staging 'binary'
        $cudaStaging = Join-Path $staging 'cuda'
        New-Item -ItemType Directory -Path $binaryStaging -Force | Out-Null
        New-Item -ItemType Directory -Path $cudaStaging -Force | Out-Null
        Expand-Archive -LiteralPath $binaryZip -DestinationPath $binaryStaging -Force
        Expand-Archive -LiteralPath $cudaZip -DestinationPath $cudaStaging -Force

        $stagedServer = Get-ChildItem -LiteralPath $binaryStaging -Filter 'llama-server.exe' -File -Recurse | Select-Object -First 1
        if ($null -eq $stagedServer) {
            throw "The official binary archive did not contain llama-server.exe."
        }
        $serverDirectory = $stagedServer.Directory.FullName
        foreach ($cudaFile in (Get-ChildItem -LiteralPath $cudaStaging -File -Recurse)) {
            Copy-Item -LiteralPath $cudaFile.FullName -Destination (Join-Path $serverDirectory $cudaFile.Name) -Force
        }

        New-Item -ItemType Directory -Path (Split-Path $versionDirectory -Parent) -Force | Out-Null
        Move-Item -LiteralPath $binaryStaging -Destination $versionDirectory
        $installedServer = Get-ChildItem -LiteralPath $versionDirectory -Filter 'llama-server.exe' -File -Recurse | Select-Object -First 1
        Remove-Item -LiteralPath $staging -Recurse -Force
    }

    $serverSha256 = (Get-FileHash -LiteralPath $installedServer.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $previousManifestPath = Join-Path $InstallRoot 'current-release.json'
    $previousManifest = if (Test-Path -LiteralPath $previousManifestPath -PathType Leaf) {
        try { Get-Content -LiteralPath $previousManifestPath -Raw | ConvertFrom-Json } catch { $null }
    } else { $null }
    $previousServerHash = if ($null -ne $previousManifest -and
        $null -ne $previousManifest.PSObject.Properties['serverSha256']) { [string]$previousManifest.serverSha256 } else { '' }
    if ($null -ne $previousManifest -and [string]$previousManifest.releaseTag -eq $tag -and
        -not [string]::IsNullOrWhiteSpace($previousServerHash)) {
        if (-not [string]::Equals($serverSha256, [string]$previousManifest.serverSha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw "The installed llama-server.exe differs from the verified manifest for '$tag'."
        }
    } else {
        if (-not (Test-Path -LiteralPath $binaryZip -PathType Leaf) -or -not (Test-Path -LiteralPath $cudaZip -PathType Leaf)) {
            throw "The installed llama.cpp version '$tag' has no verifiable manifest or release archives."
        }
        Assert-AssetDigest -Asset $binaryAsset -Path $binaryZip
        Assert-AssetDigest -Asset $cudaAsset -Path $cudaZip
    }

    $metadata = [ordered]@{
        repository = $repository
        releaseTag = $tag
        releaseUrl = [string] $release.html_url
        publishedAt = [string] $release.published_at
        cudaVersion = $CudaVersion
        serverPath = $installedServer.FullName
        serverSha256 = $serverSha256
        binaryAssetDigest = [string] $binaryAsset.digest
        cudaAssetDigest = [string] $cudaAsset.digest
        installedAt = [DateTimeOffset]::Now.ToString('O')
    }
    $metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $InstallRoot 'current-release.json') -Encoding UTF8
    Write-Host "llama.cpp $tag is ready: $($installedServer.FullName)" -ForegroundColor Green
    return $installedServer.FullName
}

function Get-InstalledLlamaServer {
    $manifestPath = Join-Path $InstallRoot 'current-release.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { return $null }
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $candidate = [string] $manifest.serverPath
        $expectedHash = [string] $manifest.serverSha256
        if ([string]::IsNullOrWhiteSpace($candidate) -or -not [IO.Path]::IsPathRooted($candidate) -or
            -not (Test-Path -LiteralPath $candidate -PathType Leaf) -or
            [IO.Path]::GetFileName($candidate) -ne 'llama-server.exe' -or
            $expectedHash -notmatch '^[0-9a-fA-F]{64}$') { return $null }
        $root = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\') + '\'
        $resolved = [IO.Path]::GetFullPath($candidate)
        if (-not $resolved.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { return $null }
        $actualHash = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash
        if (-not [string]::Equals($actualHash, $expectedHash, [StringComparison]::OrdinalIgnoreCase)) { return $null }
        return $resolved
    } catch { return $null }
}

function Write-ResolvedServerPath {
    param([Parameter(Mandatory = $true)] [string] $ServerPath)
    if ([string]::IsNullOrWhiteSpace($ResolvedPathFile)) { return }
    $target = [IO.Path]::GetFullPath($ResolvedPathFile)
    $directory = Split-Path $target -Parent
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $temporary = $target + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    [IO.File]::WriteAllText($temporary, [IO.Path]::GetFullPath($ServerPath), [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $target -Force
}

function Set-LanFirewallRule {
    if (-not (Test-IsAdministrator)) {
        throw 'Administrator rights are required to configure the Windows firewall.'
    }

    Get-NetFirewallRule -DisplayName $firewallRuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule `
        -DisplayName $firewallRuleName `
        -Direction Inbound `
        -Action Allow `
        -Protocol TCP `
        -LocalPort $Port `
        -Profile Private `
        -RemoteAddress LocalSubnet | Out-Null
    Write-Host "Firewall rule created: $firewallRuleName" -ForegroundColor Green
}

function Ensure-LanFirewallRule {
    if ($SkipFirewall) {
        Write-Warning 'Firewall configuration was skipped. LAN clients may not be able to connect.'
        return
    }
    if (Get-NetFirewallRule -DisplayName $firewallRuleName -ErrorAction SilentlyContinue) {
        Write-Host "Firewall rule is present: $firewallRuleName" -ForegroundColor DarkGray
        return
    }
    if (Test-IsAdministrator) {
        Set-LanFirewallRule
        return
    }

    Write-Host 'Windows will request administrator approval for the Private/LocalSubnet firewall rule.' -ForegroundColor Yellow
    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', ('"' + $PSCommandPath + '"'),
        '-Action', 'ConfigureFirewall',
        '-Port', [string] $Port,
        '-InstallRoot', ('"' + $InstallRoot + '"')
    )
    $elevated = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs -Wait -PassThru
    if ($elevated.ExitCode -ne 0 -or -not (Get-NetFirewallRule -DisplayName $firewallRuleName -ErrorAction SilentlyContinue)) {
        throw 'The LAN firewall rule was not created. Approve the UAC prompt and retry.'
    }
}

function Get-ServerProcess {
    if (-not (Test-Path -LiteralPath $pidPath -PathType Leaf)) {
        return $null
    }
    $storedPid = 0
    if (-not [int]::TryParse((Get-Content -LiteralPath $pidPath -Raw).Trim(), [ref] $storedPid)) {
        return $null
    }
    return Get-Process -Id $storedPid -ErrorAction SilentlyContinue
}

function Get-LanAddress {
    $route = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
        Sort-Object RouteMetric, InterfaceMetric |
        Select-Object -First 1
    if ($null -eq $route) {
        return $null
    }
    return Get-NetIPAddress -InterfaceIndex $route.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notlike '169.254.*' } |
        Select-Object -ExpandProperty IPAddress -First 1
}

function Show-ServerStatus {
    $process = Get-ServerProcess
    $healthy = $false
    try {
        $health = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/health" -TimeoutSec 3
        $healthy = $null -ne $health
    } catch {
        $healthy = $false
    }
    $lanAddress = Get-LanAddress
    [pscustomobject]@{
        Running = $null -ne $process
        ProcessId = if ($null -ne $process) { $process.Id } else { $null }
        Healthy = $healthy
        LocalUrl = "http://127.0.0.1:$Port"
        LanUrl = if ($lanAddress) { "http://${lanAddress}:$Port" } else { $null }
        StateFile = $statePath
    } | Format-List
}

function Stop-LlamaServer {
    $process = Get-ServerProcess
    if ($null -ne $process) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
        Write-Host "Stopped llama-server process $($process.Id)." -ForegroundColor Green
    } else {
        Write-Host 'No managed llama-server process is running.' -ForegroundColor DarkGray
    }
    if (Test-Path -LiteralPath $pidPath) {
        Remove-Item -LiteralPath $pidPath -Force
    }
}

function Start-LlamaServer {
    if (-not (Test-Path -LiteralPath $ModelPath -PathType Leaf)) {
        throw "GGUF model not found: $ModelPath"
    }
    if (Get-ServerProcess) {
        Write-Host 'The managed llama-server is already running.' -ForegroundColor Yellow
        Show-ServerStatus
        return
    }
    $listener = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($listener) {
        throw "Port $Port is already used by process $($listener.OwningProcess)."
    }

    $serverPath = Install-LatestLlamaServer
    Ensure-LanFirewallRule
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $stdoutPath = Join-Path $logDirectory "$stamp-stdout.log"
    $stderrPath = Join-Path $logDirectory "$stamp-stderr.log"
    $arguments = @(
        '--model', ('"' + [IO.Path]::GetFullPath($ModelPath) + '"'),
        '--host', '0.0.0.0',
        '--port', [string] $Port,
        '--ctx-size', [string] $ContextSize,
        '--n-gpu-layers', '999',
        '--split-mode', 'layer',
        '--tensor-split', $TensorSplit,
        '--main-gpu', '0',
        '--flash-attn', 'on',
        '--parallel', '1',
        '--cont-batching',
        '--jinja',
        '--metrics'
    )

    $process = Start-Process `
        -FilePath $serverPath `
        -ArgumentList $arguments `
        -WorkingDirectory (Split-Path $serverPath -Parent) `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -WindowStyle Hidden `
        -PassThru
    Set-Content -LiteralPath $pidPath -Value $process.Id -Encoding ASCII

    $state = [ordered]@{
        processId = $process.Id
        modelPath = [IO.Path]::GetFullPath($ModelPath)
        port = $Port
        contextSize = $ContextSize
        tensorSplit = $TensorSplit
        startedAt = [DateTimeOffset]::Now.ToString('O')
        stdout = $stdoutPath
        stderr = $stderrPath
    }
    $state | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8

    Write-Host "llama-server process $($process.Id) is loading the model..." -ForegroundColor Cyan
    $deadline = [DateTimeOffset]::Now.AddSeconds($StartupTimeoutSeconds)
    $lastReport = [DateTimeOffset]::MinValue
    while ([DateTimeOffset]::Now -lt $deadline) {
        if ($process.HasExited) {
            $tail = if (Test-Path -LiteralPath $stderrPath) { Get-Content -LiteralPath $stderrPath -Tail 80 | Out-String } else { '' }
            throw "llama-server exited with code $($process.ExitCode).`n$tail"
        }
        try {
            $health = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/health" -TimeoutSec 3
            if ($null -ne $health) {
                $lanAddress = Get-LanAddress
                Write-Host "llama-server is ready locally: http://127.0.0.1:$Port" -ForegroundColor Green
                if ($lanAddress) {
                    Write-Host "llama-server is available on the private LAN: http://${lanAddress}:$Port" -ForegroundColor Green
                }
                return
            }
        } catch {
            # Loading is expected to return connection errors or HTTP 503.
        }
        if (([DateTimeOffset]::Now - $lastReport).TotalSeconds -ge 15) {
            Write-Host "Still loading; logs: $stderrPath" -ForegroundColor DarkGray
            $lastReport = [DateTimeOffset]::Now
        }
        Start-Sleep -Seconds 3
    }
    throw "llama-server did not become healthy within $StartupTimeoutSeconds seconds. Inspect '$stderrPath'."
}

switch ($Action) {
    'ConfigureFirewall' {
        Set-LanFirewallRule
    }
    'Update' {
        $updateLock = Enter-UpdateLock
        try {
            try {
                $resolvedServer = Install-LatestLlamaServer
            } catch {
                $resolvedServer = Get-InstalledLlamaServer
                if ($null -eq $resolvedServer) { throw }
                Write-Warning "GitHub update check failed; continuing with the last verified llama.cpp installation. $($_.Exception.Message)"
            }
            Write-ResolvedServerPath -ServerPath $resolvedServer
        } finally {
            if ($null -ne $updateLock) { $updateLock.Dispose() }
        }
    }
    'Start' {
        Start-LlamaServer
    }
    'Stop' {
        Stop-LlamaServer
    }
    'Status' {
        Show-ServerStatus
    }
}
