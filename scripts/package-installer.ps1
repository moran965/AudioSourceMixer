param([ValidateSet('Debug','Release')][string] $Configuration = 'Release')
. (Join-Path $PSScriptRoot 'common.ps1')
$root = Get-RepositoryRoot
$dotnet = Get-DotnetExecutable
$version = Get-ProductVersion
$artifacts = Join-Path $root 'artifacts'
$staging = Join-Path $artifacts "staging\$version"
$portable = Join-Path $artifacts "portable\AudioSourceMixer-$version"
$payload = Join-Path $staging 'installer-payload.zip'
$installerPublish = Join-Path $staging 'installer-publish'
$installer = Join-Path $artifacts "AudioSourceMixer-$version-win-x64-setup.exe"
Assert-PathInsideRepository $staging

# Always rebuild the payload from current source; an existing executable is not a freshness guarantee.
& (Join-Path $PSScriptRoot 'package-portable.ps1') -Configuration $Configuration
if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath $payload -Force }
if (Test-Path -LiteralPath $installerPublish) { Remove-Item -LiteralPath $installerPublish -Recurse -Force }
if (Test-Path -LiteralPath $installer) { Remove-Item -LiteralPath $installer -Force }
New-Item -ItemType Directory -Path $staging,$installerPublish -Force | Out-Null
Compress-Archive -Path (Join-Path $portable '*') -DestinationPath $payload -CompressionLevel Optimal
Push-Location $root
try {
    Invoke-Checked { & $dotnet publish '.\src\AudioSourceMixer.Installer\AudioSourceMixer.Installer.csproj' -c $Configuration -r win-x64 --self-contained true -o $installerPublish -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None "-p:InstallerPayloadPath=$payload" } 'Installer publish'
    Copy-Item -LiteralPath (Join-Path $installerPublish 'AudioSourceMixer-Setup-x64.exe') -Destination $installer -Force
    Write-Output "Installer: $installer"
} finally {
    Pop-Location
    if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath $payload -Force }
}
