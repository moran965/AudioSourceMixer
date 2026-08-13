param([ValidateSet('Debug','Release')][string] $Configuration = 'Release')
. (Join-Path $PSScriptRoot 'common.ps1')
. (Join-Path $PSScriptRoot 'runtime-payload.ps1')
$root = Get-RepositoryRoot
$dotnet = Get-DotnetExecutable
$version = Get-ProductVersion
$artifacts = Join-Path $root 'artifacts'
$staging = Join-Path $artifacts "staging\$version"
$portable = Join-Path $artifacts "portable\AudioSourceMixer-$version"
$payloadDirectory = Join-Path $staging 'installer-payload'
$payload = Join-Path $staging 'installer-payload.zip'
$installerPublish = Join-Path $staging 'installer-publish'
$installer = Join-Path $artifacts "AudioSourceMixer-$version-win-x64-setup.exe"
Assert-PathInsideRepository $staging

& (Join-Path $PSScriptRoot 'package-portable.ps1') -Configuration $Configuration
foreach ($path in @($payloadDirectory,$installerPublish)) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}
foreach ($path in @($payload,$installer)) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
}
New-Item -ItemType Directory -Path $staging,$payloadDirectory,$installerPublish -Force | Out-Null
foreach ($relative in Get-ExpectedPayloadPaths 'InstallerPayload') {
    $source = Join-Path $portable $relative
    $destination = Join-Path $payloadDirectory $relative
    New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination -Force
}
$null = Assert-RuntimePayload $payloadDirectory 'InstallerPayload'
Compress-Archive -Path (Join-Path $payloadDirectory '*') -DestinationPath $payload -CompressionLevel Optimal
Push-Location $root
try {
    Invoke-Checked { & $dotnet publish '.\src\AudioSourceMixer.Installer\AudioSourceMixer.Installer.csproj' -c $Configuration -r win-x64 --self-contained true -o $installerPublish -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None "-p:InstallerPayloadPath=$payload" } 'Installer publish'
    Copy-Item -LiteralPath (Join-Path $installerPublish 'AudioSourceMixer-Setup-x64.exe') -Destination $installer -Force
    Write-Output "Installer: $installer"
} finally {
    Pop-Location
    if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath $payload -Force }
}
