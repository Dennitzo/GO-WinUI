[CmdletBinding()]
param(
    [ValidateSet('UD-IQ2_M', 'UD-IQ2_XXS')]
    [string] $Quantization = 'UD-IQ2_M',

    [string] $DestinationRoot = (Join-Path `
        ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) `
        '.lmstudio\models\unsloth\DeepSeek-V4-Flash-0731-GGUF'),

    [ValidateRange(1, 30)]
    [int] $MaximumAttempts = 12
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$repository = 'unsloth/DeepSeek-V4-Flash-0731-GGUF'
$revision = 'fbbb5b93fb787c21338159b0af3318bb3f4d9768'
$variants = @{
    'UD-IQ2_M' = @(
        [pscustomobject]@{
            Name = 'DeepSeek-V4-Flash-0731-UD-IQ2_M-00001-of-00003.gguf'
            Length = [long]5257664
            Sha256 = '057a3aacf912e079f22d07b94bc3b4ef46c6632476bc0bd1761347eb08edb2aa'
        },
        [pscustomobject]@{
            Name = 'DeepSeek-V4-Flash-0731-UD-IQ2_M-00002-of-00003.gguf'
            Length = [long]49956780160
            Sha256 = '700405274473b58fa26be4f14e4a194c2e7554fa3a052f62a0c50c568e89fc1f'
        },
        [pscustomobject]@{
            Name = 'DeepSeek-V4-Flash-0731-UD-IQ2_M-00003-of-00003.gguf'
            Length = [long]40964890464
            Sha256 = 'a69102ddfaf4a84426e11fdb66716654f4260dc3a1de3ade9fd50e006b8691d3'
        }
    )
    'UD-IQ2_XXS' = @(
        [pscustomobject]@{
            Name = 'DeepSeek-V4-Flash-0731-UD-IQ2_XXS-00001-of-00003.gguf'
            Length = [long]5257664
            Sha256 = 'c58c9d62eac7b62e9578b52613f425e48313d7212ab8d1d76caed8ea8de26595'
        },
        [pscustomobject]@{
            Name = 'DeepSeek-V4-Flash-0731-UD-IQ2_XXS-00002-of-00003.gguf'
            Length = [long]49890588800
            Sha256 = '65a113df6d4469f16db6882b6919e153c464c3c78c833f5e1b41a33803cdbd52'
        },
        [pscustomobject]@{
            Name = 'DeepSeek-V4-Flash-0731-UD-IQ2_XXS-00003-of-00003.gguf'
            Length = [long]40964890464
            Sha256 = 'a69102ddfaf4a84426e11fdb66716654f4260dc3a1de3ade9fd50e006b8691d3'
        }
    )
}

function Assert-FileHash {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedSha256
    )

    Write-Host "Pruefe SHA-256: $Path" -ForegroundColor DarkGray
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if (-not $actual.Equals($ExpectedSha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "SHA-256 stimmt nicht ueberein: $Path (erwartet $ExpectedSha256, erhalten $actual)"
    }
}

function Receive-PinnedShard {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject] $Shard,

        [Parameter(Mandatory = $true)]
        [string] $VariantDirectory,

        [Parameter(Mandatory = $true)]
        [string] $CurlPath
    )

    $destination = Join-Path $VariantDirectory $Shard.Name
    $partial = "$destination.part"
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        $existingLength = (Get-Item -LiteralPath $destination).Length
        if ($existingLength -ne $Shard.Length) {
            throw "Vorhandener Shard hat eine unerwartete Groesse: $destination ($existingLength statt $($Shard.Length) Bytes)"
        }
        Assert-FileHash -Path $destination -ExpectedSha256 $Shard.Sha256
        Write-Host "Bereits vollstaendig: $destination" -ForegroundColor Green
        return
    }

    if (-not (Test-Path -LiteralPath $partial -PathType Leaf)) {
        New-Item -ItemType File -Path $partial | Out-Null
    }
    $partialLength = (Get-Item -LiteralPath $partial).Length
    if ($partialLength -gt $Shard.Length) {
        throw "Partielle Datei ist groesser als der gepinnte Shard: $partial"
    }

    $encodedVariant = [Uri]::EscapeDataString($Quantization)
    $encodedName = [Uri]::EscapeDataString($Shard.Name)
    $uri = "https://huggingface.co/$repository/resolve/$revision/$encodedVariant/${encodedName}?download=true"
    for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
        $completedPercent = if ($Shard.Length -eq 0) {
            0
        }
        else {
            [Math]::Round(100 * (Get-Item -LiteralPath $partial).Length / $Shard.Length, 1)
        }
        Write-Host "Lade $($Shard.Name) ($completedPercent %, Versuch $attempt/$MaximumAttempts) ..." -ForegroundColor Cyan
        & $CurlPath @(
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
            '--output', $partial,
            $uri
        )
        $exitCode = $LASTEXITCODE
        $downloadedLength = (Get-Item -LiteralPath $partial).Length
        if ($exitCode -eq 0 -and $downloadedLength -eq $Shard.Length) {
            break
        }
        if ($downloadedLength -gt $Shard.Length) {
            throw "Download ueberschreitet die gepinnte Dateigroesse: $partial"
        }
        if ($attempt -eq $MaximumAttempts) {
            throw "Download nach $MaximumAttempts Versuchen unvollstaendig: $($Shard.Name) ($downloadedLength von $($Shard.Length) Bytes)"
        }
        Start-Sleep -Seconds ([Math]::Min(60, $attempt * 5))
    }

    Assert-FileHash -Path $partial -ExpectedSha256 $Shard.Sha256
    Move-Item -LiteralPath $partial -Destination $destination
    Write-Host "Heruntergeladen: $destination" -ForegroundColor Green
}

$curl = Get-Command 'curl.exe' -ErrorAction Stop
$destinationRootFull = [IO.Path]::GetFullPath($DestinationRoot)
$variantDirectory = Join-Path $destinationRootFull $Quantization
New-Item -ItemType Directory -Path $variantDirectory -Force | Out-Null

$shards = $variants[$Quantization]
$missingBytes = [long]0
foreach ($shard in $shards) {
    $destination = Join-Path $variantDirectory $shard.Name
    $partial = "$destination.part"
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        continue
    }
    $partialLength = if (Test-Path -LiteralPath $partial -PathType Leaf) {
        (Get-Item -LiteralPath $partial).Length
    }
    else {
        0
    }
    # Avoid Math.Max overload resolution here: PowerShell may select the Int32
    # overload before converting the ~50 GB shard sizes, which then overflows.
    $remainingBytes = [long]$shard.Length - [long]$partialLength
    if ($remainingBytes -gt 0) {
        $missingBytes = [long]($missingBytes + $remainingBytes)
    }
}

$drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($variantDirectory))
$safetyReserve = [long](5GB)
if ($drive.AvailableFreeSpace -lt ($missingBytes + $safetyReserve)) {
    $requiredGiB = [Math]::Ceiling(($missingBytes + $safetyReserve) / 1GB)
    $availableGiB = [Math]::Round($drive.AvailableFreeSpace / 1GB, 1)
    throw "Zu wenig freier Speicher. Benoetigt werden ungefaehr $requiredGiB GiB inklusive Reserve; verfuegbar sind $availableGiB GiB."
}

Write-Host "DeepSeek-V4-Flash-0731 $Quantization" -ForegroundColor Magenta
Write-Host "Repository: $repository@$revision" -ForegroundColor DarkGray
Write-Host "Ziel: $variantDirectory" -ForegroundColor DarkGray
Write-Host "Das Skript konfiguriert oder laedt das Modell nicht in LM Studio." -ForegroundColor Yellow

foreach ($shard in $shards) {
    Receive-PinnedShard -Shard $shard -VariantDirectory $variantDirectory -CurlPath $curl.Source
}

Write-Host "Alle drei $Quantization-Shards wurden vollstaendig und per SHA-256 geprueft." -ForegroundColor Green
Write-Host "Es wurden keine LM-Studio- oder GO-AI-Server-Einstellungen geaendert." -ForegroundColor Green
