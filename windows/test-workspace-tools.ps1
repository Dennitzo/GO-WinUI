#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('General', 'Coding', 'Both')][string] $Mode = 'Both',
    [ValidateSet('document', 'blender', 'visual')][string[]] $Scenario = @('document', 'blender', 'visual'),
    [switch] $SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$evidence = Join-Path $repo 'artifacts\validation\workspace-live'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$checks = [Collections.Generic.List[object]]::new()
$names = @('GO_WORKSPACE_TOOLS_LIVE', 'GO_WORKSPACE_LIVE_MODE', 'GO_WORKSPACE_LIVE_SCENARIO', 'GO_WORKSPACE_LIVE_EVIDENCE')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
Push-Location $repo
try {
    if (-not $SkipBuild) {
        dotnet build tests/GoWinUI.Tests/GoWinUI.Tests.csproj -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Workspace-Testbuild fehlgeschlagen.' }
    }
    $env:GO_WORKSPACE_TOOLS_LIVE = '1'
    $env:GO_WORKSPACE_LIVE_EVIDENCE = $evidence
    $modes = if ($Mode -eq 'Both') { @('General', 'Coding') } else { @($Mode) }
    foreach ($selectedMode in $modes) {
        foreach ($selectedScenario in $Scenario) {
            $env:GO_WORKSPACE_LIVE_MODE = $selectedMode
            $env:GO_WORKSPACE_LIVE_SCENARIO = $selectedScenario
            $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
            $log = Join-Path $evidence "$stamp-$selectedMode-$selectedScenario.log"
            Write-Host "Echte Werkzeugabnahme: $selectedMode / $selectedScenario"
            # Native stderr is not authoritative in Windows PowerShell; use exit code.
            $ErrorActionPreference = 'Continue'
            dotnet test tests/GoWinUI.Tests/GoWinUI.Tests.csproj -c Release --no-build `
                --filter 'FullyQualifiedName~WorkspaceToolsLiveTests' --logger 'console;verbosity=normal' *> $log
            $code = $LASTEXITCODE
            $ErrorActionPreference = 'Stop'
            Get-Content -LiteralPath $log -Tail 8
            $checks.Add([ordered]@{ mode = $selectedMode; scenario = $selectedScenario; passed = ($code -eq 0); exitCode = $code; log = $log })
        }
    }
}
finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    [ordered]@{ checkedAtUtc = [DateTime]::UtcNow.ToString('o');
        passed = ($checks.Count -gt 0 -and @($checks | Where-Object { -not $_.passed }).Count -eq 0);
        checks = @($checks.ToArray()) } | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath (Join-Path $evidence ('summary-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')) -Encoding UTF8
    Pop-Location
}
if (@($checks | Where-Object { -not $_.passed }).Count -gt 0) { throw 'Mindestens eine echte Workspace-Werkzeugabnahme ist fehlgeschlagen. Siehe Protokolle.' }
