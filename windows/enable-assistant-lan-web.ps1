#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateRange(1024, 65535)][int] $Port = 0,
    [switch] $ShowAccess
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$profileId = 'stable'
if ($Port -eq 0) { $Port = 8090 }
$ruleName = "Local Assistant LAN Web ($profileId)"

if (-not $ShowAccess) {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Die Firewallfreigabe muss einmalig in einer als Administrator gestarteten PowerShell ausgeführt werden.'
    }
    Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol TCP `
        -LocalPort $Port -Profile Private -RemoteAddress LocalSubnet | Out-Null
}

$localData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$dataRoot = Join-Path $localData 'GO'
$accessFile = Join-Path $dataRoot 'LanWeb\access.json'
if (Test-Path -LiteralPath $accessFile -PathType Leaf) {
    Get-Content -LiteralPath $accessFile -Raw | ConvertFrom-Json
} else {
    [pscustomobject]@{
        Profile = $profileId
        Port = $Port
        Status = 'Die Zugriffs-URL wird erzeugt, sobald die AI-Assistent-Seite in der Desktop-App geöffnet wurde.'
        AccessFile = $accessFile
    }
}
