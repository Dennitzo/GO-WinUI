#requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true)]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$artifactRoot = [IO.Path]::GetFullPath((Resolve-GoRepositoryPath -RelativePath 'artifacts')).TrimEnd('\')
$obsoleteEntries = @(
    'go-ai-server',
    'staging',
    'isolated-tests',
    'portable',
    'dashboard-maximize-test',
    'test-code-diff',
    'live-validation',
    'document-context-smoke',
    'coding-orchestrator-test',
    'test-agent-hardening',
    'live-validation-server',
    'manual-smoke',
    'model-selection-smoke',
    'deepseek-coding-smoke',
    'physics-live-test',
    'pdf-tool-smoke.pdf',
    'db-inspect\bin',
    'db-inspect\obj',
    'server-run-inspect\bin',
    'server-run-inspect\obj'
)

foreach ($entry in $obsoleteEntries) {
    $target = [IO.Path]::GetFullPath((Join-Path $artifactRoot $entry))
    if (-not $target.StartsWith($artifactRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing unsafe artifact cleanup target: $target"
    }
    if ((Test-Path -LiteralPath $target) -and $PSCmdlet.ShouldProcess($target, 'Remove obsolete generated artifact')) {
        Remove-Item -LiteralPath $target -Recurse -Force
        Write-Host "Removed $target" -ForegroundColor DarkGray
    }
}

Write-Host 'Obsolete generated artifacts removed; diagnostic traces and inspection sources were retained.' -ForegroundColor Green
