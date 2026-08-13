#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Folder', 'SingleFile')]
    [string] $Mode = 'SingleFile',

    [ValidateSet('win-x64')]
    [string] $RuntimeIdentifier = 'win-x64',

    [string] $OutputDirectory,

    [switch] $SkipSmoke
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$serverProject = Resolve-GoRepositoryPath -RelativePath 'src\GoAi.Server.App\GoAi.Server.App.csproj'
$gatewayProject = Resolve-GoRepositoryPath -RelativePath 'src\GoAi.Gateway\GoAi.Gateway.csproj'
$clientProject = Resolve-GoRepositoryPath -RelativePath 'src\GoAi.Client\GoAi.Client.csproj'
$smokeProject = Resolve-GoRepositoryPath -RelativePath 'src\GoAi.SmokeClient\GoAi.SmokeClient.csproj'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $suffix = if ($Mode -eq 'SingleFile') { 'portable' } else { 'folder' }
    $OutputDirectory = Resolve-GoRepositoryPath -RelativePath ("artifacts\go-ai-server\{0}\{1}" -f $suffix, $RuntimeIdentifier)
}

$OutputDirectory = Reset-GoArtifactDirectory -Path $OutputDirectory
$manifestPath = Assert-GoArtifactPath -Path ($OutputDirectory + '.manifest.json')
if (Test-Path -LiteralPath $manifestPath) {
    Remove-Item -LiteralPath $manifestPath -Force
}

$configuration = if ($Mode -eq 'SingleFile') { 'Portable' } else { 'Release' }
$singleFile = if ($Mode -eq 'SingleFile') { 'true' } else { 'false' }
$extractAll = if ($Mode -eq 'SingleFile') { 'true' } else { 'false' }

Invoke-GoDotNet -CommandArguments @(
    'restore', $serverProject,
    '--runtime', $RuntimeIdentifier,
    ("-p:Configuration={0}" -f $configuration),
    '-p:Platform=x64',
    '--nologo'
)
Invoke-GoDotNet -CommandArguments @(
    'publish', $serverProject,
    '--configuration', $configuration,
    '--runtime', $RuntimeIdentifier,
    '--self-contained', 'true',
    '--no-restore',
    '--output', $OutputDirectory,
    '-p:Platform=x64',
    '-p:WindowsAppSDKSelfContained=true',
    ("-p:PublishSingleFile={0}" -f $singleFile),
    ("-p:IncludeAllContentForSelfExtract={0}" -f $extractAll),
    '-p:PublishTrimmed=false',
    '-p:PublishReadyToRun=false',
    '--nologo'
)

$executable = Join-Path $OutputDirectory 'GO-AI-Server.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Published GO AI Server executable is missing: $executable"
}

$gatewayDirectory = Join-Path $OutputDirectory 'gateway'
New-Item -ItemType Directory -Path $gatewayDirectory -Force | Out-Null
Invoke-GoDotNet -CommandArguments @(
    'publish', $gatewayProject,
    '--configuration', 'Release',
    '--runtime', $RuntimeIdentifier,
    '--self-contained', 'true',
    '--output', $gatewayDirectory,
    '-p:PublishSingleFile=true',
    '-p:PublishTrimmed=false',
    '--nologo'
)
$gatewayExecutable = Join-Path $gatewayDirectory 'GoAi.Gateway.exe'
if (-not (Test-Path -LiteralPath $gatewayExecutable -PathType Leaf)) {
    throw "Published gateway executable is missing: $gatewayExecutable"
}

$nugetDirectory = Reset-GoArtifactDirectory -Path (Resolve-GoRepositoryPath -RelativePath 'artifacts\go-ai-server\nuget')
Invoke-GoDotNet -CommandArguments @(
    'pack', $clientProject,
    '--configuration', 'Release',
    '--output', $nugetDirectory,
    '--nologo'
)
$clientPackage = Get-ChildItem -LiteralPath $nugetDirectory -Filter 'GoAi.Client.*.nupkg' -File |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if ($null -eq $clientPackage) {
    throw 'GoAi.Client NuGet package was not created.'
}

$smokeDirectory = Reset-GoArtifactDirectory -Path (Resolve-GoRepositoryPath -RelativePath 'artifacts\go-ai-server\smoke-client\win-x64')
Invoke-GoDotNet -CommandArguments @(
    'publish', $smokeProject,
    '--configuration', 'Release',
    '--runtime', $RuntimeIdentifier,
    '--self-contained', 'true',
    '--output', $smokeDirectory,
    '-p:PublishSingleFile=true',
    '-p:PublishTrimmed=false',
    '--nologo'
)
$smokeExecutable = Join-Path $smokeDirectory 'GoAi.SmokeClient.exe'
if (-not (Test-Path -LiteralPath $smokeExecutable -PathType Leaf)) {
    throw "Published smoke client is missing: $smokeExecutable"
}
$bundledSmokeDirectory = Join-Path $OutputDirectory 'smoke-client\win-x64'
New-Item -ItemType Directory -Path $bundledSmokeDirectory -Force | Out-Null
Get-ChildItem -LiteralPath $smokeDirectory -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $bundledSmokeDirectory -Recurse -Force
}
$bundledClientDirectory = Join-Path $OutputDirectory 'client'
New-Item -ItemType Directory -Path $bundledClientDirectory -Force | Out-Null
Copy-Item -LiteralPath $clientPackage.FullName -Destination $bundledClientDirectory -Force

$apiDirectory = Join-Path $OutputDirectory 'openapi'
New-Item -ItemType Directory -Path $apiDirectory -Force | Out-Null
Copy-Item -LiteralPath (Resolve-GoRepositoryPath -RelativePath 'openapi\go-ai-v1.yaml') -Destination $apiDirectory -Force
Copy-Item -LiteralPath (Resolve-GoRepositoryPath -RelativePath 'openapi\run-events-v1.schema.json') -Destination $apiDirectory -Force
Copy-Item -LiteralPath (Resolve-GoRepositoryPath -RelativePath 'GO-AI-SERVER.md') -Destination $OutputDirectory -Force
$deploymentDirectory = Join-Path $OutputDirectory 'deploy\go-ai'
New-Item -ItemType Directory -Path $deploymentDirectory -Force | Out-Null
Get-ChildItem -LiteralPath (Resolve-GoRepositoryPath -RelativePath 'deploy\go-ai') -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $deploymentDirectory -Recurse -Force
}
$workerDirectory = Join-Path $OutputDirectory 'workers'
New-Item -ItemType Directory -Path $workerDirectory -Force | Out-Null
Copy-Item -LiteralPath (Resolve-GoRepositoryPath -RelativePath 'workers\.dockerignore') -Destination $workerDirectory
foreach ($workerName in @('common', 'media', 'speech', 'image')) {
    $source = Resolve-GoRepositoryPath -RelativePath ("workers\{0}" -f $workerName)
    $destination = Join-Path $workerDirectory $workerName
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem -LiteralPath $source -File -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
    }
}
$scriptDirectory = Join-Path $OutputDirectory 'scripts'
New-Item -ItemType Directory -Path $scriptDirectory -Force | Out-Null
foreach ($scriptName in @(
    'common.ps1', 'bootstrap-ai-server.ps1', 'configure-lmstudio.ps1',
    'start-lmstudio-server.ps1', 'refresh-lmstudio-model-catalog.ps1',
    'download-ai-models.ps1', 'build-ai-workers.ps1', 'validate-ai-server-compose.ps1',
    'deploy-ai-server.ps1', 'export-ai-client-bundle.ps1', 'live-smoke-ai-server.ps1'
)) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $scriptName) -Destination $scriptDirectory -Force
}

$manifest = [ordered]@{
    schema = 'go.ai.server.windows.publish.v1'
    mode = $Mode
    runtimeIdentifier = $RuntimeIdentifier
    selfContained = $true
    windowsAppSdkSelfContained = $true
    protocolVersion = '1.0'
    gatewayEndpoint = 'http://127.0.0.1:7080'
    publicEndpoint = 'https://192.168.0.67:8443'
    buildId = Get-GoBuildId
    builtAtUtc = Get-GoBuiltAt
    executableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
    gatewayExecutable = 'gateway/GoAi.Gateway.exe'
    gatewayExecutableSha256 = (Get-FileHash -LiteralPath $gatewayExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
    clientPackage = 'client/' + $clientPackage.Name
    clientPackageSha256 = (Get-FileHash -LiteralPath $clientPackage.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    smokeClient = 'smoke-client/win-x64/GoAi.SmokeClient.exe'
    smokeClientSha256 = (Get-FileHash -LiteralPath (Join-Path $bundledSmokeDirectory 'GoAi.SmokeClient.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    openApiSha256 = (Get-FileHash -LiteralPath (Join-Path $apiDirectory 'go-ai-v1.yaml') -Algorithm SHA256).Hash.ToLowerInvariant()
    eventSchemaSha256 = (Get-FileHash -LiteralPath (Join-Path $apiDirectory 'run-events-v1.schema.json') -Algorithm SHA256).Hash.ToLowerInvariant()
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

if (-not $SkipSmoke) {
    & (Join-Path $PSScriptRoot 'smoke-ai-server.ps1') `
        -PublishDirectory $OutputDirectory `
        -ManifestPath $manifestPath `
        -Mode $Mode
}

Write-Host "GO AI Server publish artifact: $OutputDirectory" -ForegroundColor Green
