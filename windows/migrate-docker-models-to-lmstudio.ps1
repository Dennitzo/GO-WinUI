#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $DockerModelRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'GO-AI-Stack\Models'),
    [string] $LmStudioModelRoot = (Join-Path $env:USERPROFILE '.lmstudio\models'),
    [switch] $NoElevation
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

$sourceRoot = [IO.Path]::GetFullPath($DockerModelRoot).TrimEnd('\')
$destinationRoot = [IO.Path]::GetFullPath($LmStudioModelRoot).TrimEnd('\')
if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
    throw "Docker model root does not exist: $sourceRoot"
}
if ([string]::Equals($sourceRoot, $destinationRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Source and destination model roots must be different.'
}

if (-not $NoElevation -and -not (Test-IsAdministrator)) {
    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', ('"' + $PSCommandPath + '"'),
        '-DockerModelRoot', ('"' + $sourceRoot + '"'),
        '-LmStudioModelRoot', ('"' + $destinationRoot + '"'),
        '-NoElevation'
    )
    $process = Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList $arguments
    if ($process.ExitCode -ne 0) {
        throw "Elevated model migration failed with exit code $($process.ExitCode)."
    }
    return
}

$moves = @(
    [pscustomobject]@{
        Source = 'llm\gpt-oss-120b-MXFP4-00001-of-00002.gguf'
        Destination = 'lmstudio-community\gpt-oss-120b-GGUF\gpt-oss-120b-MXFP4-00001-of-00002.gguf'
        Length = 39815566336L
    },
    [pscustomobject]@{
        Source = 'llm\gpt-oss-120b-MXFP4-00002-of-00002.gguf'
        Destination = 'lmstudio-community\gpt-oss-120b-GGUF\gpt-oss-120b-MXFP4-00002-of-00002.gguf'
        Length = 23571779104L
    },
    [pscustomobject]@{
        Source = 'llm\qwen3-coder-next-q8_0\Qwen3-Coder-Next-Q8_0-00001-of-00004.gguf'
        Destination = 'Qwen\Qwen3-Coder-Next-GGUF\Qwen3-Coder-Next-Q8_0-00001-of-00004.gguf'
        Length = 26190321568L
    },
    [pscustomobject]@{
        Source = 'llm\qwen3-coder-next-q8_0\Qwen3-Coder-Next-Q8_0-00002-of-00004.gguf'
        Destination = 'Qwen\Qwen3-Coder-Next-GGUF\Qwen3-Coder-Next-Q8_0-00002-of-00004.gguf'
        Length = 26212935584L
    },
    [pscustomobject]@{
        Source = 'llm\qwen3-coder-next-q8_0\Qwen3-Coder-Next-Q8_0-00003-of-00004.gguf'
        Destination = 'Qwen\Qwen3-Coder-Next-GGUF\Qwen3-Coder-Next-Q8_0-00003-of-00004.gguf'
        Length = 26047157792L
    },
    [pscustomobject]@{
        Source = 'llm\qwen3-coder-next-q8_0\Qwen3-Coder-Next-Q8_0-00004-of-00004.gguf'
        Destination = 'Qwen\Qwen3-Coder-Next-GGUF\Qwen3-Coder-Next-Q8_0-00004-of-00004.gguf'
        Length = 6361641024L
    },
    [pscustomobject]@{
        Source = 'vision\Qwen3VL-30B-A3B-Instruct-Q4_K_M.gguf'
        Destination = 'Qwen\Qwen3-VL-30B-A3B-Instruct-GGUF\Qwen3VL-30B-A3B-Instruct-Q4_K_M.gguf'
        Length = 18556687168L
    },
    [pscustomobject]@{
        Source = 'vision\mmproj-Qwen3VL-30B-A3B-Instruct-F16.gguf'
        Destination = 'Qwen\Qwen3-VL-30B-A3B-Instruct-GGUF\mmproj-Qwen3VL-30B-A3B-Instruct-F16.gguf'
        Length = 1083499584L
    },
    [pscustomobject]@{
        Source = 'embedding\bge-m3-q8_0.gguf'
        Destination = 'ggml-org\bge-m3-Q8_0-GGUF\bge-m3-q8_0.gguf'
        Length = 634553760L
    }
)

New-Item -ItemType Directory -Path $destinationRoot -Force | Out-Null
$sourcePrefix = $sourceRoot + '\'
$destinationPrefix = $destinationRoot + '\'
foreach ($move in $moves) {
    $source = [IO.Path]::GetFullPath((Join-Path $sourceRoot $move.Source))
    $destination = [IO.Path]::GetFullPath((Join-Path $destinationRoot $move.Destination))
    if (-not $source.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not $destination.StartsWith($destinationPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Resolved migration path escaped an intended root: $($move.Source)"
    }

    $sourceExists = Test-Path -LiteralPath $source -PathType Leaf
    $destinationExists = Test-Path -LiteralPath $destination -PathType Leaf
    if ($destinationExists) {
        $destinationLength = (Get-Item -LiteralPath $destination).Length
        if ($destinationLength -ne [long]$move.Length) {
            throw "Existing LM Studio model has an unexpected length: $destination"
        }
        if ($sourceExists) {
            throw "Both source and destination exist; refusing to delete or overwrite either file: $source"
        }
        Write-Host "Already migrated: $destination" -ForegroundColor DarkGray
        continue
    }
    if (-not $sourceExists) {
        throw "Pinned model is missing from both model roots: $($move.Source)"
    }
    if ((Get-Item -LiteralPath $source).Length -ne [long]$move.Length) {
        throw "Source model has an unexpected length: $source"
    }

    $destinationDirectory = Split-Path -Parent $destination
    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    Move-Item -LiteralPath $source -Destination $destination
    if (-not (Test-Path -LiteralPath $destination -PathType Leaf) -or
        (Get-Item -LiteralPath $destination).Length -ne [long]$move.Length) {
        throw "Post-move validation failed: $destination"
    }
    Write-Host "Migrated: $destination" -ForegroundColor Green
}

$candidateDirectories = @(
    (Join-Path $sourceRoot 'llm\qwen3-coder-next-q8_0\.download'),
    (Join-Path $sourceRoot 'llm\qwen3-coder-next-q8_0'),
    (Join-Path $sourceRoot 'llm'),
    (Join-Path $sourceRoot 'vision'),
    (Join-Path $sourceRoot 'embedding')
)
foreach ($directory in $candidateDirectories) {
    $resolved = [IO.Path]::GetFullPath($directory)
    if ($resolved.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolved -PathType Container) -and
        -not (Get-ChildItem -LiteralPath $resolved -Force | Select-Object -First 1)) {
        Remove-Item -LiteralPath $resolved -Force
    }
}

Write-Host 'LM Studio model migration completed. Worker-only speech and image resources remain in the Docker model root.' -ForegroundColor Green
