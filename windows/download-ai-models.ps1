#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $DataRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'GO-AI-Server'),

    [switch] $SkipLmStudioModels,

    [switch] $SkipWorkerModels
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

    $whisperRoot = Join-Path $modelRoot 'faster-whisper-large-v3'
    $speakerRoot = Join-Path $modelRoot 'spkrec-ecapa-voxceleb'
    $ttsRoot = Join-Path $modelRoot 'piper\de_DE-kerstin-low'
    $supertonicRoot = Join-Path $modelRoot 'supertonic-3'
    $zImageRoot = Join-Path $modelRoot 'z-image'
    New-Item -ItemType Directory -Path $zImageRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $ttsRoot -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $supertonicRoot 'onnx') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $supertonicRoot 'voice_styles') -Force | Out-Null

    Invoke-CheckedCommand -FilePath $hf -Arguments @(
        'download', 'Systran/faster-whisper-large-v3',
        '--revision', 'edaa852ec7e145841d8ffdb056a99866b5f0a478',
        '--exclude', 'model.bin',
        '--local-dir', $whisperRoot
    ) -FailureMessage 'Faster Whisper CTranslate2 model download failed.'
    Invoke-PinnedModelFileDownload `
        -Repository 'Systran/faster-whisper-large-v3' `
        -Revision 'edaa852ec7e145841d8ffdb056a99866b5f0a478' `
        -FileName 'model.bin' `
        -Destination (Join-Path $whisperRoot 'model.bin') `
        -ExpectedLength 3087284237 `
        -Sha256 '69f74147e3334731bc3a76048724833325d2ec74642fb52620eda87352e3d4f1' `
        -StagingDirectory $downloadRoot
    Invoke-CheckedCommand -FilePath $hf -Arguments @(
        'download', 'speechbrain/spkrec-ecapa-voxceleb',
        '--revision', '0f99f2d0ebe89ac095bcc5903c4dd8f72b367286',
        '--exclude', 'example1.wav', 'example2.flac', 'README.md',
        '--local-dir', $speakerRoot
    ) -FailureMessage 'ECAPA speaker-recognition model download failed.'
    Invoke-PinnedModelFileDownload `
        -Repository 'rhasspy/piper-voices' `
        -Revision '664c651454f055ed34bd83f09e024ffbc0da09ac' `
        -FileName 'de/de_DE/kerstin/low/de_DE-kerstin-low.onnx' `
        -Destination (Join-Path $ttsRoot 'de_DE-kerstin-low.onnx') `
        -ExpectedLength 63104526 `
        -Sha256 'd352a7641892cebf2903859af94e9ba81a141110215fe3943bcda7f7da401b7a' `
        -StagingDirectory $downloadRoot
    Invoke-PinnedModelFileDownload `
        -Repository 'rhasspy/piper-voices' `
        -Revision '664c651454f055ed34bd83f09e024ffbc0da09ac' `
        -FileName 'de/de_DE/kerstin/low/de_DE-kerstin-low.onnx.json' `
        -Destination (Join-Path $ttsRoot 'de_DE-kerstin-low.onnx.json') `
        -ExpectedLength 5952 `
        -Sha256 '370e4a87c1d3df1f1b2d251e75d750cf3f9d869563d5fbf7fa1ced557bfefa8d' `
        -StagingDirectory $downloadRoot
    $supertonicRevision = '3cadd1ee6394adea1bd021217a0e650ede09a323'
    $supertonicFiles = @(
        @{ File = 'onnx/duration_predictor.onnx'; Length = 3700147; Sha256 = 'c3eb91414d5ff8a7a239b7fe9e34e7e2bf8a8140d8375ffb14718b1c639325db' },
        @{ File = 'onnx/text_encoder.onnx'; Length = 36416150; Sha256 = 'c7befd5ea8c3119769e8a6c1486c4edc6a3bc8365c67621c881bbb774b9902ff' },
        @{ File = 'onnx/vector_estimator.onnx'; Length = 256534781; Sha256 = '883ac868ea0275ef0e991524dc64f16b3c0376efd7c320af6b53f5b780d7c61c' },
        @{ File = 'onnx/vocoder.onnx'; Length = 101424195; Sha256 = '085de76dd8e8d5836d6ca66826601f615939218f90e519f70ee8a36ed2a4c4ba' },
        @{ File = 'onnx/tts.json'; Length = 8253; Sha256 = '42078d3aef1cd43ab43021f3c54f47d2d75ceb4e75f627f118890128b06a0d09' },
        @{ File = 'onnx/unicode_indexer.json'; Length = 277676; Sha256 = '9bf7346e43883a81f8645c81224f786d43c5b57f3641f6e7671a7d6c493cb24f' },
        @{ File = 'voice_styles/F5.json'; Length = 291479; Sha256 = '45966e73316415626cf41a7d1c6f3b4c70dbc1ba2bee5c1978ef0ce33244fc8d' },
        @{ File = 'LICENSE'; Length = 15007; Sha256 = '0d944a9110fed9a9602d60e0423a272903e7bd21ab060490774efc77c2275e9f' }
    )
    foreach ($file in $supertonicFiles) {
        Invoke-PinnedModelFileDownload `
            -Repository 'Supertone/supertonic-3' `
            -Revision $supertonicRevision `
            -FileName $file.File `
            -Destination (Join-Path $supertonicRoot $file.File) `
            -ExpectedLength $file.Length `
            -Sha256 $file.Sha256 `
            -StagingDirectory $downloadRoot
    }
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
