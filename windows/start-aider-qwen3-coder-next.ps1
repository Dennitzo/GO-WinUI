[CmdletBinding()]
param(
    [string] $Workspace = 'C:\Users\AMD\Documents\GitHub\LokaleAI',
    [string] $ModelKey = 'qwen3-coder-next',
    [string] $LmStudioApiBase = 'http://127.0.0.1:1234/v1',
    [bool] $InstallAiderIfMissing = $true,
    [switch] $KeepOtherModels,
    [switch] $ValidateOnly,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $AdditionalAiderArguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-LmsJson {
    param([Parameter(Mandatory)][string[]] $Arguments)

    $output = & lms @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "lms $($Arguments -join ' ') ist mit Exit-Code $LASTEXITCODE fehlgeschlagen."
    }

    if ([string]::IsNullOrWhiteSpace(($output -join [Environment]::NewLine))) {
        return @()
    }

    return @($output | ConvertFrom-Json)
}

function Test-PythonModule {
    param(
        [Parameter(Mandatory)][string] $PythonExecutable,
        [Parameter(Mandatory)][string] $ModuleName
    )

    # Ein fehlendes Modul ist ein erwartetes Pruefergebnis und darf unter
    # Windows PowerShell nicht als NativeCommandError abbrechen.
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $PythonExecutable -c "import importlib.util, sys; sys.exit(0 if importlib.util.find_spec('$ModuleName') else 1)" *> $null
        return $LASTEXITCODE -eq 0
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
}

function Get-PythonScriptsDirectory {
    param([Parameter(Mandatory)][string] $PythonExecutable)

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $directory = & $PythonExecutable -c "import sysconfig; print(sysconfig.get_path('scripts'))" 2>$null
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace(($directory -join ''))) {
            return [string] ($directory | Select-Object -First 1)
        }
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    return $null
}

function Get-PythonUserScriptsDirectory {
    param([Parameter(Mandatory)][string] $PythonExecutable)

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $directory = & $PythonExecutable -c "import sysconfig; print(sysconfig.get_path('scripts', scheme='nt_user'))" 2>$null
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace(($directory -join ''))) {
            return [string] ($directory | Select-Object -First 1)
        }
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    return $null
}

function Find-ConsoleExecutable {
    param(
        [Parameter(Mandatory)][string] $Name,
        [string] $PythonExecutable
    )

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $candidateDirectories = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($PythonExecutable)) {
        $pythonScriptsDirectory = Get-PythonScriptsDirectory -PythonExecutable $PythonExecutable
        if (-not [string]::IsNullOrWhiteSpace($pythonScriptsDirectory)) {
            $candidateDirectories.Add($pythonScriptsDirectory)
        }
        $pythonUserScriptsDirectory = Get-PythonUserScriptsDirectory -PythonExecutable $PythonExecutable
        if (-not [string]::IsNullOrWhiteSpace($pythonUserScriptsDirectory)) {
            $candidateDirectories.Add($pythonUserScriptsDirectory)
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
        # aider-install/uv legt Windows-Konsolenwerkzeuge standardmaessig hier ab.
        $candidateDirectories.Add((Join-Path $env:USERPROFILE '.local\bin'))
    }

    foreach ($directory in $candidateDirectories | Select-Object -Unique) {
        foreach ($fileName in @("$Name.exe", "$Name.cmd", "$Name.bat", $Name)) {
            $candidate = Join-Path $directory $fileName
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                return [System.IO.Path]::GetFullPath($candidate)
            }
        }
    }

    return $null
}

function Invoke-NativeInstaller {
    param(
        [Parameter(Mandatory)][string] $Executable,
        [string[]] $Arguments = @()
    )

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        # Installer schreiben auch harmlose Hinweise auf stderr. Der Exit-Code
        # bleibt die autoritative Erfolgspruefung.
        $ErrorActionPreference = 'Continue'
        & $Executable @Arguments | Out-Host
        return $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
}

function Resolve-AiderCommand {
    $python = Get-Command python -ErrorAction SilentlyContinue
    $pythonExecutable = if ($null -ne $python) { $python.Source } else { $null }
    $aiderExecutable = Find-ConsoleExecutable -Name 'aider' -PythonExecutable $pythonExecutable
    if (-not [string]::IsNullOrWhiteSpace($aiderExecutable)) {
        return [pscustomobject]@{
            Executable = $aiderExecutable
            PrefixArgs = @()
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($pythonExecutable) -and
        (Test-PythonModule -PythonExecutable $pythonExecutable -ModuleName 'aider')) {
        return [pscustomobject]@{
            Executable = $pythonExecutable
            PrefixArgs = @('-m', 'aider')
        }
    }

    if (-not $InstallAiderIfMissing) {
        throw 'Aider wurde nicht gefunden. Installiere es mit: python -m pip install aider-install; aider-install'
    }
    if ([string]::IsNullOrWhiteSpace($pythonExecutable)) {
        throw 'Python wurde nicht gefunden. Installiere Python und danach Aider.'
    }

    Write-Host 'Aider wird einmalig installiert ...' -ForegroundColor Cyan
    $pipExitCode = Invoke-NativeInstaller -Executable $pythonExecutable -Arguments @('-m', 'pip', 'install', '--upgrade', 'aider-install')
    if ($pipExitCode -ne 0) {
        throw 'aider-install konnte nicht installiert werden.'
    }
    $installerExecutable = Find-ConsoleExecutable -Name 'aider-install' -PythonExecutable $pythonExecutable
    if ([string]::IsNullOrWhiteSpace($installerExecutable)) {
        throw 'aider-install wurde installiert, der Konsolenbefehl konnte jedoch nicht gefunden werden.'
    }

    # Der offizielle Installer richtet Aider isoliert mit Python 3.12 ein.
    $installerExitCode = Invoke-NativeInstaller -Executable $installerExecutable
    if ($installerExitCode -ne 0) {
        throw "aider-install ist mit Exit-Code $installerExitCode fehlgeschlagen."
    }

    $aiderExecutable = Find-ConsoleExecutable -Name 'aider' -PythonExecutable $pythonExecutable
    if (-not [string]::IsNullOrWhiteSpace($aiderExecutable)) {
        return [pscustomobject]@{
            Executable = $aiderExecutable
            PrefixArgs = @()
        }
    }

    if (Test-PythonModule -PythonExecutable $pythonExecutable -ModuleName 'aider') {
        return [pscustomobject]@{
            Executable = $pythonExecutable
            PrefixArgs = @('-m', 'aider')
        }
    }

    throw 'Aider wurde installiert, ist in dieser PowerShell-Sitzung aber noch nicht aufrufbar. Öffne PowerShell erneut und starte das Skript noch einmal.'
}

$resolvedWorkspace = [System.IO.Path]::GetFullPath($Workspace)
if (-not (Test-Path -LiteralPath $resolvedWorkspace -PathType Container)) {
    throw "Der Workspace wurde nicht gefunden: $resolvedWorkspace"
}

if ($null -eq (Get-Command lms -ErrorAction SilentlyContinue)) {
    throw 'Die LM-Studio-CLI "lms" wurde nicht gefunden. Installiere sie in LM Studio und öffne PowerShell erneut.'
}

$catalog = Invoke-LmsJson -Arguments @('ls', '--json')
$model = $catalog | Where-Object { $_.modelKey -eq $ModelKey } | Select-Object -First 1
if ($null -eq $model) {
    throw "Das LM-Studio-Modell '$ModelKey' wurde nicht gefunden. Verfügbare Modelle: $($catalog.modelKey -join ', ')"
}

$maximumContextLength = [int64] $model.maxContextLength
if ($maximumContextLength -le 0) {
    throw "LM Studio meldet für '$ModelKey' kein gültiges Kontextlimit."
}

# Qwen dokumentiert maximal 65.536 neue Token. Der Eingabebereich reserviert
# diesen Platz innerhalb des nativen Gesamtfensters von 262.144 Token.
$maximumOutputTokens = [Math]::Min([int64] 65536, [Math]::Max([int64] 4096, $maximumContextLength / 4))
$maximumInputTokens = $maximumContextLength - $maximumOutputTokens

# Der Serverstart ist idempotent. Eine bereits laufende Instanz bleibt aktiv.
& lms server start | Out-Host

$loadedModels = Invoke-LmsJson -Arguments @('ps', '--json')
if (-not $KeepOtherModels) {
    foreach ($loadedModel in @($loadedModels | Where-Object { $_.modelKey -ne $ModelKey })) {
        if ($loadedModel.status -notin @('idle', 'loaded')) {
            throw "Das Modell '$($loadedModel.identifier)' arbeitet gerade und wird nicht automatisch entladen."
        }
        Write-Host "Entlade anderes Modell: $($loadedModel.identifier)" -ForegroundColor DarkYellow
        & lms unload $loadedModel.identifier | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "Das Modell '$($loadedModel.identifier)' konnte nicht entladen werden."
        }
    }
}

$loadedModels = Invoke-LmsJson -Arguments @('ps', '--json')
$loaded = $loadedModels | Where-Object { $_.modelKey -eq $ModelKey } | Select-Object -First 1
$requiresReload = $null -eq $loaded -or [int64] $loaded.contextLength -ne $maximumContextLength
if ($requiresReload -and $null -ne $loaded) {
    if ($loaded.status -notin @('idle', 'loaded')) {
        throw "Das Modell '$ModelKey' arbeitet gerade und kann nicht mit maximalem Kontext neu geladen werden."
    }
    Write-Host "Lade '$ModelKey' wegen abweichendem Kontext neu ..." -ForegroundColor DarkYellow
    & lms unload $loaded.identifier | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Das Modell '$ModelKey' konnte nicht entladen werden."
    }
}

if ($requiresReload) {
    Write-Host "Lade '$ModelKey' mit $maximumContextLength Kontexttoken ..." -ForegroundColor Cyan
    & lms load $ModelKey `
        --context-length $maximumContextLength `
        --gpu max `
        --parallel 1 `
        --identifier $ModelKey `
        --yes | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Das Modell '$ModelKey' konnte nicht geladen werden."
    }
}
else {
    Write-Host "'$ModelKey' ist bereits mit $maximumContextLength Kontexttoken geladen." -ForegroundColor Green
}

$modelsEndpoint = $LmStudioApiBase.TrimEnd('/') + '/models'
try {
    $null = Invoke-RestMethod -Uri $modelsEndpoint -Method Get -TimeoutSec 15
}
catch {
    throw "Die LM-Studio-API ist unter '$modelsEndpoint' nicht erreichbar: $($_.Exception.Message)"
}

$aider = Resolve-AiderCommand
$aiderModel = "lm_studio/$ModelKey"
$metadataPath = Join-Path ([System.IO.Path]::GetTempPath()) 'go-aider-qwen3-coder-next.metadata.json'
$metadata = @{
    $aiderModel = @{
        max_tokens           = $maximumOutputTokens
        max_input_tokens     = $maximumInputTokens
        max_output_tokens    = $maximumOutputTokens
        input_cost_per_token = 0
        output_cost_per_token = 0
        litellm_provider     = 'lm_studio'
        mode                 = 'chat'
    }
} | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText(
    $metadataPath,
    $metadata,
    [System.Text.UTF8Encoding]::new($false))

$env:LM_STUDIO_API_KEY = 'lm-studio'
$env:LM_STUDIO_API_BASE = $LmStudioApiBase.TrimEnd('/')
$env:AIDER_MAX_CHAT_HISTORY_TOKENS = [string] $maximumInputTokens

Write-Host ''
Write-Host "Workspace:       $resolvedWorkspace" -ForegroundColor Cyan
Write-Host "Modell:          $aiderModel" -ForegroundColor Cyan
Write-Host "Gesamtkontext:   $maximumContextLength Token" -ForegroundColor Cyan
Write-Host "Eingabebudget:   $maximumInputTokens Token" -ForegroundColor Cyan
Write-Host "Ausgabereserve:  $maximumOutputTokens Token" -ForegroundColor Cyan
Write-Host "Aider:           $($aider.Executable)" -ForegroundColor Cyan
Write-Host ''

if ($ValidateOnly) {
    Write-Host 'Validierung abgeschlossen; Aider wurde nicht interaktiv gestartet.' -ForegroundColor Green
    return
}

Set-Location -LiteralPath $resolvedWorkspace
$aiderArguments = @(
    '--model', $aiderModel,
    '--weak-model', $aiderModel,
    '--editor-model', $aiderModel,
    '--model-metadata-file', $metadataPath,
    '--max-chat-history-tokens', [string] $maximumInputTokens,
    '--map-tokens', '4096',
    '--no-check-model-accepts-settings'
) + $AdditionalAiderArguments

$invocationArguments = @($aider.PrefixArgs) + $aiderArguments
& $aider.Executable @invocationArguments
exit $LASTEXITCODE
