param([ValidateSet('Debug','Release')][string] $Configuration = 'Release')
. (Join-Path $PSScriptRoot 'common.ps1')
$root = Get-RepositoryRoot
$dotnet = Get-DotnetExecutable
$version = Get-ProductVersion
$artifacts = Join-Path $root 'artifacts'
$staging = Join-Path $artifacts "staging\$version"
$publish = Join-Path $staging 'publish'
$portable = Join-Path $artifacts "portable\AudioSourceMixer-$version"
$zip = Join-Path $artifacts "AudioSourceMixer-$version-win-x64-portable.zip"
$manifestPath = Join-Path $artifacts "AudioSourceMixer-$version-build-manifest.json"
Assert-PathInsideRepository $staging
Assert-PathInsideRepository $portable
if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
if (Test-Path -LiteralPath $portable) { Remove-Item -LiteralPath $portable -Recurse -Force }
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
if (Test-Path -LiteralPath $manifestPath) { Remove-Item -LiteralPath $manifestPath -Force }
New-Item -ItemType Directory -Path $publish,$portable -Force | Out-Null

Push-Location $root
try {
    Invoke-Checked { & $dotnet publish '.\src\AudioSourceMixer.Desktop\AudioSourceMixer.Desktop.csproj' -c $Configuration -r win-x64 --self-contained true -o (Join-Path $publish 'desktop') -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None } 'Desktop publish'
    Invoke-Checked { & $dotnet publish '.\src\AudioSourceMixer.NativeHost\AudioSourceMixer.NativeHost.csproj' -c $Configuration -r win-x64 --self-contained true -o (Join-Path $publish 'native-host') -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None } 'Native host publish'
    Copy-Item -Path (Join-Path $publish 'desktop\*') -Destination $portable -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $publish 'native-host\AudioSourceMixer.NativeHost.exe') -Destination $portable -Force
    Copy-Item -LiteralPath '.\src\AudioSourceMixer.BrowserExtension' -Destination (Join-Path $portable 'BrowserExtension') -Recurse -Force
    Copy-Item -LiteralPath '.\docs' -Destination (Join-Path $portable 'docs') -Recurse -Force
    New-Item -ItemType Directory -Path (Join-Path $portable 'scripts') -Force | Out-Null
    Copy-Item -LiteralPath '.\scripts\register-native-host.ps1','.\scripts\unregister-native-host.ps1' -Destination (Join-Path $portable 'scripts') -Force
    Copy-Item -LiteralPath '.\README.md','.\LICENSE','.\THIRD_PARTY_NOTICES.md' -Destination $portable -Force

    $publishExe = Join-Path $publish 'desktop\AudioSourceMixer.exe'
    $portableExe = Join-Path $portable 'AudioSourceMixer.exe'
    foreach ($obsoleteHelper in @((Join-Path $publish 'desktop\ProcessBoostHost.exe'), (Join-Path $portable 'ProcessBoostHost.exe'))) {
        if (Test-Path -LiteralPath $obsoleteHelper) { throw "Obsolete ordinary-session boost helper entered the payload: $obsoleteHelper" }
    }
    $publishHash = Get-Sha256 $publishExe
    $portableHash = Get-Sha256 $portableExe
    if ($publishHash -ne $portableHash) { throw "Published and portable desktop executables differ: $publishHash != $portableHash" }
    $sourceExtensionDirectory = [System.IO.Path]::GetFullPath('.\src\AudioSourceMixer.BrowserExtension')
    $portableExtensionDirectory = Join-Path $portable 'BrowserExtension'
    $sourceExtensionPrefix = $sourceExtensionDirectory.TrimEnd('\') + '\'
    $extensionFiles = @(Get-ChildItem -LiteralPath $sourceExtensionDirectory -File -Recurse | ForEach-Object {
        $_.FullName.Substring($sourceExtensionPrefix.Length)
    } | Sort-Object)
    $extensionHashes = [ordered]@{}
    foreach ($relative in $extensionFiles) {
        $sourceExtensionFile = Join-Path $sourceExtensionDirectory $relative
        $portableExtensionFile = Join-Path $portableExtensionDirectory $relative
        $sourceExtensionHash = Get-Sha256 $sourceExtensionFile
        $portableExtensionHash = Get-Sha256 $portableExtensionFile
        if ($sourceExtensionHash -ne $portableExtensionHash) {
            throw "Source and portable extension file differ: $relative; $sourceExtensionHash != $portableExtensionHash"
        }
        $extensionHashes[$relative] = $sourceExtensionHash
    }

    $manifest = [ordered]@{
        version = $version
        configuration = $Configuration
        createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
        publishExecutable = $publishExe
        publishSha256 = $publishHash
        portableExecutable = $portableExe
        portableSha256 = $portableHash
        browserExtensionSha256 = $extensionHashes
    }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $portable 'build-manifest.json') -Force

    Invoke-UiSmokeTest $portableExe 'Portable UI smoke test'
    $compressed = $false
    for ($attempt = 1; $attempt -le 10 -and -not $compressed; $attempt++) {
        try {
            if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
            Compress-Archive -Path (Join-Path $portable '*') -DestinationPath $zip -CompressionLevel Optimal
            $compressed = $true
        } catch [System.IO.IOException] {
            if ($attempt -eq 10) { throw }
            Start-Sleep -Seconds 1
        }
    }
    Write-Output "Publish executable SHA-256: $publishHash"
    Write-Output "Portable executable SHA-256: $portableHash"
    Write-Output "Portable directory: $portable"
    Write-Output "Portable ZIP: $zip"
} finally { Pop-Location }
