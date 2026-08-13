#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $DataRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'GO-AI-Server'),

    [switch] $SkipLmStudioModels,

    [switch] $SkipWorkerModels,

    [switch] $SkipVisionFallback
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)] [string] $FilePath,
        [Parameter(Mandatory = $true)] [string[]] $Arguments,
        [Parameter(Mandatory = $true)] [string] $FailureMessage
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage Exit code: $LASTEXITCODE"
    }
}

function Assert-FileHash {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [string] $Sha256
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Downloaded model file is missing: $Path"
    }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not [string]::Equals($actual, $Sha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "SHA-256 mismatch for $Path. Expected $Sha256, got $actual."
    }
}

function Invoke-PinnedModelFileDownload {
    param(
        [Parameter(Mandatory = $true)] [string] $Repository,
        [Parameter(Mandatory = $true)] [string] $Revision,
        [Parameter(Mandatory = $true)] [string] $FileName,
        [Parameter(Mandatory = $true)] [string] $Destination,
        [Parameter(Mandatory = $true)] [long] $ExpectedLength,
        [Parameter(Mandatory = $true)] [string] $Sha256,
        [Parameter(Mandatory = $true)] [string] $StagingDirectory
    )

    $Destination = [IO.Path]::GetFullPath($Destination)
    New-Item -ItemType Directory -Path (Split-Path $Destination -Parent) -Force | Out-Null
    if (Test-Path -LiteralPath $Destination -PathType Leaf) {
        $existing = Get-Item -LiteralPath $Destination
        if ($existing.Length -ne $ExpectedLength) {
            throw "Existing model file has an unexpected length: $Destination"
        }
        Assert-FileHash -Path $Destination -Sha256 $Sha256
        Write-Host "Pinned model file already present: $Destination" -ForegroundColor DarkGray
        return
    }

    $StagingDirectory = [IO.Path]::GetFullPath($StagingDirectory)
    New-Item -ItemType Directory -Path $StagingDirectory -Force | Out-Null
    # Never resume inside LM Studio's model tree. LM Studio owns its own
    # downloading_*.part files and may update or remove them concurrently.
    $partialPath = Join-Path $StagingDirectory ($Sha256.ToLowerInvariant() + '.part')
    if (-not (Test-Path -LiteralPath $partialPath -PathType Leaf)) {
        New-Item -ItemType File -Path $partialPath -Force | Out-Null
    }
    $partial = Get-Item -LiteralPath $partialPath
    if ($partial.Length -gt $ExpectedLength) {
        throw "Partial model file exceeds the pinned length: $partialPath"
    }

    $curl = Assert-GoCommand -Name 'curl.exe'
    $normalizedName = $FileName.Replace('\', '/')
    $url = "https://huggingface.co/$Repository/resolve/$Revision/${normalizedName}?download=true"
    for ($attempt = 1; $attempt -le 12; $attempt++) {
        $downloaded = (Get-Item -LiteralPath $partialPath).Length
        $progress = [Math]::Round(100.0 * $downloaded / $ExpectedLength, 1)
        Write-Host "Downloading $FileName ($progress %, attempt $attempt/12) ..." -ForegroundColor Cyan
        & $curl.Source @(
            '--location',
            '--fail',
            '--show-error',
            '--retry', '4',
            '--retry-all-errors',
            '--retry-delay', '5',
            '--connect-timeout', '30',
            '--speed-limit', '1024',
            '--speed-time', '120',
            '--continue-at', '-',
            '--output', $partialPath,
            $url
        )
        $exitCode = $LASTEXITCODE
        $downloaded = (Get-Item -LiteralPath $partialPath).Length
        if ($exitCode -eq 0 -and $downloaded -eq $ExpectedLength) {
            break
        }
        if ($downloaded -gt $ExpectedLength) {
            throw "Downloaded model file exceeds the pinned length: $partialPath"
        }
        if ($attempt -eq 12) {
            throw "Pinned model download failed after $attempt attempts (curl exit $exitCode): $FileName"
        }
        Start-Sleep -Seconds ([Math]::Min(60, $attempt * 5))
    }

    Assert-FileHash -Path $partialPath -Sha256 $Sha256
    Move-Item -LiteralPath $partialPath -Destination $Destination
    Write-Host "Pinned model file downloaded: $Destination" -ForegroundColor Green
}

$DataRoot = [IO.Path]::GetFullPath($DataRoot)
$modelRoot = Join-Path $DataRoot 'Models'
$downloadRoot = Join-Path $DataRoot 'Downloads'
$toolRoot = Join-Path $DataRoot 'Tools\huggingface'
New-Item -ItemType Directory -Path $modelRoot -Force | Out-Null

if (-not $SkipLmStudioModels) {
    $lmModelRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) '.lmstudio\models'
    $lmFiles = @(
        [pscustomobject]@{
            Repository = 'Qwen/Qwen3-VL-30B-A3B-Instruct-GGUF'
            Revision = 'f54435e6cc31258f04b0969105c3f6badb197931'
            FileName = 'Qwen3VL-30B-A3B-Instruct-Q4_K_M.gguf'
            RelativePath = 'Qwen\Qwen3-VL-30B-A3B-Instruct-GGUF\Qwen3VL-30B-A3B-Instruct-Q4_K_M.gguf'
            Length = 18556687168
            Sha256 = '87bb374d849f80ebdfabb304189fac9e0bd35a0f74506e6a59c51b206cbe863b'
        },
        [pscustomobject]@{
            Repository = 'Qwen/Qwen3-VL-30B-A3B-Instruct-GGUF'
            Revision = 'f54435e6cc31258f04b0969105c3f6badb197931'
            FileName = 'mmproj-Qwen3VL-30B-A3B-Instruct-F16.gguf'
            RelativePath = 'Qwen\Qwen3-VL-30B-A3B-Instruct-GGUF\mmproj-Qwen3VL-30B-A3B-Instruct-F16.gguf'
            ExistingPartialPath = $null
            Length = 1083499584
            Sha256 = 'cae72cf123cc9e08d553cd5a5055d6d3cf0f82652aa41c3e4aa424cda9a26f7f'
        },
        [pscustomobject]@{
            Repository = 'ggml-org/bge-m3-Q8_0-GGUF'
            Revision = '9eba04c5d75ba5a1595e45de734d36bef4e5cb98'
            FileName = 'bge-m3-q8_0.gguf'
            RelativePath = 'ggml-org\bge-m3-Q8_0-GGUF\bge-m3-q8_0.gguf'
            ExistingPartialPath = $null
            Length = 634553760
            Sha256 = 'aa473d51f451a22f0fcf39ba3330c14bed38a385712b1113440f69df4047a173'
        }
    )
    if (-not $SkipVisionFallback) {
        $lmFiles += @(
            [pscustomobject]@{
                Repository = 'Qwen/Qwen3-VL-8B-Instruct-GGUF'
                Revision = 'f982a07559d4a2f6c8744d840bf6fccab30eea96'
                FileName = 'Qwen3VL-8B-Instruct-Q4_K_M.gguf'
                RelativePath = 'Qwen\Qwen3-VL-8B-Instruct-GGUF\Qwen3VL-8B-Instruct-Q4_K_M.gguf'
                ExistingPartialPath = $null
                Length = 5027784800
                Sha256 = '67d1659bfe71b89d50b45a4ad1a9e5b997e5bb16ce5da66a6a6167abd569e9e2'
            },
            [pscustomobject]@{
                Repository = 'Qwen/Qwen3-VL-8B-Instruct-GGUF'
                Revision = 'f982a07559d4a2f6c8744d840bf6fccab30eea96'
                FileName = 'mmproj-Qwen3VL-8B-Instruct-F16.gguf'
                RelativePath = 'Qwen\Qwen3-VL-8B-Instruct-GGUF\mmproj-Qwen3VL-8B-Instruct-F16.gguf'
                ExistingPartialPath = $null
                Length = 1159029824
                Sha256 = 'ca524100ebf825c9a870db1c580d03879e0da0ab2541697e2458e64891cf9d38'
            }
        )
    }
    foreach ($file in $lmFiles) {
        $arguments = @{
            Repository = $file.Repository
            Revision = $file.Revision
            FileName = $file.FileName
            Destination = (Join-Path $lmModelRoot $file.RelativePath)
            ExpectedLength = $file.Length
            Sha256 = $file.Sha256
            StagingDirectory = $downloadRoot
        }
        Invoke-PinnedModelFileDownload @arguments
    }

    $requiredCatalogModels = @('qwen3-vl-30b-a3b-instruct', 'bge-m3')
    if (-not $SkipVisionFallback) {
        $requiredCatalogModels += 'qwen3-vl-8b-instruct'
    }
    & (Join-Path $PSScriptRoot 'refresh-lmstudio-model-catalog.ps1') `
        -RequiredNameFragments $requiredCatalogModels
}

if (-not $SkipWorkerModels) {
    $python = Assert-GoCommand -Name 'python'
    $venvPython = Join-Path $toolRoot 'Scripts\python.exe'
    if (-not (Test-Path -LiteralPath $venvPython -PathType Leaf)) {
        New-Item -ItemType Directory -Path (Split-Path $toolRoot -Parent) -Force | Out-Null
        Invoke-CheckedCommand -FilePath $python.Source -Arguments @('-m', 'venv', $toolRoot) -FailureMessage 'Unable to create the Hugging Face download environment.'
        Invoke-CheckedCommand -FilePath $venvPython -Arguments @('-m', 'pip', 'install', '--disable-pip-version-check', 'huggingface_hub==0.36.0') -FailureMessage 'Unable to install the pinned Hugging Face client.'
    }

    $hf = Join-Path $toolRoot 'Scripts\hf.exe'
    if (-not (Test-Path -LiteralPath $hf -PathType Leaf)) {
        throw "Hugging Face client is missing: $hf"
    }

    $whisperRoot = Join-Path $modelRoot 'faster-whisper-large-v3-turbo'
    $ttsRoot = Join-Path $modelRoot 'Qwen3-TTS-12Hz-0.6B-Base'
    $zImageRoot = Join-Path $modelRoot 'z-image'
    New-Item -ItemType Directory -Path $zImageRoot -Force | Out-Null

    Invoke-CheckedCommand -FilePath $hf -Arguments @(
        'download', 'dropbox-dash/faster-whisper-large-v3-turbo',
        '--revision', '0a363e9161cbc7ed1431c9597a8ceaf0c4f78fcf',
        '--exclude', 'model.bin',
        '--local-dir', $whisperRoot
    ) -FailureMessage 'Faster Whisper CTranslate2 model download failed.'
    Invoke-PinnedModelFileDownload `
        -Repository 'dropbox-dash/faster-whisper-large-v3-turbo' `
        -Revision '0a363e9161cbc7ed1431c9597a8ceaf0c4f78fcf' `
        -FileName 'model.bin' `
        -Destination (Join-Path $whisperRoot 'model.bin') `
        -ExpectedLength 1617884929 `
        -Sha256 'e76620f83d5f5b69efd3d87e3dc180c1bd21df9fbebacfd4335e5e1efcc018da' `
        -StagingDirectory $downloadRoot
    Invoke-CheckedCommand -FilePath $hf -Arguments @(
        'download', 'Qwen/Qwen3-TTS-12Hz-0.6B-Base',
        '--revision', '5d83992436eae1d760afd27aff78a71d676296fc',
        '--local-dir', $ttsRoot
    ) -FailureMessage 'Qwen3-TTS model download failed.'
    Invoke-CheckedCommand -FilePath $hf -Arguments @(
        'download', 'leejet/Z-Image-Turbo-GGUF', 'z_image_turbo-Q4_K.gguf',
        '--revision', 'c61c0e422dc8b541b7548cf33a4ef8302b0f8085',
        '--local-dir', $zImageRoot
    ) -FailureMessage 'Z-Image model download failed.'
    Invoke-CheckedCommand -FilePath $hf -Arguments @(
        'download', 'Comfy-Org/z_image_turbo', 'split_files/vae/ae.safetensors',
        '--revision', 'd24c4cf2a0cd98a42f23467e27e3d76ee9438b8e',
        '--local-dir', $zImageRoot
    ) -FailureMessage 'Z-Image VAE download failed.'
    Invoke-CheckedCommand -FilePath $hf -Arguments @(
        'download', 'unsloth/Qwen3-4B-Instruct-2507-GGUF', 'Qwen3-4B-Instruct-2507-Q4_K_M.gguf',
        '--revision', 'a06e946bb6b655725eafa393f4a9745d460374c9',
        '--local-dir', $zImageRoot
    ) -FailureMessage 'Z-Image text encoder download failed.'

    $downloadedVae = Join-Path $zImageRoot 'split_files\vae\ae.safetensors'
    $flatVae = Join-Path $zImageRoot 'ae.safetensors'
    Copy-Item -LiteralPath $downloadedVae -Destination $flatVae -Force
    Assert-FileHash (Join-Path $zImageRoot 'z_image_turbo-Q4_K.gguf') '14b375ab4f226bc5378f68f37e899ef3c2242b8541e61e2bc1aff40976086fbd'
    Assert-FileHash $flatVae 'afc8e28272cd15db3919bacdb6918ce9c1ed22e96cb12c4d5ed0fba823529e38'
    Assert-FileHash (Join-Path $zImageRoot 'Qwen3-4B-Instruct-2507-Q4_K_M.gguf') '3605803b982cb64aead44f6c1b2ae36e3acdb41d8e46c8a94c6533bc4c67e597'
}

Write-Host "GO AI model downloads are complete: $modelRoot" -ForegroundColor Green
