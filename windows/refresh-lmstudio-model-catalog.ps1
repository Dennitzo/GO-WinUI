#requires -Version 5.1
[CmdletBinding()]
param(
    [string[]] $RequiredNameFragments = @(),

    [ValidateRange(1, 30)]
    [int] $RetryCount = 5,

    [ValidateRange(0, 30)]
    [int] $RetryDelaySeconds = 2,

    [switch] $PassThru
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

function Test-CatalogEntryContains {
    param(
        [Parameter(Mandatory = $true)] $Entry,
        [Parameter(Mandatory = $true)] [string] $Fragment
    )

    foreach ($propertyName in @('modelKey', 'path', 'indexedModelIdentifier', 'displayName')) {
        $property = $Entry.PSObject.Properties[$propertyName]
        if ($null -ne $property -and
            $null -ne $property.Value -and
            ([string] $property.Value).IndexOf($Fragment, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $true
        }
    }
    return $false
}

$lms = Assert-GoCommand -Name 'lms'
$required = @($RequiredNameFragments | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
$catalog = @()
$missing = $required

for ($attempt = 1; $attempt -le $RetryCount; $attempt++) {
    $catalogJson = & $lms.Source 'ls' '--json'
    if ($LASTEXITCODE -ne 0) {
        throw "LM Studio model catalog refresh failed with exit code $LASTEXITCODE."
    }

    try {
        $parsedCatalog = $catalogJson | ConvertFrom-Json
        if ($parsedCatalog -is [Array]) {
            $catalog = @($parsedCatalog | ForEach-Object { $_ })
        }
        elseif ($null -eq $parsedCatalog) {
            $catalog = @()
        }
        else {
            $catalog = @($parsedCatalog)
        }
    }
    catch {
        throw "LM Studio returned an invalid model catalog: $($_.Exception.Message)"
    }

    $missing = @($required | Where-Object {
        $fragment = $_
        -not ($catalog | Where-Object { Test-CatalogEntryContains -Entry $_ -Fragment $fragment })
    })
    if ($missing.Count -eq 0) {
        break
    }
    if ($attempt -lt $RetryCount -and $RetryDelaySeconds -gt 0) {
        Start-Sleep -Seconds $RetryDelaySeconds
    }
}

if ($missing.Count -ne 0) {
    throw "LM Studio did not index the required models: $($missing -join ', ')."
}

Write-Host ("LM Studio catalog refreshed: {0} model entries are visible in My Models." -f $catalog.Count) -ForegroundColor Green
if ($PassThru) {
    $catalog
}
