#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Start', 'Stop', 'Status')][string] $Action = 'Start',
    [Alias('NativeModelRoot')][string] $ModelRoot = (Join-Path $env:USERPROFILE '.cache\huggingface\hub'),
    [string] $BinaryPath = (Join-Path $env:USERPROFILE '.unsloth\llama.cpp\build\bin\Release\llama-server.exe'),
    [string] $PythonPath,
    # AppData is virtualized when launched from an MSIX host such as Codex.
    # Keep one identity/log directory for repository and portable app launches.
    [string] $StateDirectory = (Join-Path $env:USERPROFILE '.go-winui\native-runtime'),
    [ValidateRange(1024, 65535)][int] $Port = 8081,
    # Each GPU keeps this reserve; llama fits from the model's native context maximum.
    [ValidateRange(256, 32768)][int] $FitTargetMiB = 2048,
    [string] $GpuLayers = 'auto',
    # GUI callers do not redirect process pipes: the long-lived Python child
    # could inherit them and prevent ReadToEndAsync from ever completing.
    [string] $ErrorFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
trap {
    if ([string]::IsNullOrWhiteSpace($ErrorFile)) { break }
    $startupError = $_
    try {
        [IO.File]::WriteAllText([IO.Path]::GetFullPath($ErrorFile), $startupError.Exception.Message, [Text.UTF8Encoding]::new($false))
    } catch {
        [Console]::Error.WriteLine('Could not write the native runtime startup error file: ' + $_.Exception.Message)
    }
    Write-Error -ErrorRecord $startupError -ErrorAction Continue
    exit 1
}
$catalogScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\workers\coding\catalog.py'))
$StateDirectory = [IO.Path]::GetFullPath($StateDirectory)
$ownerFile = Join-Path $StateDirectory 'supervisor.json'
$stopFile = Join-Path $StateDirectory 'stop.requested'

function Test-NativeSupervisorIdentity {
    param($Owner, $Process, [string] $CommandLine, [string] $ExpectedStateDirectory)
    if ($null -eq $Process -or $Process.StartTime.ToUniversalTime().Ticks -ne [long]$Owner.startedUtcTicks) { return $false }
    # An existing supervisor may have been started by another copy of GO. Check
    # its recorded script, not this invocation's repository/portable script path.
    $savedScript = [string]$Owner.catalogScript
    if ([string]::IsNullOrWhiteSpace($savedScript) -or -not [IO.Path]::IsPathRooted($savedScript) -or
        [IO.Path]::GetFileName($savedScript) -ne 'catalog.py' -or [string]::IsNullOrWhiteSpace($CommandLine)) { return $false }
    # Both script and state arguments are quoted by this launcher. Exact tokens
    # prevent a same-prefix path from being mistaken for the saved identity.
    $scriptArgument = '"' + $savedScript + '"'
    $stateArgument = '"' + ($ExpectedStateDirectory -replace '(\\+)$', '$1$1') + '"'
    return $CommandLine.IndexOf($scriptArgument, [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
        $CommandLine.IndexOf('--state-directory', [StringComparison]::Ordinal) -ge 0 -and
        $CommandLine.IndexOf($stateArgument, [StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Write-NativeSupervisorOwner {
    param($Process)
    $temporaryOwner = $ownerFile + '.new'
    [IO.File]::WriteAllText($temporaryOwner, (@{ processId = $Process.Id; startedUtcTicks = $Process.StartTime.ToUniversalTime().Ticks
        catalogScript = $catalogScript; binary = [IO.Path]::GetFullPath($BinaryPath); port = $Port } | ConvertTo-Json))
    if ([IO.File]::Exists($ownerFile)) { [IO.File]::Replace($temporaryOwner, $ownerFile, [NullString]::Value) }
    else { [IO.File]::Move($temporaryOwner, $ownerFile) }
}

function Get-OwnedSupervisor {
    if (-not (Test-Path -LiteralPath $ownerFile -PathType Leaf)) { return $null }
    $owner = Get-Content -LiteralPath $ownerFile -Raw | ConvertFrom-Json
    $process = Get-Process -Id $owner.processId -ErrorAction SilentlyContinue
    if ($null -eq $process -or $process.StartTime.ToUniversalTime().Ticks -ne [long]$owner.startedUtcTicks) { return $null }
    $native = Get-CimInstance Win32_Process -Filter "ProcessId = $($process.Id)"
    if ($null -eq $native -or -not (Test-NativeSupervisorIdentity -Owner $owner -Process $process -CommandLine $native.CommandLine -ExpectedStateDirectory $StateDirectory)) {
        throw 'The saved process identity does not match this native model supervisor; no process will be stopped.'
    }
    return $process
}

function Show-NativeStatus {
    $process = Get-OwnedSupervisor
    $healthy = $false
    $models = @()
    if ($process) {
        try {
            $null = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/health" -TimeoutSec 3
            $models = @( (Invoke-RestMethod -Uri "http://127.0.0.1:$Port/v1/models?reload=1" -TimeoutSec 5).data )
            $healthy = $true
        } catch { $healthy = $false }
    }
    [pscustomobject]@{ Running = $null -ne $process; Healthy = $healthy
        SupervisorId = if ($process) { $process.Id } else { $null }
        Endpoint = "http://127.0.0.1:$Port"; ModelCount = $models.Count; StateDirectory = $StateDirectory }
}

if ($Action -in @('Status', 'Stop') -and (Test-Path -LiteralPath $ownerFile -PathType Leaf)) {
    $Port = [int](Get-Content -LiteralPath $ownerFile -Raw | ConvertFrom-Json).port
}
if ($Action -eq 'Status') { Show-NativeStatus; return }
if ($Action -eq 'Stop') {
    $process = Get-OwnedSupervisor
    if ($process) {
        [IO.File]::WriteAllText($stopFile, 'stop')
        if (-not $process.WaitForExit(15000)) {
            $verified = Get-OwnedSupervisor
            if ($verified) { Stop-Process -Id $verified.Id -Force }
        }
    }
    Show-NativeStatus
    return
}

if (Get-OwnedSupervisor) {
    $runningState = Get-Content -LiteralPath (Join-Path $StateDirectory 'runtime.json') -Raw | ConvertFrom-Json
    if ([IO.Path]::GetFullPath($runningState.modelRoot) -ne [IO.Path]::GetFullPath($ModelRoot) -or
        [IO.Path]::GetFullPath($runningState.binary) -ne [IO.Path]::GetFullPath($BinaryPath) -or [int]$runningState.port -ne $Port) {
        throw 'The managed native runtime is already running with different paths or port. Stop it before changing its configuration.'
    }
    $status = Show-NativeStatus
    if (-not $status.Healthy) {
        throw "The managed native supervisor is running but its model endpoint is not ready. Inspect $StateDirectory\llama.stderr.log and $StateDirectory\stderr.log"
    }
    $status
    return
}
if (-not (Test-Path -LiteralPath $catalogScript -PathType Leaf)) { throw "Native model catalog script not found: $catalogScript. Restore the complete Assets\NativeRuntime folder from the portable package." }
if (-not (Test-Path -LiteralPath $ModelRoot -PathType Container)) { throw "Local model directory not found: $ModelRoot" }
if (-not (Test-Path -LiteralPath $BinaryPath -PathType Leaf)) { throw "Native llama-server.exe not found: $BinaryPath. Specify an existing binary with -BinaryPath." }
if ([string]::IsNullOrWhiteSpace($PythonPath)) {
    $unslothPython = Join-Path $env:USERPROFILE '.unsloth\studio\unsloth_studio\Scripts\python.exe'
    $PythonPath = if (Test-Path -LiteralPath $unslothPython -PathType Leaf) { $unslothPython } else { (Get-Command python.exe -ErrorAction Stop).Source }
}
if (-not (Test-Path -LiteralPath $PythonPath -PathType Leaf)) { throw "Python interpreter not found: $PythonPath" }
if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) { throw "Port $Port is already occupied; its existing process will not be changed." }
$help = (& $BinaryPath --help 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0 -or $help -notmatch '--models-preset' -or $help -notmatch '--fit-target' -or $help -notmatch '--fit-ctx' -or
    $help -notmatch '--embedding' -or $help -notmatch '--mmproj' -or $help -notmatch '--tags' -or
    $help -notmatch '--reasoning-effort' -or $help -notmatch '--reasoning-budget' -or $help -notmatch '--sleep-idle-seconds') {
    throw 'The existing native llama binary does not support the required model router, memory fitting and model reasoning options.'
}
New-Item -ItemType Directory -Path $StateDirectory -Force | Out-Null
$arguments = @($catalogScript, '--model-root', [IO.Path]::GetFullPath($ModelRoot), '--binary', [IO.Path]::GetFullPath($BinaryPath),
    '--state-directory', $StateDirectory, '--port', [string]$Port, '--fit-target', [string]$FitTargetMiB, '--gpu-layers', $GpuLayers)
$quotedArguments = ($arguments | ForEach-Object {
    if ($_ -match '"') { throw 'An argument contains an unsupported double quote.' }
    # Windows argument parsing consumes backslashes before the closing quote.
    # Preserve directory roots and paths ending in a separator by doubling them.
    '"' + ($_ -replace '(\\+)$', '$1$1') + '"'
}) -join ' '
$process = Start-Process -FilePath $PythonPath -ArgumentList $quotedArguments -WorkingDirectory $StateDirectory -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $StateDirectory 'stdout.log') -RedirectStandardError (Join-Path $StateDirectory 'stderr.log') -PassThru
Write-NativeSupervisorOwner -Process $process
$deadline = [DateTime]::UtcNow.AddSeconds(30)
do {
    if ($process.HasExited) { throw "Native model supervisor exited. Inspect $StateDirectory\stderr.log" }
    try {
        $null = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/health" -TimeoutSec 2
        # A venv python.exe can be a launcher for another interpreter process.
        # Persist the actual supervisor so forced shutdown closes its Job Object.
        $runtimeState = Get-Content -LiteralPath (Join-Path $StateDirectory 'runtime.json') -Raw | ConvertFrom-Json
        $actualSupervisor = Get-Process -Id $runtimeState.supervisorPid -ErrorAction Stop
        Write-NativeSupervisorOwner -Process $actualSupervisor
        Show-NativeStatus
        return
    } catch { Start-Sleep -Milliseconds 500 }
} while ([DateTime]::UtcNow -lt $deadline)
throw "Native model supervisor did not become ready within 30 seconds. Inspect $StateDirectory\stderr.log"
