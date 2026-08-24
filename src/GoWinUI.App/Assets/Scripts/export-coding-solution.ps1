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

function Invoke-EdgeProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Executable,

        [Parameter(Mandatory = $true)]
        [string[]]$CommandArguments
    )

    # Chromium can emit harmless diagnostics on stderr while returning exit
    # code zero. Windows PowerShell otherwise promotes those lines to a
    # terminating NativeCommandError because this script runs in Stop mode.
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $capturedOutput = (& $Executable @CommandArguments 2>&1 | Out-String)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }

    return [pscustomobject]@{
        ExitCode = [int]$exitCode
        Output = [string]$capturedOutput
    }
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
$validationProfilePath = Join-Path $temporaryRoot 'edge-profile-validation'
$printProfilePath = Join-Path $temporaryRoot 'edge-profile-print'
[IO.Directory]::CreateDirectory($validationProfilePath) | Out-Null
[IO.Directory]::CreateDirectory($printProfilePath) | Out-Null
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
    const solutionContent = document.getElementById('solution-content');
    solutionContent.append(globalThis.goMarkdown.render(decodeUtf8('$sourceBase64')));
    document.body.dataset.goPdfReady = 'true';
    document.body.dataset.goKatexInvalid = String(solutionContent.querySelectorAll('.math-selectable.invalid').length);
    document.body.dataset.goKatexRendered = String(solutionContent.querySelectorAll('.math-render[data-math-typeset="true"] .katex').length);
  </script>
</body>
</html>
"@

try {
    [IO.File]::WriteAllText($htmlPath, $html, [Text.UTF8Encoding]::new($false))
    $edge = Resolve-EdgeExecutable
    $commonArguments = @(
        '--headless=new',
        '--disable-gpu',
        '--disable-extensions',
        '--disable-background-networking',
        '--no-first-run',
        '--allow-file-access-from-files',
        '--run-all-compositor-stages-before-draw',
        '--virtual-time-budget=5000'
    )
    $dumpArguments = $commonArguments + @(
        ('--user-data-dir=' + $validationProfilePath),
        '--dump-dom',
        ([Uri]$htmlPath).AbsoluteUri
    )

    $validationResult = Invoke-EdgeProcess -Executable $edge -CommandArguments $dumpArguments
    $renderedDom = $validationResult.Output
    if ($validationResult.ExitCode -ne 0) {
        throw "Microsoft Edge hat die KaTeX-Prüfung mit Exit-Code $($validationResult.ExitCode) beendet."
    }
    if ($renderedDom -notmatch 'data-go-pdf-ready="true"') {
        throw 'Der GO-Markdown-/KaTeX-Renderer wurde vor der PDF-Erzeugung nicht vollständig initialisiert.'
    }
    if ($renderedDom -match 'data-go-katex-invalid="([1-9][0-9]*)"') {
        throw "Die PDF wurde nicht erzeugt, weil $($Matches[1]) mathematische Ausdrücke nicht KaTeX-kompatibel sind."
    }

    $arguments = $commonArguments + @(
        ('--user-data-dir=' + $printProfilePath),
        '--no-pdf-header-footer',
        ('--print-to-pdf=' + $temporaryPdf),
        ([Uri]$htmlPath).AbsoluteUri
    )

    $printResult = Invoke-EdgeProcess -Executable $edge -CommandArguments $arguments
    if ($printResult.ExitCode -ne 0) {
        throw "Microsoft Edge hat die PDF-Erzeugung mit Exit-Code $($printResult.ExitCode) beendet."
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
