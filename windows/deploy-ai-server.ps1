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
$modelRoot = [IO.Path]::GetFullPath((Join-Path $DataRoot 'Models'))
$safeModelRoot = $modelRoot.TrimEnd('\') + '\'
foreach ($obsoleteModelName in @(
    'piper',
    'qwen3-tts-12hz-1.7b-voicedesign',
    'Qwen3-TTS-12Hz-0.6B-Base',
    'chatterbox-multilingual-v3',
    'whisperx-align-de',
    'faster-whisper-large-v3-turbo',
    'whisper-large-v3-turbo'
)) {
    $obsoleteModelRoot = [IO.Path]::GetFullPath((Join-Path $modelRoot $obsoleteModelName))
    if ($obsoleteModelRoot.StartsWith($safeModelRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($obsoleteModelRoot) -ceq $obsoleteModelName -and
        (Test-Path -LiteralPath $obsoleteModelRoot -PathType Container)) {
        Remove-Item -LiteralPath $obsoleteModelRoot -Recurse -Force
        Write-Host "Removed obsolete speech model directory: $obsoleteModelRoot" -ForegroundColor DarkGray
    }
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
    $service.Dispose()
    Invoke-Sc @('delete', $serviceName)
    $deleteDeadline = [DateTime]::UtcNow.AddSeconds(30)
    while ((Test-ServiceExists $serviceName) -and [DateTime]::UtcNow -lt $deleteDeadline) {
        Start-Sleep -Milliseconds 250
    }
    if (Test-ServiceExists $serviceName) {
        throw 'The legacy GO AI Server Windows service could not be removed.'
    }
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
if (-not (Test-Path -LiteralPath $gatewayExecutable -PathType Leaf)) {
    throw "Diagnostic gateway executable is missing: $gatewayExecutable"
}

$currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
& icacls.exe $DataRoot /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' ("*{0}:(OI)(CI)F" -f $currentSid) | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to apply the GO AI Server root ACL.'
}
& icacls.exe $InstallRoot /grant:r ("*{0}:(OI)(CI)RX" -f $currentSid) | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to grant the interactive server app read access to its installed binaries.'
}

$lmStudioStartupScript = Join-Path $InstallRoot 'scripts\start-lmstudio-server.ps1'
if (-not (Test-Path -LiteralPath $lmStudioStartupScript -PathType Leaf)) {
    throw "LM Studio startup script is missing: $lmStudioStartupScript"
}
$taskName = 'GO AI LM Studio'
if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}

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

$lmStudioFirewallName = 'LM Studio API 1234 (Private LAN)'
$lmStudioFirewall = Get-NetFirewallRule -DisplayName $lmStudioFirewallName -ErrorAction SilentlyContinue
if ($null -eq $lmStudioFirewall) {
    New-NetFirewallRule -DisplayName $lmStudioFirewallName -Direction Inbound -Action Allow -Protocol TCP -LocalPort 1234 -Profile Private -RemoteAddress LocalSubnet | Out-Null
}
else {
    Set-NetFirewallRule -DisplayName $lmStudioFirewallName -Enabled True -Direction Inbound -Action Allow -Profile Private | Out-Null
    $lmStudioFirewall | Get-NetFirewallPortFilter | Set-NetFirewallPortFilter -Protocol TCP -LocalPort 1234 | Out-Null
    $lmStudioFirewall | Get-NetFirewallAddressFilter | Set-NetFirewallAddressFilter -RemoteAddress LocalSubnet | Out-Null
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

$shell = New-Object -ComObject WScript.Shell
$shortcutPaths = @(
    (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonDesktopDirectory)) 'GO AI Server.lnk'),
    (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonStartup)) 'GO AI Server.lnk')
)
foreach ($shortcutPath in $shortcutPaths) {
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = Join-Path $InstallRoot 'GO-AI-Server.exe'
    $shortcut.Arguments = ''
    $shortcut.WorkingDirectory = $InstallRoot
    $shortcut.IconLocation = (Join-Path $InstallRoot 'GO-AI-Server.exe') + ',0'
    $shortcut.Save()
}

$listeners = Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue
$unexpected = @($listeners | Where-Object {
    $_.LocalPort -in @(7080, 7081, 7082, 7083, 7084, 7085, 7086) -and $_.LocalAddress -notin @('127.0.0.1', '::1')
})
if ($unexpected.Count -ne 0) {
    throw 'A GO AI internal port is bound beyond loopback.'
}

& $docker compose --env-file $composeEnvironment --file $composePath stop --timeout 20
if ($LASTEXITCODE -ne 0) {
    throw "Docker Compose shutdown after deployment failed with exit code $LASTEXITCODE."
}
$lms = Get-Command 'lms' -ErrorAction SilentlyContinue
if ($null -ne $lms) {
    Write-Host 'LM Studio bleibt für andere Anwendungen aktiv; die Server-App entlädt nur Modellinstanzen.'
}

Write-Host "GO AI Server deployed. Start GO-AI-Server.exe to activate Gateway, LM Studio and Docker services: https://${ExpectedLanIp}:8443" -ForegroundColor Green
