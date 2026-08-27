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

function Resolve-GoLmStudioCommand {
    $command = Get-Command lms -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $candidate = Join-Path $env:USERPROFILE '.lmstudio\bin\lms.exe'
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        return $candidate
    }

    throw 'LM Studio CLI was not found. Install LM Studio and initialize its CLI once.'
}

function Get-GoAiStackDefaults {
    param(
        [string] $DataRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'GO-AI-Stack'),
        [string] $ModelRoot,
        [string] $LmStudioModelRoot
    )

    $resolvedDataRoot = [IO.Path]::GetFullPath($DataRoot)
    $resolvedModelRoot = if ([string]::IsNullOrWhiteSpace($ModelRoot)) {
        Join-Path $resolvedDataRoot 'Models'
    }
    else {
        [IO.Path]::GetFullPath($ModelRoot)
    }
    $resolvedLmStudioModelRoot = if ([string]::IsNullOrWhiteSpace($LmStudioModelRoot)) {
        Join-Path $env:USERPROFILE '.lmstudio\models'
    }
    else {
        [IO.Path]::GetFullPath($LmStudioModelRoot)
    }
    return [pscustomobject]@{
        DataRoot = $resolvedDataRoot
        ModelRoot = $resolvedModelRoot
        LmStudioModelRoot = [IO.Path]::GetFullPath($resolvedLmStudioModelRoot)
        ComposeFile = Resolve-GoRepositoryPath -RelativePath 'deploy\go-ai\compose.yaml'
        EnvironmentFile = Join-Path $resolvedDataRoot 'stack.env'
    }
}

function Write-GoAiStackEnvironment {
    param(
        [Parameter(Mandatory = $true)] $Paths,
        [string] $ServerIp = '192.168.0.67',
        [string] $ImageVersion = '1.0.0'
    )

    New-Item -ItemType Directory -Path $Paths.DataRoot -Force | Out-Null
    $content = @(
        "GO_AI_DATA_ROOT=$($Paths.DataRoot -replace '\\','/')"
        "GO_AI_MODEL_ROOT=$($Paths.ModelRoot -replace '\\','/')"
        "GO_AI_EXPECTED_LAN_IP=$ServerIp"
        "GO_AI_PUBLIC_URL=http://${ServerIp}:8080"
        "GO_AI_IMAGE_VERSION=$ImageVersion"
    ) -join "`n"
    [IO.File]::WriteAllText($Paths.EnvironmentFile, $content + "`n", [Text.UTF8Encoding]::new($false))
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

function Resolve-GoBricsCadInstallDirectory {
    param(
        [string] $RequestedPath
    )

    $candidates = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $candidates.Add($RequestedPath)
    }
    if (-not [string]::IsNullOrWhiteSpace($env:BRICSCAD_V26_DIR)) {
        $candidates.Add($env:BRICSCAD_V26_DIR)
    }

    $programFilesRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    $bricsysRoot = Join-Path $programFilesRoot 'Bricsys'
    if (Test-Path -LiteralPath $bricsysRoot) {
        Get-ChildItem -LiteralPath $bricsysRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like 'BricsCAD V26*' } |
            ForEach-Object { $candidates.Add($_.FullName) }
    }

    foreach ($candidate in $candidates) {
        $resolved = [System.IO.Path]::GetFullPath($candidate)
        $required = @('bricscad.exe', 'BrxMgd.dll', 'TD_Mgd.dll', 'TD_MgdBrep.dll', 'TD_MgdDbConstraints.dll')
        $valid = $true
        foreach ($file in $required) {
            if (-not (Test-Path -LiteralPath (Join-Path $resolved $file) -PathType Leaf)) {
                $valid = $false
                break
            }
        }
        if ($valid) {
            return $resolved
        }
    }

    throw 'BricsCAD V26 managed SDK was not found. Pass -BricsCadInstallDir or set BRICSCAD_V26_DIR.'
}

function Get-GoContractHash {
    $contract = Resolve-GoRepositoryPath -RelativePath 'contracts\bricscad-dotnet-tools-v2.json'
    if (-not (Test-Path -LiteralPath $contract -PathType Leaf)) {
        throw "BricsCAD contract is missing: $contract"
    }

    return (Get-FileHash -LiteralPath $contract -Algorithm SHA256).Hash.ToLowerInvariant()
}
