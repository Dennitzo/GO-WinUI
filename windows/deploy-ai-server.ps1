#requires -Version 5.1
#requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string] $DataRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'GO-AI-Server'),

    [string] $InstallRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) 'GO-AI-Server'),

    [string] $PortableSource,

    [string] $ExpectedLanIp = '192.168.0.67',

    [string] $ImageVersion = '1.0.0',

    [switch] $SkipBuild,

    [switch] $SkipDockerBuild,

    [switch] $SkipLmStudioConfiguration,

    [switch] $AllowUnauthenticatedLmStudio
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

function Invoke-Sc {
    param([Parameter(Mandatory = $true)] [string[]] $Arguments)
    & "$env:SystemRoot\System32\sc.exe" @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe failed with exit code $LASTEXITCODE."
    }
}

function Test-ServiceExists {
    param([string] $Name)
    return $null -ne (Get-Service -Name $Name -ErrorAction SilentlyContinue)
}

$DataRoot = [IO.Path]::GetFullPath($DataRoot)
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$programFiles = [IO.Path]::GetFullPath([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)).TrimEnd('\') + '\'
if (-not $InstallRoot.StartsWith($programFiles, [StringComparison]::OrdinalIgnoreCase) -or $InstallRoot.TrimEnd('\') -eq $programFiles.TrimEnd('\')) {
    throw "InstallRoot must be a dedicated child of Program Files: $InstallRoot"
}
if ([string]::Equals($DataRoot, [IO.Path]::GetPathRoot($DataRoot), [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe data root: $DataRoot"
}
if ([string]::IsNullOrWhiteSpace($PortableSource)) {
    $packageRoot = Get-GoRepositoryRoot
    $PortableSource = if (Test-Path -LiteralPath (Join-Path $packageRoot 'GO-AI-Server.exe') -PathType Leaf) {
        $packageRoot
    }
    else {
        Resolve-GoRepositoryPath -RelativePath 'artifacts\go-ai-server\portable\win-x64'
    }
}
$PortableSource = [IO.Path]::GetFullPath($PortableSource)

& (Join-Path $PSScriptRoot 'bootstrap-ai-server.ps1') -DataRoot $DataRoot -ExpectedLanIp $ExpectedLanIp
if (-not $SkipLmStudioConfiguration) {
    $configurationArguments = @{
        DataRoot = $DataRoot
    }
    if (-not $AllowUnauthenticatedLmStudio) {
        $configurationArguments.RequireAuthentication = $true
    }
    & (Join-Path $PSScriptRoot 'configure-lmstudio.ps1') @configurationArguments
}
if (-not $SkipBuild) {
    $buildScript = Join-Path $PSScriptRoot 'build-ai-server.ps1'
    if (Test-Path -LiteralPath $buildScript -PathType Leaf) {
        & $buildScript -Configuration Release -SkipDocker:$SkipDockerBuild
    }
    elseif (-not (Test-Path -LiteralPath (Join-Path $PortableSource 'GO-AI-Server.exe') -PathType Leaf)) {
        throw 'The repository build script and a prebuilt portable server are both missing.'
    }
}
if ((-not (Test-Path -LiteralPath (Join-Path $PortableSource 'GO-AI-Server.exe') -PathType Leaf)) -or
    (-not (Test-Path -LiteralPath (Join-Path $PortableSource 'gateway\GoAi.Gateway.exe') -PathType Leaf))) {
    throw "Portable GO AI Server artifact is incomplete: $PortableSource"
}

$serviceName = 'GO-AI-Server'
if (Test-ServiceExists $serviceName) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    $service = Get-Service -Name $serviceName
    $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
}

$stagingRoot = $InstallRoot + '.staging-' + [Guid]::NewGuid().ToString('N')
New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
try {
    Get-ChildItem -LiteralPath $PortableSource -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $stagingRoot -Recurse -Force
    }
    if (Test-Path -LiteralPath $InstallRoot) {
        $backupDirectory = Join-Path $DataRoot ('Backups\application-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
        New-Item -ItemType Directory -Path (Split-Path $backupDirectory -Parent) -Force | Out-Null
        Move-Item -LiteralPath $InstallRoot -Destination $backupDirectory
    }
    Move-Item -LiteralPath $stagingRoot -Destination $InstallRoot
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}

$gatewayExecutable = Join-Path $InstallRoot 'gateway\GoAi.Gateway.exe'
$serviceEnvironment = @(
    "GO_AI_DATA_DIRECTORY=$DataRoot",
    "GO_AI_EXPECTED_LAN_IP=$ExpectedLanIp",
    "GO_AI_PUBLIC_URL=https://${ExpectedLanIp}:8443"
)
if ($AllowUnauthenticatedLmStudio) {
    $serviceEnvironment += 'GO_AI_ALLOW_UNAUTHENTICATED_LM_STUDIO=1'
}
$serviceEnvironmentRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
if (Test-ServiceExists $serviceName) {
    Invoke-Sc @('config', $serviceName, 'binPath=', ('"{0}"' -f $gatewayExecutable), 'start=', 'delayed-auto', 'obj=', ('NT SERVICE\{0}' -f $serviceName))
}
else {
    Invoke-Sc @('create', $serviceName, 'binPath=', ('"{0}"' -f $gatewayExecutable), 'start=', 'delayed-auto', 'obj=', ('NT SERVICE\{0}' -f $serviceName), 'DisplayName=', 'GO AI Server Gateway')
}
New-ItemProperty -Path $serviceEnvironmentRegistryPath -Name Environment -PropertyType MultiString -Value $serviceEnvironment -Force | Out-Null
Invoke-Sc @('description', $serviceName, 'Loopbackgebundenes Gateway für GO-WinUI AI-Dienste')
Invoke-Sc @('sidtype', $serviceName, 'unrestricted')
Invoke-Sc @('failure', $serviceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/15000/restart/60000')

$currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$serviceIdentity = "NT SERVICE\$serviceName"
& icacls.exe $DataRoot /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' ("*{0}:(OI)(CI)F" -f $currentSid) ("{0}:(RX)" -f $serviceIdentity) | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to apply the GO AI Server root ACL.'
}
foreach ($relativeDirectory in @('Data', 'Uploads', 'Artifacts', 'Logs', 'Secrets')) {
    $mutableDirectory = Join-Path $DataRoot $relativeDirectory
    New-Item -ItemType Directory -Path $mutableDirectory -Force | Out-Null
    & icacls.exe $mutableDirectory /grant:r ("{0}:(OI)(CI)M" -f $serviceIdentity) | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to grant the gateway service access to $mutableDirectory."
    }
}
& icacls.exe $InstallRoot /grant:r ("{0}:(OI)(CI)RX" -f $serviceIdentity) | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to grant the gateway service read access to its installed binaries.'
}

$caddyAcl = Get-Acl -LiteralPath (Join-Path $DataRoot 'Caddy')
$unsafeCaddyRules = @($caddyAcl.Access | Where-Object {
    $_.IdentityReference.Value -eq $serviceIdentity -and
    ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Write) -ne 0
})
if ($unsafeCaddyRules.Count -ne 0) {
    throw 'The gateway service must not have write access to the Caddy CA directory.'
}

$lmStudioStartupScript = Join-Path $InstallRoot 'scripts\start-lmstudio-server.ps1'
if (-not (Test-Path -LiteralPath $lmStudioStartupScript -PathType Leaf)) {
    throw "LM Studio startup script is missing: $lmStudioStartupScript"
}
$taskName = 'GO AI LM Studio'
$taskUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$powerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$taskArguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -DataRoot "{1}"' -f $lmStudioStartupScript, $DataRoot
$taskAction = New-ScheduledTaskAction -Execute $powerShell -Argument $taskArguments -WorkingDirectory (Split-Path $lmStudioStartupScript -Parent)
$taskTrigger = New-ScheduledTaskTrigger -AtLogOn -User $taskUser
$taskTrigger.Delay = 'PT15S'
$taskPrincipal = New-ScheduledTaskPrincipal -UserId $taskUser -LogonType Interactive -RunLevel Limited
$taskSettings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 10) `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask `
    -TaskName $taskName `
    -Action $taskAction `
    -Trigger $taskTrigger `
    -Principal $taskPrincipal `
    -Settings $taskSettings `
    -Description 'Starts and validates the authenticated LM Studio loopback server for GO AI after AMD signs in.' `
    -Force | Out-Null

$address = Get-NetIPAddress -AddressFamily IPv4 -IPAddress $ExpectedLanIp -ErrorAction Stop | Select-Object -First 1
$profile = Get-NetConnectionProfile -InterfaceIndex $address.InterfaceIndex -ErrorAction Stop
if ($profile.NetworkCategory -ne 'Private') {
    Set-NetConnectionProfile -InterfaceIndex $address.InterfaceIndex -NetworkCategory Private
}
$firewallName = 'GO AI Server HTTPS 8443 (Private LAN)'
$firewall = Get-NetFirewallRule -DisplayName $firewallName -ErrorAction SilentlyContinue
if ($null -eq $firewall) {
    New-NetFirewallRule -DisplayName $firewallName -Direction Inbound -Action Allow -Protocol TCP -LocalPort 8443 -Profile Private -RemoteAddress LocalSubnet | Out-Null
}
else {
    Set-NetFirewallRule -DisplayName $firewallName -Enabled True -Direction Inbound -Action Allow -Profile Private | Out-Null
    $firewall | Get-NetFirewallPortFilter | Set-NetFirewallPortFilter -Protocol TCP -LocalPort 8443 | Out-Null
    $firewall | Get-NetFirewallAddressFilter | Set-NetFirewallAddressFilter -RemoteAddress LocalSubnet | Out-Null
}

$composePath = Join-Path $InstallRoot 'deploy\go-ai\compose.yaml'
$composeEnvironment = Join-Path $DataRoot 'Config\compose.env'
$caddyFile = Join-Path $InstallRoot 'deploy\go-ai\caddy\Caddyfile'
$caddyText = Get-Content -LiteralPath $caddyFile -Raw -Encoding utf8
$caddyText = [regex]::Replace($caddyText, 'https://(?:\d{1,3}\.){3}\d{1,3}:8443', "https://${ExpectedLanIp}:8443")
$caddyText = [regex]::Replace(
    $caddyText,
    '(?m)^(\s*default_sni\s+)(?:\d{1,3}\.){3}\d{1,3}\s*$',
    ('${1}' + $ExpectedLanIp))
$caddyText | Set-Content -LiteralPath $caddyFile -Encoding utf8
$docker = Resolve-GoDockerCommand
$previousDataRoot = $env:GO_AI_DATA_ROOT
$previousImageVersion = $env:GO_AI_IMAGE_VERSION
$previousExpectedLanIp = $env:GO_AI_EXPECTED_LAN_IP
try {
    $env:GO_AI_DATA_ROOT = $DataRoot.Replace('\', '/')
    $env:GO_AI_IMAGE_VERSION = $ImageVersion
    $env:GO_AI_EXPECTED_LAN_IP = $ExpectedLanIp
    & $docker compose --env-file $composeEnvironment --file $composePath up -d --remove-orphans
    if ($LASTEXITCODE -ne 0) {
        throw "Docker Compose deployment failed with exit code $LASTEXITCODE."
    }
    & $docker compose --env-file $composeEnvironment --file $composePath restart caddy
    if ($LASTEXITCODE -ne 0) {
        throw "Caddy configuration reload failed with exit code $LASTEXITCODE."
    }
}
finally {
    $env:GO_AI_DATA_ROOT = $previousDataRoot
    $env:GO_AI_IMAGE_VERSION = $previousImageVersion
    $env:GO_AI_EXPECTED_LAN_IP = $previousExpectedLanIp
}

Start-Service -Name $serviceName
$gatewayDeadline = [DateTime]::UtcNow.AddSeconds(60)
$live = $null
do {
    try {
        $live = Invoke-RestMethod -Method Get -Uri 'http://127.0.0.1:7080/v1/health/live' -TimeoutSec 3
        if ($live.status -eq 'live') { break }
    }
    catch {
        Start-Sleep -Seconds 1
    }
} while ([DateTime]::UtcNow -lt $gatewayDeadline)
if ($null -eq $live -or $live.status -ne 'live') {
    throw 'GO AI Gateway did not become live after service start.'
}

$rootCertificate = Join-Path $DataRoot 'Caddy\data\caddy\pki\authorities\local\root.crt'
$certificateDeadline = [DateTime]::UtcNow.AddSeconds(60)
while (-not (Test-Path -LiteralPath $rootCertificate -PathType Leaf) -and [DateTime]::UtcNow -lt $certificateDeadline) {
    Start-Sleep -Seconds 1
}
if (-not (Test-Path -LiteralPath $rootCertificate -PathType Leaf)) {
    throw 'Caddy did not create its local root certificate.'
}
$certificate = Import-Certificate -FilePath $rootCertificate -CertStoreLocation 'Cert:\LocalMachine\Root'
Write-Host "Trusted Caddy root certificate: $($certificate.Thumbprint)" -ForegroundColor DarkGray

$shortcutPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonDesktopDirectory)) 'GO AI Server.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $InstallRoot 'GO-AI-Server.exe'
$shortcut.Arguments = '--dashboard-only'
$shortcut.WorkingDirectory = $InstallRoot
$shortcut.IconLocation = (Join-Path $InstallRoot 'GO-AI-Server.exe') + ',0'
$shortcut.Save()

$listeners = Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue
$unexpected = @($listeners | Where-Object {
    $_.LocalPort -in @(1234, 7080, 7081, 7082, 7083, 7084) -and $_.LocalAddress -notin @('127.0.0.1', '::1')
})
if ($unexpected.Count -ne 0) {
    throw 'A GO AI internal port is bound beyond loopback.'
}

Write-Host "GO AI Server deployed. Public endpoint: https://${ExpectedLanIp}:8443" -ForegroundColor Green
