param([ValidateSet('Debug','Release')][string] $Configuration = 'Release')
. (Join-Path $PSScriptRoot 'common.ps1')
. (Join-Path $PSScriptRoot 'runtime-payload.ps1')
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
foreach ($path in @($publish,$portable)) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}
foreach ($path in @($zip,$manifestPath)) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
}
New-Item -ItemType Directory -Path $publish,$portable -Force | Out-Null

function Copy-PayloadFile([string] $RelativePath) {
    $normalized = $RelativePath.Replace('/', '\')
    $source = if ($normalized.StartsWith('BrowserExtension\', [StringComparison]::OrdinalIgnoreCase)) {
        Join-Path $root ('src\AudioSourceMixer.BrowserExtension\' + $normalized.Substring('BrowserExtension\'.Length))
    } else { Join-Path $root $normalized }
    $destination = Join-Path $portable $normalized
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Allowlisted source file is missing: $source" }
    New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination -Force
}

Push-Location $root
try {
    Invoke-Checked { & $dotnet publish '.\src\AudioSourceMixer.Desktop\AudioSourceMixer.Desktop.csproj' -c $Configuration -r win-x64 --self-contained true -o (Join-Path $publish 'desktop') -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None } 'Desktop publish'
    Invoke-Checked { & $dotnet publish '.\src\AudioSourceMixer.NativeHost\AudioSourceMixer.NativeHost.csproj' -c $Configuration -r win-x64 --self-contained true -o (Join-Path $publish 'native-host') -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None } 'Native host publish'
    Copy-Item -LiteralPath (Join-Path $publish 'desktop\AudioSourceMixer.exe') -Destination $portable -Force
    Copy-Item -LiteralPath (Join-Path $publish 'native-host\AudioSourceMixer.NativeHost.exe') -Destination $portable -Force
    $allowlist = Get-RuntimeAllowlist
    foreach ($entry in @($allowlist.runtimeFiles) + @($allowlist.portableOnlyFiles)) {
        if ($entry.path -in @('AudioSourceMixer.exe','AudioSourceMixer.NativeHost.exe')) { continue }
        Copy-PayloadFile ([string]$entry.path)
    }

    foreach ($obsoleteHelper in @((Join-Path $publish 'desktop\ProcessBoostHost.exe'), (Join-Path $portable 'ProcessBoostHost.exe'))) {
        if (Test-Path -LiteralPath $obsoleteHelper) { throw "Obsolete ordinary-session boost helper entered the payload: $obsoleteHelper" }
    }
    $publishExe = Join-Path $publish 'desktop\AudioSourceMixer.exe'
    $portableExe = Join-Path $portable 'AudioSourceMixer.exe'
    $publishHash = Get-Sha256 $publishExe
    $portableHash = Get-Sha256 $portableExe
    if ($publishHash -ne $portableHash) { throw "Published and portable desktop executables differ: $publishHash != $portableHash" }
    $inventory = @(Assert-RuntimePayload $portable 'Portable')
    $manifest = [ordered]@{
        schemaVersion = 2
        version = $version
        configuration = $Configuration
        createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
        publish = [ordered]@{
            desktop = [ordered]@{ path = 'publish/desktop/AudioSourceMixer.exe'; size = (Get-Item $publishExe).Length; sha256 = $publishHash }
            nativeHost = [ordered]@{ path = 'publish/native-host/AudioSourceMixer.NativeHost.exe'; size = (Get-Item (Join-Path $publish 'native-host\AudioSourceMixer.NativeHost.exe')).Length; sha256 = (Get-Sha256 (Join-Path $publish 'native-host\AudioSourceMixer.NativeHost.exe')) }
        }
        portable = [ordered]@{ path = "portable/AudioSourceMixer-$version"; files = $inventory }
    }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

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
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try { $zipEntries = @($archive.Entries | Where-Object Name | ForEach-Object { $_.FullName.Replace('\', '/') }) }
    finally { $archive.Dispose() }
    if (@(Compare-Object @($inventory.path | Sort-Object) @($zipEntries | Sort-Object)).Count -ne 0) { throw 'Portable ZIP inventory differs from the verified directory.' }
    Write-Output "Publish executable SHA-256: $publishHash"
    Write-Output "Portable executable SHA-256: $portableHash"
    Write-Output "Portable directory: $portable"
    Write-Output "Portable ZIP: $zip"
} finally { Pop-Location }
