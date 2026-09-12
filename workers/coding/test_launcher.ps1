#requires -Version 5.1
# Pure ownership/path regressions; never starts or stops a model process.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$launcher = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\windows\manage-coding-llama.ps1'))
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($launcher, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
foreach ($name in @('Test-NativeSupervisorIdentity', 'Write-NativeSupervisorOwner')) {
    $functionAst = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (-not $functionAst) { throw "Missing launcher function: $name" }
    # Evaluate only these bounded functions, never the launcher's entry point.
    . ([scriptblock]::Create($functionAst.Extent.Text))
}

function Assert-LauncherCheck {
    param([bool] $Condition, [string] $Description)
    if (-not $Condition) { throw "FAILED: $Description" }
    Write-Output "PASS: $Description"
}

$started = [DateTime]::SpecifyKind([DateTime]::Parse('2026-09-12T06:00:00'), [DateTimeKind]::Utc)
$process = [pscustomobject]@{ Id = 12345; StartTime = $started }
$savedScript = 'C:\Repo with spaces\workers\coding\catalog.py'
$state = 'C:\Users\Example\.go-winui\native-runtime'
$owner = [pscustomobject]@{ startedUtcTicks = $started.Ticks; catalogScript = $savedScript }
$command = '"C:\Python\python.exe" "' + $savedScript + '" "--state-directory" "' + $state + '"'
$catalogScript = 'C:\Portable\Assets\NativeRuntime\workers\coding\catalog.py'
Assert-LauncherCheck (Test-NativeSupervisorIdentity $owner $process $command $state) 'Portable launcher recognizes the recorded repository supervisor'
Assert-LauncherCheck (-not (Test-NativeSupervisorIdentity $owner ([pscustomobject]@{ StartTime = $started.AddSeconds(1) }) $command $state)) 'Reused PID/start-time mismatch is rejected'
Assert-LauncherCheck (-not (Test-NativeSupervisorIdentity $owner $process ($command.Replace('catalog.py"', 'catalog.py.other"')) $state)) 'Same-prefix script path is rejected'
Assert-LauncherCheck (-not (Test-NativeSupervisorIdentity $owner $process ($command.Replace('native-runtime"', 'native-runtime-other"')) $state)) 'Same-prefix state directory is rejected'
Assert-LauncherCheck (-not (Test-NativeSupervisorIdentity $owner $process '' $state)) 'Missing process command line is rejected'
Assert-LauncherCheck (-not (Test-NativeSupervisorIdentity $owner $null $command $state)) 'Exited supervisor is rejected'
$rootState = 'D:\'
$rootCommand = 'python "' + $savedScript + '" "--state-directory" "D:\\"'
Assert-LauncherCheck (Test-NativeSupervisorIdentity $owner $process $rootCommand $rootState) 'Quoted state root preserves the trailing slash'
$stateParameter = $ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'StateDirectory' }
Assert-LauncherCheck ($stateParameter.DefaultValue.Extent.Text -match '\$env:USERPROFILE' -and $stateParameter.DefaultValue.Extent.Text -match '\.go-winui\\native-runtime') 'Default state path is independent of virtualized LocalAppData'

$fixtureParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$fixture = Join-Path $fixtureParent ('go-native-launcher-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
try {
    $ownerFile = Join-Path $fixture 'supervisor.json'
    $BinaryPath = 'C:\Unsloth\llama-server.exe'
    $Port = 8081
    Write-NativeSupervisorOwner $process
    $first = Get-Content -LiteralPath $ownerFile -Raw | ConvertFrom-Json
    Assert-LauncherCheck ($first.processId -eq 12345 -and $first.catalogScript -eq $catalogScript) 'New owner stores the exact launched script identity'
    $process.Id = 23456
    Write-NativeSupervisorOwner $process
    $second = Get-Content -LiteralPath $ownerFile -Raw | ConvertFrom-Json
    Assert-LauncherCheck ($second.processId -eq 23456 -and -not (Test-Path -LiteralPath ($ownerFile + '.new'))) 'Owner replacement publishes complete JSON without a leftover temporary file'
    $portableWindows = Join-Path $fixture 'Assets\NativeRuntime\windows'
    New-Item -ItemType Directory -Path $portableWindows -Force | Out-Null
    $portableLauncher = Join-Path $portableWindows 'manage-coding-llama.ps1'
    Copy-Item -LiteralPath $launcher -Destination $portableLauncher
    $emptyState = Join-Path $fixture 'empty-state'
    $status = & $portableLauncher -Action Status -StateDirectory $emptyState
    Assert-LauncherCheck (-not $status.Running -and -not $status.Healthy -and $status.StateDirectory -eq $emptyState) 'Portable status works without repository files or a running process'
    $missingCatalogMessage = ''
    try { & $portableLauncher -Action Start -StateDirectory $emptyState -ModelRoot $fixture -BinaryPath (Join-Path $fixture 'missing.exe') | Out-Null }
    catch { $missingCatalogMessage = $_.Exception.Message }
    Assert-LauncherCheck ($missingCatalogMessage -like 'Native model catalog script not found:*Assets\NativeRuntime\workers\coding\catalog.py*') 'Incomplete portable bundle fails before any process start with its exact missing path'
    $errorFile = Join-Path $fixture 'startup-error.txt'
    # The separate PowerShell invocation exits before reaching any native
    # process launch; its exit status and error-file contract match GUI callers.
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $errorOutput = & powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $portableLauncher -Action Start -StateDirectory $emptyState -ModelRoot $fixture -ErrorFile $errorFile 2>&1
        $errorExitCode = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previousPreference }
    Assert-LauncherCheck ($errorExitCode -eq 1) 'GUI launcher failure returns exit code one'
    $savedError = [IO.File]::ReadAllText($errorFile)
    Assert-LauncherCheck ($savedError -eq $missingCatalogMessage) 'GUI error file preserves the precise original exception message'
    Assert-LauncherCheck (($errorOutput | Out-String) -match 'Native model catalog script not found') 'Error-file mode retains CLI error output'
} finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    if (-not $resolvedFixture.StartsWith($fixtureParent + '\go-native-launcher-test-', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup target.' }
    $items = @(Get-Item -LiteralPath $resolvedFixture) + @(Get-ChildItem -LiteralPath $resolvedFixture -Force -Recurse)
    if ($items | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }) { throw 'Refusing test cleanup through a reparse point.' }
    Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
}
