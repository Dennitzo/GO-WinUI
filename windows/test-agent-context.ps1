#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string] $Configuration = 'Release',
    [switch] $SkipClientTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$repo = Get-GoRepositoryRoot
$evidence = Join-Path $repo 'artifacts\validation\agent-context'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$checks = [Collections.Generic.List[object]]::new()

function Invoke-ContextCheck {
    param([string] $Name, [string] $Executable, [string[]] $Arguments)
    Write-Host "Pruefe $Name"
    $log = Join-Path $evidence ($Name + '.log')
    $savedErrorAction = $ErrorActionPreference
    try {
        # Windows PowerShell represents native stderr as ErrorRecords even for
        # ordinary test diagnostics. The process exit code is authoritative.
        $ErrorActionPreference = 'Continue'
        & $Executable @Arguments *> $log
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $savedErrorAction }
    Get-Content -LiteralPath $log -Tail 12
    $checks.Add([ordered]@{ name = $Name; passed = ($code -eq 0); exitCode = $code; log = $log })
    if ($code -ne 0) { throw "Pflichttest fehlgeschlagen: $Name (Exit $code)" }
}

Push-Location $repo
try {
    if (-not $SkipClientTests) {
        Invoke-ContextCheck 'client' 'dotnet' @('test', 'tests\GoWinUI.Tests\GoWinUI.Tests.csproj', '-c', $Configuration,
            '--nologo', '--blame-hang-timeout', '3m', '--logger', 'console;verbosity=minimal',
            '--results-directory', $evidence, '--logger', 'trx;LogFileName=client.trx')
    }
    Invoke-ContextCheck 'server' 'dotnet' @('test', 'tests\GoAi.Server.Tests\GoAi.Server.Tests.csproj', '-c', $Configuration,
        '--nologo', '--blame-hang-timeout', '3m', '--logger', 'console;verbosity=minimal',
        '--results-directory', $evidence, '--logger', 'trx;LogFileName=server.trx')
    $webTests = @(Get-ChildItem -LiteralPath (Join-Path $repo 'tests\web') -Filter '*.test.cjs' | Sort-Object Name | ForEach-Object FullName)
    Invoke-ContextCheck 'web-state' 'node' (@('--test') + $webTests)
    Invoke-ContextCheck 'native-cache' 'python' @('-m', 'unittest', 'discover', '-s', 'workers/coding', '-p', 'test_*.py')
}
finally {
    [ordered]@{ schema = 'go.agent-context.validation.v1'; checkedAtUtc = [DateTime]::UtcNow.ToString('o');
        passed = ($checks.Count -eq $(if ($SkipClientTests) { 3 } else { 4 }) -and @($checks | Where-Object { -not $_.passed }).Count -eq 0);
        checks = @($checks.ToArray()); liveModelMeasurementIncluded = $false } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence 'summary.json') -Encoding UTF8
    Pop-Location
}
Write-Host 'Subagent-, Sitzungs-, Darstellungs- und Cache-Pflichttests bestanden.' -ForegroundColor Green
