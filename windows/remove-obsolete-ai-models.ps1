#requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [string] $DockerModelRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'GO-AI-Stack\Models'),
    [string] $LmStudioModelRoot = (Join-Path $env:USERPROFILE '.lmstudio\models')
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$dockerRoot = [IO.Path]::GetFullPath($DockerModelRoot).TrimEnd('\')
$lmRoot = [IO.Path]::GetFullPath($LmStudioModelRoot).TrimEnd('\')
$requiredLmResources = @(
    'lmstudio-community\gpt-oss-120b-GGUF\gpt-oss-120b-MXFP4-00001-of-00002.gguf',
    'lmstudio-community\gpt-oss-120b-GGUF\gpt-oss-120b-MXFP4-00002-of-00002.gguf',
    'lmstudio-community\Qwen3.8-27B-GGUF\Qwen3.8-27B-Q4_K_M.gguf',
    'Qwen\Qwen3-Coder-Next-GGUF\Qwen3-Coder-Next-Q8_0-00001-of-00004.gguf',
    'Qwen\Qwen3-VL-30B-A3B-Instruct-GGUF\Qwen3VL-30B-A3B-Instruct-Q4_K_M.gguf',
    'ggml-org\bge-m3-Q8_0-GGUF\bge-m3-q8_0.gguf'
)
$requiredWorkerResources = @(
    'speech\faster-whisper-large-v3\model.bin',
    'speech\spkrec-ecapa-voxceleb',
    'speech\supertonic-3\onnx\vocoder.onnx',
    'image\z-image\z_image_turbo-Q4_K.gguf'
)
foreach ($relativePath in $requiredLmResources) {
    $resource = Join-Path $lmRoot $relativePath
    if (-not (Test-Path -LiteralPath $resource)) {
        throw "LM Studio model stock is incomplete; cleanup aborted: $resource"
    }
}
foreach ($relativePath in $requiredWorkerResources) {
    $resource = Join-Path $dockerRoot $relativePath
    if (-not (Test-Path -LiteralPath $resource)) {
        throw "Docker worker model stock is incomplete; cleanup aborted: $resource"
    }
}

$dockerPrefix = $dockerRoot + '\'
foreach ($relativePath in @('llm', 'vision', 'embedding')) {
    $target = [IO.Path]::GetFullPath((Join-Path $dockerRoot $relativePath))
    if (-not $target.StartsWith($dockerPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe cleanup target: $target"
    }
    if ((Test-Path -LiteralPath $target) -and $PSCmdlet.ShouldProcess($target, 'Remove obsolete Docker model-runtime directory')) {
        Remove-Item -LiteralPath $target -Recurse -Force
        Write-Host "Removed obsolete Docker model-runtime directory: $target" -ForegroundColor Green
    }
}

Write-Host 'LM Studio models and Docker worker resources remain intact.' -ForegroundColor Cyan
