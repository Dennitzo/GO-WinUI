#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $DataRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'GO-AI-Server'),

    [string] $ServerUrl = 'https://192.168.0.67:8443',

    [string] $ApiKeyPath,

    [string] $RootCertificatePath,

    [string] $SmokeClientPath,

    [string] $OutputDirectory,

    [string] $ImageFixture,

    [string] $AudioFixture,

    [string] $VideoFixture

)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

function Assert-HttpStatus {
    param(
        [Parameter(Mandatory = $true)] [string] $Uri,
        [Parameter(Mandatory = $true)] [int] $ExpectedStatus,
        [hashtable] $Headers = @{}
    )

    try {
        $response = Invoke-WebRequest -UseBasicParsing -Method Get -Uri $Uri -Headers $Headers -TimeoutSec 15
        $status = [int]$response.StatusCode
    }
    catch {
        if ($null -eq $_.Exception.Response) {
            throw
        }
        $status = [int]$_.Exception.Response.StatusCode
    }
    if ($status -ne $ExpectedStatus) {
        throw "Expected HTTP $ExpectedStatus from $Uri, received HTTP $status."
    }
}

function New-SmokeImage {
    param([Parameter(Mandatory = $true)] [string] $Path)

    Add-Type -AssemblyName System.Drawing
    $bitmap = New-Object System.Drawing.Bitmap 640, 360
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::FromArgb(24, 22, 29))
        $green = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(143, 189, 69))
        $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
        $gray = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(120, 120, 128)), 5
        $font = New-Object System.Drawing.Font 'Segoe UI', 30, ([System.Drawing.FontStyle]::Bold)
        try {
            $graphics.FillRectangle($green, 40, 45, 165, 95)
            $graphics.DrawRectangle($gray, 245, 50, 340, 210)
            $graphics.DrawLine($gray, 245, 150, 585, 150)
            $graphics.DrawString('TGA', $font, $white, 86, 70)
            $graphics.DrawString('GO AI SMOKE', $font, $white, 245, 280)
            $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $font.Dispose()
            $gray.Dispose()
            $white.Dispose()
            $green.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function New-SmokeAudio {
    param([Parameter(Mandatory = $true)] [string] $Path)

    Add-Type -AssemblyName System.Speech
    $synthesizer = New-Object System.Speech.Synthesis.SpeechSynthesizer
    try {
        $germanVoice = $synthesizer.GetInstalledVoices() |
            Where-Object { $_.VoiceInfo.Culture.Name -eq 'de-DE' } |
            Select-Object -First 1
        if ($null -eq $germanVoice) {
            throw 'No German Windows voice is installed for the live STT fixture.'
        }
        $synthesizer.SelectVoice($germanVoice.VoiceInfo.Name)
        $format = [System.Speech.AudioFormat.SpeechAudioFormatInfo]::new(
            16000,
            [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen,
            [System.Speech.AudioFormat.AudioChannel]::Mono)
        $synthesizer.SetOutputToWaveFile($Path, $format)
        $synthesizer.Speak('Der GO AI Server prüft Heizung Lüftung Sanitär und Elektro für die technische Gebäudeausrüstung.')
    }
    finally {
        $synthesizer.Dispose()
    }
}

function New-SmokeVideo {
    param(
        [Parameter(Mandatory = $true)] [string] $Directory,
        [Parameter(Mandatory = $true)] [string] $ImagePath,
        [Parameter(Mandatory = $true)] [string] $AudioPath,
        [Parameter(Mandatory = $true)] [string] $OutputPath
    )

    $docker = Resolve-GoDockerCommand
    $mount = $Directory.Replace('\', '/')
    & $docker run --rm `
        --entrypoint ffmpeg `
        --volume ("{0}:/fixtures" -f $mount) `
        'go-ai/media:1.0.0' `
        -nostdin -v error -y `
        -loop 1 -i ('/fixtures/' + [IO.Path]::GetFileName($ImagePath)) `
        -i ('/fixtures/' + [IO.Path]::GetFileName($AudioPath)) `
        -c:v libx264 -preset ultrafast -pix_fmt yuv420p `
        -c:a aac -shortest -t 12 `
        ('/fixtures/' + [IO.Path]::GetFileName($OutputPath))
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $OutputPath -PathType Leaf)) {
        throw 'Unable to create the deterministic video smoke fixture with the media worker image.'
    }
}

$DataRoot = [IO.Path]::GetFullPath($DataRoot)
if ([string]::IsNullOrWhiteSpace($ApiKeyPath)) {
    $ApiKeyPath = Join-Path $DataRoot 'Secrets\bootstrap-client-key.once'
}
if ([string]::IsNullOrWhiteSpace($RootCertificatePath)) {
    $RootCertificatePath = Join-Path $DataRoot 'Caddy\data\caddy\pki\authorities\local\root.crt'
}
if ([string]::IsNullOrWhiteSpace($SmokeClientPath)) {
    $packageRoot = Get-GoRepositoryRoot
    $bundled = Join-Path $packageRoot 'smoke-client\win-x64\GoAi.SmokeClient.exe'
    $published = Join-Path $packageRoot 'artifacts\go-ai-server\smoke-client\win-x64\GoAi.SmokeClient.exe'
    $SmokeClientPath = if (Test-Path -LiteralPath $bundled -PathType Leaf) {
        $bundled
    }
    elseif (Test-Path -LiteralPath $published -PathType Leaf) {
        $published
    }
    else {
        Resolve-GoRepositoryPath -RelativePath 'src\GoAi.SmokeClient\bin\Release\net10.0\GoAi.SmokeClient.exe'
    }
}
foreach ($requiredPath in @($ApiKeyPath, $RootCertificatePath, $SmokeClientPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Live smoke prerequisite is missing: $requiredPath"
    }
}
$apiKey = (Get-Content -LiteralPath $ApiKeyPath -Raw -Encoding ascii).Trim()
if ([string]::IsNullOrWhiteSpace($apiKey)) {
    throw 'The live smoke API key file is empty.'
}

$fixtureBase = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'GO-AI-Server-Live-Smoke'))
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $fixtureBase ([Guid]::NewGuid().ToString('N'))))
if (-not [string]::Equals([IO.Path]::GetDirectoryName($fixtureRoot), $fixtureBase, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe fixture directory: $fixtureRoot"
}
New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Resolve-GoRepositoryPath -RelativePath 'artifacts\go-ai-server\live-smoke'
}
$OutputDirectory = Reset-GoArtifactDirectory -Path $OutputDirectory

$previousApiKey = $env:GO_AI_API_KEY
$previousServerUrl = $env:GO_AI_SERVER_URL
$previousRootCertificate = $env:GO_AI_ROOT_CERTIFICATE
$previousCertificateRevocationCheck = [Net.ServicePointManager]::CheckCertificateRevocationList
try {
    # Caddy's private CA has no public CRL endpoint. Chain trust, validity, IP-SAN
    # and the pinned CA are still validated; only an unavailable online revocation
    # lookup is disabled for the Windows PowerShell preflight requests.
    [Net.ServicePointManager]::CheckCertificateRevocationList = $false
    if ([string]::IsNullOrWhiteSpace($ImageFixture)) {
        $ImageFixture = Join-Path $fixtureRoot 'tga-smoke.png'
        New-SmokeImage -Path $ImageFixture
    }
    if ([string]::IsNullOrWhiteSpace($AudioFixture)) {
        $AudioFixture = Join-Path $fixtureRoot 'tga-smoke.wav'
        New-SmokeAudio -Path $AudioFixture
    }
    if ([string]::IsNullOrWhiteSpace($VideoFixture)) {
        $VideoFixture = Join-Path $fixtureRoot 'tga-smoke.mp4'
        New-SmokeVideo -Directory $fixtureRoot -ImagePath $ImageFixture -AudioPath $AudioFixture -OutputPath $VideoFixture
    }

    Assert-HttpStatus -Uri ($ServerUrl.TrimEnd('/') + '/v1/health/live') -ExpectedStatus 200
    Assert-HttpStatus -Uri ($ServerUrl.TrimEnd('/') + '/v1/capabilities') -ExpectedStatus 401
    Assert-HttpStatus -Uri ($ServerUrl.TrimEnd('/') + '/v1/capabilities') -ExpectedStatus 401 -Headers @{ 'X-GO-AI-Key' = 'invalid-smoke-key' }

    $env:GO_AI_API_KEY = $apiKey
    $env:GO_AI_SERVER_URL = $ServerUrl
    $env:GO_AI_ROOT_CERTIFICATE = $RootCertificatePath
    $smokeClientArguments = @(
        'live-smoke',
        '--image', $ImageFixture,
        '--audio', $AudioFixture,
        '--video', $VideoFixture,
        '--output', $OutputDirectory
    )
    & $SmokeClientPath @smokeClientArguments
    if ($LASTEXITCODE -ne 0) {
        throw "GO AI live smoke client failed with exit code $LASTEXITCODE."
    }

    $internalPorts = @(7080, 7081, 7082, 7083, 7084, 7085, 7086)
    $unsafeListeners = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object {
        $_.LocalPort -in $internalPorts -and $_.LocalAddress -notin @('127.0.0.1', '::1')
    })
    if ($unsafeListeners.Count -ne 0) {
        throw 'At least one GO AI internal service is reachable beyond loopback.'
    }
    $lmStudioLanListener = @(Get-NetTCPConnection -State Listen -LocalPort 1234 -ErrorAction SilentlyContinue |
        Where-Object { $_.LocalAddress -in @('0.0.0.0', '::') })
    if ($lmStudioLanListener.Count -eq 0) {
        throw 'LM Studio is not serving the local network on port 1234.'
    }
}
finally {
    $env:GO_AI_API_KEY = $previousApiKey
    $env:GO_AI_SERVER_URL = $previousServerUrl
    $env:GO_AI_ROOT_CERTIFICATE = $previousRootCertificate
    [Net.ServicePointManager]::CheckCertificateRevocationList = $previousCertificateRevocationCheck
    if (Test-Path -LiteralPath $fixtureRoot) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}

Write-Host "GO AI Server live model and worker smokes passed: $ServerUrl" -ForegroundColor Green
