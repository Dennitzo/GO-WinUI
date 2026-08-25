#requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [string] $CurrentModelRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'GO-AI-Stack\Models')
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$currentRoot = [IO.Path]::GetFullPath($CurrentModelRoot)
$requiredResources = @(
    'llm\gpt-oss-120b-MXFP4-00001-of-00002.gguf'
    'llm\gpt-oss-120b-MXFP4-00002-of-00002.gguf'
    'llm\qwen3-coder-next-q8_0\Qwen3-Coder-Next-Q8_0-00001-of-00004.gguf'
    'llm\qwen3-coder-next-q8_0\Qwen3-Coder-Next-Q8_0-00002-of-00004.gguf'
    'llm\qwen3-coder-next-q8_0\Qwen3-Coder-Next-Q8_0-00003-of-00004.gguf'
    'llm\qwen3-coder-next-q8_0\Qwen3-Coder-Next-Q8_0-00004-of-00004.gguf'
    'vision\Qwen3VL-30B-A3B-Instruct-Q4_K_M.gguf'
    'vision\mmproj-Qwen3VL-30B-A3B-Instruct-F16.gguf'
    'embedding\bge-m3-q8_0.gguf'
    'speech\faster-whisper-large-v3\model.bin'
    'speech\spkrec-ecapa-voxceleb'
    'speech\supertonic-3\onnx\vocoder.onnx'
    'image\z-image\z_image_turbo-Q4_K.gguf'
)
foreach ($relativePath in $requiredResources) {
    $resource = Join-Path $currentRoot $relativePath
    if (-not (Test-Path -LiteralPath $resource)) {
        throw "Der aktive Docker-Modellbestand ist unvollständig. Bereinigung abgebrochen: $resource"
    }
}

$targets = @(
    [pscustomobject]@{ Path = (Join-Path $env:USERPROFILE '.lmstudio\models\ggml-org'); Parent = (Join-Path $env:USERPROFILE '.lmstudio\models') }
    [pscustomobject]@{ Path = (Join-Path $env:USERPROFILE '.lmstudio\models\lmstudio-community'); Parent = (Join-Path $env:USERPROFILE '.lmstudio\models') }
    [pscustomobject]@{ Path = (Join-Path $env:USERPROFILE '.lmstudio\models\Qwen'); Parent = (Join-Path $env:USERPROFILE '.lmstudio\models') }
    [pscustomobject]@{ Path = (Join-Path $env:ProgramData 'GO-AI-Server\Models'); Parent = (Join-Path $env:ProgramData 'GO-AI-Server') }
    [pscustomobject]@{ Path = (Join-Path $env:USERPROFILE '.cache\huggingface\hub\models--openai--gpt-oss-20b'); Parent = (Join-Path $env:USERPROFILE '.cache\huggingface\hub') }
    [pscustomobject]@{ Path = (Join-Path $env:USERPROFILE '.cache\huggingface\hub\.locks\models--openai--gpt-oss-20b'); Parent = (Join-Path $env:USERPROFILE '.cache\huggingface\hub\.locks') }
)

foreach ($target in $targets) {
    $resolvedTarget = [IO.Path]::GetFullPath($target.Path)
    $resolvedParent = [IO.Path]::GetFullPath($target.Parent).TrimEnd('\') + '\'
    if (-not $resolvedTarget.StartsWith($resolvedParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsicheres Bereinigungsziel: $resolvedTarget"
    }
    if (-not (Test-Path -LiteralPath $resolvedTarget)) {
        Write-Host "Bereits entfernt: $resolvedTarget" -ForegroundColor DarkGray
        continue
    }

    if ($PSCmdlet.ShouldProcess($resolvedTarget, 'Veralteten AI-Modellbestand dauerhaft entfernen')) {
        Remove-Item -LiteralPath $resolvedTarget -Recurse -Force
        Write-Host "Entfernt: $resolvedTarget" -ForegroundColor Green
    }
}

foreach ($relativePath in $requiredResources) {
    $resource = Join-Path $currentRoot $relativePath
    if (-not (Test-Path -LiteralPath $resource)) {
        throw "Aktive Docker-Ressource fehlt nach der Bereinigung: $resource"
    }
}

Write-Host "Der aktive Docker-Modellbestand bleibt erhalten: $currentRoot" -ForegroundColor Cyan
