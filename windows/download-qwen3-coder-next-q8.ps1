#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $DataRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'GO-AI-Stack'),

    [string] $ModelRoot,

    [string] $NativeModelRoot = (Join-Path $env:USERPROFILE '.cache\huggingface\hub'),

    [switch] $PlanOnly
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$repository = 'Qwen/Qwen3-Coder-Next-GGUF'
$revision = 'b82fb7382639d97b38fa7672e526c760c2fb358e'
$variant = 'Qwen3-Coder-Next-Q8_0'
$files = @(
    [pscustomobject]@{
        Name = 'Qwen3-Coder-Next-Q8_0-00001-of-00004.gguf'
        Length = [long] 26190321568
        Sha256 = '30b7554fc0c846a5dc3ecf585884c77471f73e3da698a8ba4fabd8e7868c6533'
    },
    [pscustomobject]@{
        Name = 'Qwen3-Coder-Next-Q8_0-00002-of-00004.gguf'
        Length = [long] 26212935584
        Sha256 = '3f96379de5a5c4655cb378710ea571d5e9cc96f260120a44a6477198efcdc27d'
    },
    [pscustomobject]@{
        Name = 'Qwen3-Coder-Next-Q8_0-00003-of-00004.gguf'
        Length = [long] 26047157792
        Sha256 = '5dd1ce07eaae95ee430331dc9c6f3120ff88e4211ad3a0cceeaa963f25328504'
    },
    [pscustomobject]@{
        Name = 'Qwen3-Coder-Next-Q8_0-00004-of-00004.gguf'
        Length = [long] 6361641024
        Sha256 = '76730702c630bf76305139165cb85421858604030851dfd64fe96a5e67cda99d'
    }
)

function Assert-DownloadedFile {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [long] $ExpectedLength,
        [Parameter(Mandatory = $true)] [string] $ExpectedSha256
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Downloaded file is missing: $Path"
    }

    $file = Get-Item -LiteralPath $Path
    if ($file.Length -ne $ExpectedLength) {
        throw "Unexpected file length for '$Path'. Expected $ExpectedLength bytes, got $($file.Length)."
    }

    Write-Host "Verifying SHA-256: $($file.Name)" -ForegroundColor DarkGray
    $actualSha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not [string]::Equals($actualSha256, $ExpectedSha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "SHA-256 mismatch for '$Path'. Expected $ExpectedSha256, got $actualSha256."
    }
}

function Receive-PinnedShard {
    param(
        [Parameter(Mandatory = $true)] $Shard,
        [Parameter(Mandatory = $true)] [string] $DestinationDirectory,
        [Parameter(Mandatory = $true)] [string] $StagingDirectory,
        [Parameter(Mandatory = $true)] [System.Management.Automation.CommandInfo] $Curl
    )

    $destination = Join-Path $DestinationDirectory $Shard.Name
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        Assert-DownloadedFile `
            -Path $destination `
            -ExpectedLength $Shard.Length `
            -ExpectedSha256 $Shard.Sha256
        Write-Host "Pinned shard already present: $destination" -ForegroundColor DarkGray
        return
    }

    $partialPath = Join-Path $StagingDirectory ($Shard.Name + '.part')
    if (-not (Test-Path -LiteralPath $partialPath -PathType Leaf)) {
        New-Item -ItemType File -Path $partialPath -Force | Out-Null
    }

    $partialLength = (Get-Item -LiteralPath $partialPath).Length
    if ($partialLength -gt $Shard.Length) {
        throw "Partial shard exceeds its pinned length: $partialPath"
    }
    if ($partialLength -eq $Shard.Length) {
        Assert-DownloadedFile `
            -Path $partialPath `
            -ExpectedLength $Shard.Length `
            -ExpectedSha256 $Shard.Sha256
        Move-Item -LiteralPath $partialPath -Destination $destination
        Write-Host "Pinned shard downloaded: $destination" -ForegroundColor Green
        return
    }

    $remotePath = "$variant/$($Shard.Name)"
    $url = "https://huggingface.co/$repository/resolve/$revision/${remotePath}?download=true"
    for ($attempt = 1; $attempt -le 12; $attempt++) {
        $downloaded = (Get-Item -LiteralPath $partialPath).Length
        $progress = [Math]::Round(100.0 * [double] $downloaded / [double] $Shard.Length, 1)
        Write-Host "Downloading $($Shard.Name) ($progress %, attempt $attempt/12) ..." -ForegroundColor Cyan

        & $Curl.Source @(
            '--location',
            '--fail',
            '--show-error',
            '--retry', '4',
            '--retry-all-errors',
            '--retry-delay', '5',
            '--connect-timeout', '30',
            '--speed-limit', '1024',
            '--speed-time', '180',
            '--continue-at', '-',
            '--output', $partialPath,
            $url
        )
        $exitCode = $LASTEXITCODE
        $downloaded = (Get-Item -LiteralPath $partialPath).Length
        if ($exitCode -eq 0 -and $downloaded -eq $Shard.Length) {
            break
        }
        if ($downloaded -gt $Shard.Length) {
            throw "Downloaded shard exceeds its pinned length: $partialPath"
        }
        if ($attempt -eq 12) {
            throw "Pinned shard download failed after $attempt attempts (curl exit $exitCode): $($Shard.Name)"
        }

        Start-Sleep -Seconds ([Math]::Min(60, $attempt * 5))
    }

    Assert-DownloadedFile `
        -Path $partialPath `
        -ExpectedLength $Shard.Length `
        -ExpectedSha256 $Shard.Sha256
    Move-Item -LiteralPath $partialPath -Destination $destination
    Write-Host "Pinned shard downloaded: $destination" -ForegroundColor Green
}

$paths = Get-GoAiStackDefaults -DataRoot $DataRoot -ModelRoot $ModelRoot -NativeModelRoot $NativeModelRoot
$resolvedModelRoot = [IO.Path]::GetFullPath($paths.NativeModelRoot)
$firstShard = Resolve-GoNativeModelFile -NativeModelRoot $resolvedModelRoot -Repository $repository -Revision $revision -FileName ($variant + '/' + $files[0].Name)
$destinationDirectory = Split-Path $firstShard -Parent
$modelRootPrefix = $resolvedModelRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $destinationDirectory.StartsWith($modelRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Destination must stay below the native model root: $destinationDirectory"
}

$stagingDirectory = Join-Path $destinationDirectory '.download'
$totalLength = [long] 0
foreach ($file in $files) {
    $totalLength += [long] $file.Length
}

Write-Host 'Qwen3-Coder-Next Q8_0 download plan' -ForegroundColor Cyan
Write-Host "Repository: $repository@$revision"
Write-Host "Destination: $destinationDirectory"
Write-Host ("Download size: {0:N2} GiB" -f ($totalLength / 1GB))
Write-Host 'The native catalog assigns a stable local ID after all four shards are complete.'
foreach ($file in $files) {
    Write-Host ("  {0} | {1:N2} GiB | SHA-256 {2}" -f $file.Name, ($file.Length / 1GB), $file.Sha256) -ForegroundColor DarkGray
}

if ($PlanOnly) {
    Write-Host 'Plan only: no directories or files were changed.' -ForegroundColor Yellow
    return
}

$curl = Assert-GoCommand -Name 'curl.exe'
New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null

$remainingLength = [long] 0
foreach ($file in $files) {
    $destination = Join-Path $destinationDirectory $file.Name
    $partialPath = Join-Path $stagingDirectory ($file.Name + '.part')
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        continue
    }

    $partialLength = if (Test-Path -LiteralPath $partialPath -PathType Leaf) {
        (Get-Item -LiteralPath $partialPath).Length
    }
    else {
        [long] 0
    }
    if ($partialLength -gt $file.Length) {
        throw "Partial shard exceeds its pinned length: $partialPath"
    }
    $remainingLength += ([long] $file.Length - [long] $partialLength)
}

$driveRoot = [IO.Path]::GetPathRoot($destinationDirectory)
$drive = [IO.DriveInfo]::new($driveRoot)
$requiredFreeSpace = $remainingLength + 5GB
if ($drive.AvailableFreeSpace -lt $requiredFreeSpace) {
    throw ("Insufficient free space on {0}. Required including reserve: {1:N2} GiB; available: {2:N2} GiB." -f `
        $driveRoot, ($requiredFreeSpace / 1GB), ($drive.AvailableFreeSpace / 1GB))
}

foreach ($file in $files) {
    Receive-PinnedShard `
        -Shard $file `
        -DestinationDirectory $destinationDirectory `
        -StagingDirectory $stagingDirectory `
        -Curl $curl
}

$refsDirectory = Join-Path (Join-Path $resolvedModelRoot ('models--' + $repository.Replace('/', '--'))) 'refs'
New-Item -ItemType Directory -Path $refsDirectory -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $refsDirectory 'main'), $revision, [Text.Encoding]::ASCII)
Write-Host 'Qwen3-Coder-Next Q8_0 download completed and verified in the native Unsloth cache.' -ForegroundColor Green
Write-Host 'Refresh the GO model catalog to select the model for General or Coding.' -ForegroundColor Yellow
