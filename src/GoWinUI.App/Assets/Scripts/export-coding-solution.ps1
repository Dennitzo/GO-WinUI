[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath,

    [Parameter(Mandatory = $true)]
    [string]$WebAssetsPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Resolve-EdgeExecutable {
    $configured = [Environment]::GetEnvironmentVariable('GO_EDGE_PATH')
    $candidates = @(
        $configured,
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'),
        (Join-Path $env:ProgramFiles 'Microsoft\Edge\Application\msedge.exe'),
        (Join-Path $env:LOCALAPPDATA 'Microsoft\Edge\Application\msedge.exe')
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw 'Microsoft Edge wurde nicht gefunden. Setze bei Bedarf GO_EDGE_PATH auf msedge.exe.'
}

function Convert-ToFileUri([string]$Path) {
    return ([Uri](Resolve-Path -LiteralPath $Path).Path).AbsoluteUri
}

$source = (Resolve-Path -LiteralPath $SourcePath).Path
$webAssets = (Resolve-Path -LiteralPath $WebAssetsPath).Path
$output = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $output
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null

$stylesUri = Convert-ToFileUri (Join-Path $webAssets 'styles.css')
$markdownUri = Convert-ToFileUri (Join-Path $webAssets 'markdown.js')
$katexRoot = Join-Path $webAssets 'vendor\katex\0.16.10'
$katexCssUri = Convert-ToFileUri (Join-Path $katexRoot 'katex.min.css')
$katexScriptUri = Convert-ToFileUri (Join-Path $katexRoot 'katex.min.js')

$sourceText = [IO.File]::ReadAllText($source, [Text.Encoding]::UTF8)
$title = [IO.Path]::GetFileNameWithoutExtension($source)
$sourceBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($sourceText))
$titleBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($title))

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('GO-SolutionPdf-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
$htmlPath = Join-Path $temporaryRoot 'solution.html'
$profilePath = Join-Path $temporaryRoot 'edge-profile'
[IO.Directory]::CreateDirectory($profilePath) | Out-Null
$temporaryPdf = Join-Path $outputDirectory ('.' + [IO.Path]::GetFileNameWithoutExtension($output) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp.pdf')
$backupPdf = Join-Path $outputDirectory ('.' + [IO.Path]::GetFileNameWithoutExtension($output) + '.' + [Guid]::NewGuid().ToString('N') + '.bak.pdf')

$html = @"
<!doctype html>
<html lang="de">
<head>
  <meta charset="utf-8">
  <meta name="color-scheme" content="light">
  <link rel="stylesheet" href="$katexCssUri">
  <link rel="stylesheet" href="$stylesUri">
  <style>
    @page { size: A4 portrait; margin: 20mm 20mm 24mm 24mm; }
    html, body { margin: 0 !important; padding: 0 !important; background: #fff !important; color: #171717 !important; }
    .pdf-book { position: static !important; display: block !important; width: 100% !important; visibility: visible !important; }
  </style>
  <script src="$katexScriptUri"></script>
  <script src="$markdownUri"></script>
</head>
<body class="pdf-exporting">
  <article class="pdf-book pdf-book--message">
    <header class="pdf-book__header">
      <div class="pdf-book__eyebrow">GO · CODING WORKFLOW</div>
      <h1 id="solution-title"></h1>
      <p>Verifizierte Lösung · A4-Buchformat</p>
    </header>
    <section class="pdf-book__content">
      <article class="message assistant">
        <div class="message-body">
          <div id="solution-content" class="message-content"></div>
        </div>
      </article>
    </section>
    <footer class="pdf-book__end-mark">◆</footer>
  </article>
  <script>
    const decodeUtf8 = value => new TextDecoder().decode(Uint8Array.from(atob(value), character => character.charCodeAt(0)));
    document.getElementById('solution-title').textContent = decodeUtf8('$titleBase64');
    document.getElementById('solution-content').append(globalThis.goMarkdown.render(decodeUtf8('$sourceBase64')));
  </script>
</body>
</html>
"@

try {
    [IO.File]::WriteAllText($htmlPath, $html, [Text.UTF8Encoding]::new($false))
    $edge = Resolve-EdgeExecutable
    $arguments = @(
        '--headless=new',
        '--disable-gpu',
        '--disable-extensions',
        '--disable-background-networking',
        '--no-first-run',
        '--allow-file-access-from-files',
        '--run-all-compositor-stages-before-draw',
        '--no-pdf-header-footer',
        '--virtual-time-budget=5000',
        ('--user-data-dir=' + $profilePath),
        ('--print-to-pdf=' + $temporaryPdf),
        ([Uri]$htmlPath).AbsoluteUri
    )

    & $edge @arguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Microsoft Edge hat die PDF-Erzeugung mit Exit-Code $LASTEXITCODE beendet."
    }
    if (-not (Test-Path -LiteralPath $temporaryPdf -PathType Leaf)) {
        throw 'Microsoft Edge hat keine PDF-Datei erzeugt.'
    }

    $pdfBytes = [IO.File]::ReadAllBytes($temporaryPdf)
    if ($pdfBytes.Length -lt 1024 -or [Text.Encoding]::ASCII.GetString($pdfBytes, 0, 5) -ne '%PDF-') {
        throw 'Die erzeugte Datei ist kein gültiges, nicht leeres PDF.'
    }

    if (Test-Path -LiteralPath $output -PathType Leaf) {
        [IO.File]::Replace($temporaryPdf, $output, $backupPdf, $true)
        [IO.File]::Delete($backupPdf)
    }
    else {
        [IO.File]::Move($temporaryPdf, $output)
    }
    Write-Output $output
}
finally {
    if (Test-Path -LiteralPath $temporaryPdf -PathType Leaf) {
        Remove-Item -LiteralPath $temporaryPdf -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $backupPdf -PathType Leaf) {
        Remove-Item -LiteralPath $backupPdf -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $temporaryRoot -PathType Container) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
