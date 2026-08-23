#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $DestinationRoot = (Join-Path `
        ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) `
        '.lmstudio\models\lmstudio-community\Qwen3.8-27B-GGUF'),

    [ValidateRange(1, 30)]
    [int] $MaximumAttempts = 12,

    [switch] $IncludeVisionProjector,

    [switch] $SkipCatalogRefresh,

    [switch] $Force
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$repository = 'lmstudio-community/Qwen3.8-27B-GGUF'
$revision = '5a7da681f60570ab5b439a587e912d2e5eddb582'
$files = @(
    [pscustomobject]@{
        Name = 'Qwen3.8-27B-Q8_0.gguf'
        Length = [long]29047084256
        Sha256 = 'f0fc43cfd802bd87bc7b95f2120553a6407848e3229126b623cc397427857e8a'
        Required = $true
    },
    [pscustomobject]@{
        Name = 'mmproj-Qwen3.8-27B-BF16.gguf'
        Length = [long]931145856
        Sha256 = '97ba9d70e7407f08c880def231fd360a312c76d0053387733f10dcf6affd75a1'
        Required = $false
    }
)

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

function Get-DownloadUri {
    param(
        [Parameter(Mandatory = $true)]
        [string] $FileName
    )

    $encodedName = [Uri]::EscapeDataString($FileName)
    return 'https://huggingface.co/{0}/resolve/{1}/{2}?download=true' -f $repository, $revision, $encodedName
}

function Receive-PinnedFile {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject] $File,

        [Parameter(Mandatory = $true)]
        [string] $TargetDirectory,

        [Parameter(Mandatory = $true)]
        [string] $CurlPath
    )

    $destination = Join-Path $TargetDirectory $File.Name
    $partial = "$destination.part"

    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        $existingLength = (Get-Item -LiteralPath $destination).Length
        if ($existingLength -eq $File.Length) {
            try {
                Assert-FileHash -Path $destination -ExpectedSha256 $File.Sha256
                Write-Host "Bereits vollstaendig: $destination" -ForegroundColor Green
                return
            }
            catch {
                if (-not $Force) {
                    throw
                }
                Write-Warning "Vorhandene Datei ist ungueltig und wird wegen -Force ersetzt: $destination"
                Remove-Item -LiteralPath $destination -Force
            }
        }
        elseif ($Force) {
            Write-Warning "Vorhandene Datei hat falsche Groesse und wird wegen -Force ersetzt: $destination"
            Remove-Item -LiteralPath $destination -Force
        }
        else {
            throw "Vorhandene Datei hat eine unerwartete Groesse: $destination ($existingLength statt $($File.Length) Bytes). Verwende -Force zum Ersetzen."
        }
    }

    if (Test-Path -LiteralPath $partial -PathType Leaf) {
        $partialLength = (Get-Item -LiteralPath $partial).Length
        if ($partialLength -gt $File.Length) {
            if ($Force) {
                Write-Warning "Partielle Datei ist groesser als erwartet und wird wegen -Force ersetzt: $partial"
                Remove-Item -LiteralPath $partial -Force
            }
            else {
                throw "Partielle Datei ist groesser als die gepinnte Datei: $partial. Verwende -Force zum Ersetzen."
            }
        }
    }

    if (-not (Test-Path -LiteralPath $partial -PathType Leaf)) {
        New-Item -ItemType File -Path $partial | Out-Null
    }

    $uri = Get-DownloadUri -FileName $File.Name
    for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
        $partialLength = (Get-Item -LiteralPath $partial).Length
        $completedPercent = if ($File.Length -eq 0) {
            0
        }
        else {
            [Math]::Round(100 * $partialLength / $File.Length, 1)
        }

        Write-Host "Lade $($File.Name) ($completedPercent %, Versuch $attempt/$MaximumAttempts) ..." -ForegroundColor Cyan
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
        if ($exitCode -eq 0 -and $downloadedLength -eq $File.Length) {
            break
        }
        if ($downloadedLength -gt $File.Length) {
            throw "Download ueberschreitet die gepinnte Dateigroesse: $partial"
        }
        if ($attempt -eq $MaximumAttempts) {
            throw "Download nach $MaximumAttempts Versuchen unvollstaendig: $($File.Name) ($downloadedLength von $($File.Length) Bytes)"
        }
        Start-Sleep -Seconds ([Math]::Min(60, $attempt * 5))
    }

    Assert-FileHash -Path $partial -ExpectedSha256 $File.Sha256
    Move-Item -LiteralPath $partial -Destination $destination
    Write-Host "Heruntergeladen: $destination" -ForegroundColor Green
}

$curl = Get-Command 'curl.exe' -ErrorAction Stop
$destinationRootFull = [IO.Path]::GetFullPath($DestinationRoot)
New-Item -ItemType Directory -Path $destinationRootFull -Force | Out-Null

$selectedFiles = @($files | Where-Object { $_.Required -or $IncludeVisionProjector })
$missingBytes = [long]0
foreach ($file in $selectedFiles) {
    $destination = Join-Path $destinationRootFull $file.Name
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
    $remainingBytes = [long]$file.Length - [long]$partialLength
    if ($remainingBytes -gt 0) {
        $missingBytes = [long]($missingBytes + $remainingBytes)
    }
}

$drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($destinationRootFull))
$safetyReserve = [long](5GB)
if ($drive.AvailableFreeSpace -lt ($missingBytes + $safetyReserve)) {
    $requiredGiB = [Math]::Ceiling(($missingBytes + $safetyReserve) / 1GB)
    $availableGiB = [Math]::Round($drive.AvailableFreeSpace / 1GB, 1)
    throw "Zu wenig freier Speicher. Benoetigt werden ungefaehr $requiredGiB GiB inklusive Reserve; verfuegbar sind $availableGiB GiB."
}

Write-Host 'Qwen3.8-27B Q8_0 fuer LM Studio' -ForegroundColor Magenta
Write-Host "Repository: $repository@$revision" -ForegroundColor DarkGray
Write-Host "Ziel: $destinationRootFull" -ForegroundColor DarkGray
Write-Host 'Das Skript laedt das Modell nur herunter und startet es nicht.' -ForegroundColor Yellow
if ($IncludeVisionProjector) {
    Write-Host 'Vision-Projektor wird optional mitgeladen.' -ForegroundColor Yellow
}

foreach ($file in $selectedFiles) {
    Receive-PinnedFile -File $file -TargetDirectory $destinationRootFull -CurlPath $curl.Source
}

if (-not $SkipCatalogRefresh) {
    $refreshScript = Join-Path $PSScriptRoot 'refresh-lmstudio-model-catalog.ps1'
    if (Test-Path -LiteralPath $refreshScript -PathType Leaf) {
        & $refreshScript -RequiredNameFragments @('Qwen3.8-27B-Q8_0') | Out-Host
    }
    else {
        Write-Warning 'LM-Studio-Katalog konnte nicht aktualisiert werden, weil refresh-lmstudio-model-catalog.ps1 nicht gefunden wurde.'
    }
}

Write-Host 'Qwen3.8-27B-Q8_0.gguf wurde vollstaendig geladen und per SHA-256 geprueft.' -ForegroundColor Green
Write-Host 'Es wurden keine GO-AI-Server-Einstellungen geaendert und kein Modell geladen.' -ForegroundColor Green
