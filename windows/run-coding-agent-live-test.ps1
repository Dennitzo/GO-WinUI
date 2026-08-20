[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Coding', 'Excel', 'Physics', 'Einstein')]
    [string] $Scenario,

    [string] $Workspace,

    [ValidateSet('qwen3-coder-next', 'ud')]
    [string] $Model = 'qwen3-coder-next',

    [ValidateRange(1, 1000)]
    [int] $Iterations = 3,

    [switch] $Continuous,
    [switch] $ContinueExisting,
    [switch] $CurrentWindow,
    [switch] $NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = $MyInvocation.MyCommand.Path
$repositoryRoot = Split-Path -Parent $PSScriptRoot

function ConvertTo-SingleQuotedLiteral {
    param([Parameter(Mandatory)][string] $Value)
    return "'" + $Value.Replace("'", "''") + "'"
}

if (-not $CurrentWindow) {
    $arguments = @(
        '-Scenario ' + (ConvertTo-SingleQuotedLiteral $Scenario),
        '-Model ' + (ConvertTo-SingleQuotedLiteral $Model),
        '-Iterations ' + $Iterations,
        '-CurrentWindow'
    )
    if (-not [string]::IsNullOrWhiteSpace($Workspace)) {
        $arguments += '-Workspace ' + (ConvertTo-SingleQuotedLiteral $Workspace)
    }
    if ($Continuous) {
        $arguments += '-Continuous'
    }
    if ($ContinueExisting) {
        $arguments += '-ContinueExisting'
    }
    if ($NoBuild) {
        $arguments += '-NoBuild'
    }

    $childCommand = @(
        "& $(ConvertTo-SingleQuotedLiteral $scriptPath) $($arguments -join ' ')"
        'exit $LASTEXITCODE'
    ) -join [Environment]::NewLine
    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($childCommand))
    $process = Start-Process `
        -FilePath 'powershell.exe' `
        -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encodedCommand) `
        -WorkingDirectory $repositoryRoot `
        -WindowStyle Normal `
        -Wait `
        -PassThru
    exit $process.ExitCode
}

if ([string]::IsNullOrWhiteSpace($Workspace)) {
    $workspaceRoot = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'GitHub\GO-Coding-TestWorkspaces'
    $Workspace = Join-Path $workspaceRoot $Scenario.ToLowerInvariant()
}
$Workspace = [IO.Path]::GetFullPath($Workspace)
if (-not (Test-Path -LiteralPath $Workspace -PathType Container)) {
    New-Item -ItemType Directory -Path $Workspace -Force | Out-Null
}

$environmentNames = @(
    'GO_AI_LIVE_CODING_WORKSPACE',
    'GO_AI_LIVE_CODING_PROMPT',
    'GO_AI_LIVE_EXCEL_WORKSPACE',
    'GO_AI_LIVE_PHYSICS_WORKSPACE',
    'GO_AI_LIVE_PHYSICS_CONTINUE',
    'GO_AI_LIVE_EINSTEIN_WORKSPACE',
    'GO_AI_LIVE_EINSTEIN_ITERATIONS',
    'GO_AI_LIVE_EINSTEIN_CONTINUOUS',
    'GO_AI_LIVE_CODING_MODEL'
)
foreach ($name in $environmentNames) {
    [Environment]::SetEnvironmentVariable($name, $null, 'Process')
}
[Environment]::SetEnvironmentVariable('GO_AI_LIVE_CODING_MODEL', $Model, 'Process')

$testName = switch ($Scenario) {
    'Coding' {
        [Environment]::SetEnvironmentVariable('GO_AI_LIVE_CODING_WORKSPACE', $Workspace, 'Process')
        'GoWinUI.Tests.QwenCoderLiveTests.QwenCoderCanCompleteANaturalUserRequestInAnArbitraryWorkspace'
    }
    'Excel' {
        [Environment]::SetEnvironmentVariable('GO_AI_LIVE_EXCEL_WORKSPACE', $Workspace, 'Process')
        'GoWinUI.Tests.QwenCoderExcelLiveTests.QwenCoderCanCreateEditAndAnalyzeATgaVentilationWorkbook'
    }
    'Physics' {
        [Environment]::SetEnvironmentVariable('GO_AI_LIVE_PHYSICS_WORKSPACE', $Workspace, 'Process')
        if ($ContinueExisting) {
            [Environment]::SetEnvironmentVariable('GO_AI_LIVE_PHYSICS_CONTINUE', '1', 'Process')
        }
        'GoWinUI.Tests.QwenCoderPhysicsLiveTests.QwenCoderCanImplementAndVerifyAnalyticalAndNumericalQuantumMechanics'
    }
    'Einstein' {
        [Environment]::SetEnvironmentVariable('GO_AI_LIVE_EINSTEIN_WORKSPACE', $Workspace, 'Process')
        [Environment]::SetEnvironmentVariable('GO_AI_LIVE_EINSTEIN_ITERATIONS', $Iterations.ToString([Globalization.CultureInfo]::InvariantCulture), 'Process')
        if ($Continuous) {
            [Environment]::SetEnvironmentVariable('GO_AI_LIVE_EINSTEIN_CONTINUOUS', '1', 'Process')
        }
        'GoWinUI.Tests.QwenCoderEinsteinTests.QwenCoderContinuouslyStudiesEinsteinFieldEquationsAndQuantumGravityModels'
    }
}

$resultDirectory = Join-Path $repositoryRoot 'artifacts\coding-live-tests\test-results'
New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
$timestamp = [DateTimeOffset]::Now.ToString('yyyyMMdd-HHmmss', [Globalization.CultureInfo]::InvariantCulture)
$trxName = "$($Scenario.ToLowerInvariant())-$timestamp.trx"

Write-Host ''
Write-Host 'GO Coding-Agent-Livetest' -ForegroundColor Magenta
Write-Host "Szenario : $Scenario"
Write-Host "Modell   : $Model"
Write-Host "Workspace: $Workspace"
Write-Host "TRX      : $(Join-Path $resultDirectory $trxName)"
Write-Host 'JSONL    : artifacts\coding-live-tests\<Szenario>\latest.json verweist auf das aktive Protokoll.'
if ($Continuous) {
    Write-Host 'Dauermodus aktiv. Mit Ctrl+C kontrolliert beenden.' -ForegroundColor Yellow
}
Write-Host ''

$dotnetArguments = @(
    'test',
    'tests\GoWinUI.Tests\GoWinUI.Tests.csproj',
    '--configuration', 'Debug',
    '--filter', "FullyQualifiedName=$testName",
    '--logger', 'console;verbosity=detailed',
    '--logger', "trx;LogFileName=$trxName",
    '--results-directory', $resultDirectory
)
if ($NoBuild) {
    $dotnetArguments += '--no-build'
}

Push-Location $repositoryRoot
try {
    & dotnet @dotnetArguments
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
