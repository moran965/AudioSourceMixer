param(
    [ValidateSet('Debug','Release')][string] $Configuration = 'Release',
    [string] $DesktopPublishPath = '',
    [string] $NativeHostPublishPath = ''
)
. (Join-Path $PSScriptRoot 'common.ps1')
. (Join-Path $PSScriptRoot 'runtime-payload.ps1')
$root = Get-RepositoryRoot
$dotnet = Get-DotnetExecutable
$version = Get-ProductVersion
$artifacts = Join-Path $root 'artifacts'
$staging = Join-Path $artifacts "staging\$version"
$publish = Join-Path $staging 'runtime-publish'
$externalPublish = -not [string]::IsNullOrWhiteSpace($DesktopPublishPath) -or -not [string]::IsNullOrWhiteSpace($NativeHostPublishPath)
if ($externalPublish -and ([string]::IsNullOrWhiteSpace($DesktopPublishPath) -or [string]::IsNullOrWhiteSpace($NativeHostPublishPath))) {
    throw 'DesktopPublishPath and NativeHostPublishPath must be supplied together.'
}
$desktopPublish = if ($externalPublish) { [IO.Path]::GetFullPath($DesktopPublishPath) } else { Join-Path $publish 'desktop' }
$nativeHostPublish = if ($externalPublish) { [IO.Path]::GetFullPath($NativeHostPublishPath) } else { Join-Path $publish 'native-host' }
$payloadDirectory = Join-Path $staging 'installer-runtime-payload'
$payloadArchive = Join-Path $staging 'installer-payload.zip'
$installerPublish = Join-Path $staging 'installer-publish'
$installer = Join-Path $artifacts "AudioSourceMixer-$version-win-x64-setup.exe"
$manifestPath = Join-Path $artifacts "AudioSourceMixer-$version-build-manifest.json"
Assert-PathInsideRepository $staging
Assert-PathInsideRepository $installer
Assert-PathInsideRepository $manifestPath

if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
foreach ($path in @($installer,$manifestPath)) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
}
$directories = @($payloadDirectory,$installerPublish)
if (-not $externalPublish) { $directories += @($desktopPublish,$nativeHostPublish) }
New-Item -ItemType Directory -Path $directories -Force | Out-Null

function Copy-RuntimeFile([string] $RelativePath) {
    $normalized = $RelativePath.Replace('/', '\')
    $source = if ($normalized.StartsWith('BrowserExtension\', [StringComparison]::OrdinalIgnoreCase)) {
        Join-Path $root ('src\AudioSourceMixer.BrowserExtension\' + $normalized.Substring('BrowserExtension\'.Length))
    } else { Join-Path $root $normalized }
    $destination = Join-Path $payloadDirectory $normalized
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Allowlisted source file is missing: $source" }
    New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination -Force
}

Push-Location $root
try {
    if (-not $externalPublish) {
        Invoke-Checked { & $dotnet publish '.\src\AudioSourceMixer.Desktop\AudioSourceMixer.Desktop.csproj' -c $Configuration -r win-x64 --self-contained true -o $desktopPublish -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None } 'Desktop runtime publish'
        Invoke-Checked { & $dotnet publish '.\src\AudioSourceMixer.NativeHost\AudioSourceMixer.NativeHost.csproj' -c $Configuration -r win-x64 --self-contained true -o $nativeHostPublish -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None } 'Native host runtime publish'
    }

    foreach ($requiredPublish in @((Join-Path $desktopPublish 'AudioSourceMixer.exe'),(Join-Path $nativeHostPublish 'AudioSourceMixer.NativeHost.exe'))) {
        if (-not (Test-Path -LiteralPath $requiredPublish -PathType Leaf)) { throw "Required runtime publish is missing: $requiredPublish" }
    }

    Copy-Item -LiteralPath (Join-Path $desktopPublish 'AudioSourceMixer.exe') -Destination $payloadDirectory -Force
    Copy-Item -LiteralPath (Join-Path $nativeHostPublish 'AudioSourceMixer.NativeHost.exe') -Destination $payloadDirectory -Force
    foreach ($relative in Get-ExpectedPayloadPaths 'InstallerPayload') {
        if ($relative -in @('AudioSourceMixer.exe','AudioSourceMixer.NativeHost.exe')) { continue }
        Copy-RuntimeFile ([string]$relative)
    }

    $desktopPublishExe = Join-Path $desktopPublish 'AudioSourceMixer.exe'
    $nativeHostPublishExe = Join-Path $nativeHostPublish 'AudioSourceMixer.NativeHost.exe'
    $payloadExe = Join-Path $payloadDirectory 'AudioSourceMixer.exe'
    $publishHash = Get-Sha256 $desktopPublishExe
    $payloadHash = Get-Sha256 $payloadExe
    if ($publishHash -ne $payloadHash) { throw "Published and installer-payload desktop executables differ: $publishHash != $payloadHash" }
    $inventory = @(Assert-RuntimePayload $payloadDirectory 'InstallerPayload')
    Invoke-UiSmokeTest $payloadExe 'Installer runtime payload UI smoke test'

    $manifest = [ordered]@{
        schemaVersion = 3
        version = $version
        configuration = $Configuration
        externalSignedPublish = $externalPublish
        createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
        runtimePublish = [ordered]@{
            desktop = [ordered]@{ path = 'staging/runtime-publish/desktop/AudioSourceMixer.exe'; size = (Get-Item $desktopPublishExe).Length; sha256 = $publishHash }
            nativeHost = [ordered]@{ path = 'staging/runtime-publish/native-host/AudioSourceMixer.NativeHost.exe'; size = (Get-Item $nativeHostPublishExe).Length; sha256 = (Get-Sha256 $nativeHostPublishExe) }
        }
        installerPayload = [ordered]@{ path = 'staging/installer-runtime-payload'; executableSha256 = $payloadHash; files = $inventory }
    }

    Compress-Archive -Path (Join-Path $payloadDirectory '*') -DestinationPath $payloadArchive -CompressionLevel Optimal
    Invoke-Checked { & $dotnet publish '.\src\AudioSourceMixer.Installer\AudioSourceMixer.Installer.csproj' -c $Configuration -r win-x64 --self-contained true -o $installerPublish -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None "-p:InstallerPayloadPath=$payloadArchive" } 'Installer publish'
    Copy-Item -LiteralPath (Join-Path $installerPublish 'AudioSourceMixer-Setup-x64.exe') -Destination $installer -Force
    $manifest.installer = [ordered]@{ path = "AudioSourceMixer-$version-win-x64-setup.exe"; size = (Get-Item $installer).Length; sha256 = (Get-Sha256 $installer) }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    Write-Output "Runtime publish executable SHA-256: $publishHash"
    Write-Output "Installer payload executable SHA-256: $payloadHash"
    Write-Output "Installer: $installer"
    Write-Output "Build manifest: $manifestPath"
} finally {
    Pop-Location
    if (Test-Path -LiteralPath $payloadArchive) { Remove-Item -LiteralPath $payloadArchive -Force }
}
