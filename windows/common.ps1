#requires -Version 5.1
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:GoRepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Get-GoRepositoryRoot {
    return $script:GoRepositoryRoot
}

function Resolve-GoRepositoryPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RelativePath
    )

    return [System.IO.Path]::GetFullPath((Join-Path $script:GoRepositoryRoot $RelativePath))
}

function Assert-GoCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "Required command '$Name' was not found in PATH."
    }

    return $command
}

function Resolve-GoDockerCommand {
    $dockerBin = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) 'Docker\Docker\resources\bin'
    if (Test-Path -LiteralPath $dockerBin -PathType Container) {
        $pathEntries = @($env:PATH -split ';')
        if (-not ($pathEntries | Where-Object { [string]::Equals($_.TrimEnd('\'), $dockerBin.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) })) {
            $env:PATH = $dockerBin + ';' + $env:PATH
        }
    }

    $command = Get-Command docker -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $candidate = Join-Path $dockerBin 'docker.exe'
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        return $candidate
    }

    throw 'Docker Desktop command was not found. Install or start Docker Desktop.'
}

function Get-GoAiStackDefaults {
    param(
        [string] $DataRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'GO-AI-Stack'),
        [string] $ModelRoot,
        [string] $NativeModelRoot
    )

    $resolvedDataRoot = [IO.Path]::GetFullPath($DataRoot)
    $resolvedModelRoot = if ([string]::IsNullOrWhiteSpace($ModelRoot)) {
        Join-Path $resolvedDataRoot 'Models'
    }
    else {
        [IO.Path]::GetFullPath($ModelRoot)
    }
    $resolvedNativeModelRoot = if ([string]::IsNullOrWhiteSpace($NativeModelRoot)) {
        Join-Path $env:USERPROFILE '.cache\huggingface\hub'
    }
    else {
        [IO.Path]::GetFullPath($NativeModelRoot)
    }
    return [pscustomobject]@{
        DataRoot = $resolvedDataRoot
        ModelRoot = $resolvedModelRoot
        NativeModelRoot = [IO.Path]::GetFullPath($resolvedNativeModelRoot)
        ComposeFile = Resolve-GoRepositoryPath -RelativePath 'deploy\go-ai\compose.yaml'
        EnvironmentFile = Join-Path $resolvedDataRoot 'stack.env'
    }
}

function Write-GoAiStackEnvironment {
    param(
        [Parameter(Mandatory = $true)] $Paths,
        [string] $ServerIp = '192.168.0.67',
        [string] $ImageVersion = '1.0.0',
        [Alias('CodingModelRoot')][string] $NativeModelRoot
    )
    if ([string]::IsNullOrWhiteSpace($NativeModelRoot)) { $NativeModelRoot = $Paths.NativeModelRoot }
    New-Item -ItemType Directory -Path $Paths.DataRoot -Force | Out-Null
    $content = @(
        "GO_AI_DATA_ROOT=$($Paths.DataRoot -replace '\\','/')"
        "GO_AI_MODEL_ROOT=$($Paths.ModelRoot -replace '\\','/')"
        "GO_AI_NATIVE_MODEL_ROOT=$($NativeModelRoot -replace '\\','/')"
        "GO_AI_CODING_MODEL_ROOT=$($NativeModelRoot -replace '\\','/')"
        'GO_AI_MODEL_RUNTIME_URL=http://host.docker.internal:8081'
        "GO_AI_EXPECTED_LAN_IP=$ServerIp"
        "GO_AI_PUBLIC_URL=http://${ServerIp}:8080"
        "GO_AI_IMAGE_VERSION=$ImageVersion"
    ) -join "`n"
    [IO.File]::WriteAllText($Paths.EnvironmentFile, $content + "`n", [Text.UTF8Encoding]::new($false))
}

function Resolve-GoNativeModelFile {
    param([Parameter(Mandatory = $true)][string] $NativeModelRoot,
        [Parameter(Mandatory = $true)][string] $Repository,
        [Parameter(Mandatory = $true)][string] $Revision,
        [Parameter(Mandatory = $true)][string] $FileName)
    if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or $Revision -notmatch '^[a-fA-F0-9]{40}$') {
        throw 'A native model cache entry requires a pinned Hugging Face repository and revision.'
    }
    $root = [IO.Path]::GetFullPath($NativeModelRoot).TrimEnd('\', '/')
    $snapshot = Join-Path $root ('models--' + $Repository.Replace('/', '--') + '\snapshots\' + $Revision)
    $destination = [IO.Path]::GetFullPath((Join-Path $snapshot $FileName))
    if (-not $destination.StartsWith($snapshot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The model filename escapes its pinned native snapshot directory.'
    }
    return $destination
}

function Get-GoNativeModelCatalog {
    param([Parameter(Mandatory = $true)][string] $NativeModelRoot)
    $python = Join-Path $env:USERPROFILE '.unsloth\studio\unsloth_studio\Scripts\python.exe'
    if (-not (Test-Path -LiteralPath $python -PathType Leaf)) { $python = (Assert-GoCommand -Name 'python.exe').Source }
    $json = & $python (Resolve-GoRepositoryPath -RelativePath 'workers\coding\catalog.py') --model-root $NativeModelRoot --list-models
    if ($LASTEXITCODE -ne 0) { throw 'The native model catalog could not be read.' }
    return @($json | ConvertFrom-Json)
}

function Invoke-GoAiCompose {
    param(
        [Parameter(Mandatory = $true)] $Paths,
        [Parameter(Mandatory = $true)] [string[]] $Arguments
    )

    $docker = Resolve-GoDockerCommand
    & $docker compose --env-file $Paths.EnvironmentFile -f $Paths.ComposeFile @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose failed with exit code $LASTEXITCODE."
    }
}

function Invoke-GoDotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $CommandArguments
    )

    Assert-GoCommand -Name 'dotnet' | Out-Null
    Write-Host ("dotnet " + ($CommandArguments -join ' ')) -ForegroundColor DarkGray
    & dotnet @CommandArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code $LASTEXITCODE."
    }
}

function Get-GoBuildId {
    $git = Get-Command git -ErrorAction SilentlyContinue
    if ($null -ne $git) {
        $commit = $null
        try {
            $commit = (& git -C $script:GoRepositoryRoot rev-parse --short=12 HEAD 2>$null)
        }
        catch {
            $commit = $null
        }
        if ($null -ne $commit -and -not [string]::IsNullOrWhiteSpace($commit)) {
            return ([string]$commit).Trim()
        }
    }

    return [DateTime]::UtcNow.ToString('yyyyMMddHHmmss', [Globalization.CultureInfo]::InvariantCulture)
}

function Get-GoBuiltAt {
    return [DateTime]::UtcNow.ToString('o', [Globalization.CultureInfo]::InvariantCulture)
}

function Assert-GoArtifactPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $artifactRoot = Resolve-GoRepositoryPath -RelativePath 'artifacts'
    $resolved = [System.IO.Path]::GetFullPath($Path)
    $prefix = $artifactRoot.TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Artifact path must stay below '$artifactRoot': $resolved"
    }

    return $resolved
}

function Reset-GoArtifactDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $resolved = Assert-GoArtifactPath -Path $Path
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }

    New-Item -ItemType Directory -Path $resolved -Force | Out-Null
    return $resolved
}
