#requires -Version 5.1
#requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PortableSource,

    [string] $DataRoot = 'C:\ProgramData\GO-AI-Server',

    [string] $InstallRoot = 'C:\Program Files\GO-AI-Server',

    [string] $ExpectedLanIp = '192.168.0.67',

    [string] $ImageVersion = '1.0.0',

    [string] $LogPath = 'C:\ProgramData\GO-AI-Server\Logs\deployment.log',

    [string] $ResultPath = 'C:\ProgramData\GO-AI-Server\Logs\deployment-result.json'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$logDirectory = Split-Path $LogPath -Parent
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
Start-Transcript -LiteralPath $LogPath -Force | Out-Null
$exitCode = 1
try {
    & (Join-Path $PSScriptRoot 'deploy-ai-server.ps1') `
        -DataRoot $DataRoot `
        -InstallRoot $InstallRoot `
        -PortableSource $PortableSource `
        -ExpectedLanIp $ExpectedLanIp `
        -ImageVersion $ImageVersion `
        -SkipBuild `
        -SkipDockerBuild
    $exitCode = 0
    [ordered]@{
        succeeded = $true
        completedAtUtc = [DateTime]::UtcNow.ToString('o')
        errorType = $null
        message = 'GO AI Server deployment completed.'
    } | ConvertTo-Json | Set-Content -LiteralPath $ResultPath -Encoding utf8
}
catch {
    [ordered]@{
        succeeded = $false
        completedAtUtc = [DateTime]::UtcNow.ToString('o')
        errorType = $_.Exception.GetType().FullName
        message = $_.Exception.Message
        scriptStackTrace = $_.ScriptStackTrace
    } | ConvertTo-Json | Set-Content -LiteralPath $ResultPath -Encoding utf8
    Write-Error -ErrorRecord $_
}
finally {
    Stop-Transcript | Out-Null
}

exit $exitCode
