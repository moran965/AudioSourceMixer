param([ValidateSet('Both','Chrome','Edge')][string] $Browser = 'Both')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$root = Get-RepositoryRoot
$version = Get-ProductVersion
$runtimePayload = Join-Path $root "artifacts\staging\$version\installer-runtime-payload"
$desktop = Join-Path $runtimePayload 'AudioSourceMixer.exe'
$nativeHost = Join-Path $runtimePayload 'AudioSourceMixer.NativeHost.exe'
$extension = Join-Path $runtimePayload 'BrowserExtension'
$chrome = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
$edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
$dataDirectory = Join-Path $env:LOCALAPPDATA 'AudioSourceMixer'
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "AudioSourceMixer-browser-runtime-$PID-$([Guid]::NewGuid().ToString('N'))"
$generatedManifest = Join-Path $temporaryRoot 'native-host-manifest.json'
$dataBackup = Join-Path $temporaryRoot 'user-data'
$dataExisted = Test-Path -LiteralPath $dataDirectory
$nativeHostKeys = @(
    'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.audiosourcemixer.bridge',
    'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.audiosourcemixer.bridge'
)
$nativeHostBackup = @{}
$desktopProcess = $null

function Get-Inventory([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return @() }
    $prefix = [IO.Path]::GetFullPath($Path).TrimEnd('\') + '\'
    return @(Get-ChildItem -LiteralPath $Path -File -Recurse | ForEach-Object {
        "$($_.FullName.Substring($prefix.Length))|$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
    } | Sort-Object)
}

function Register-TestNativeHost {
    $trusted = Get-Content -LiteralPath (Join-Path $runtimePayload 'browser-extension-origins.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $ids = @($trusted.developmentExtensionId,$trusted.chromeStoreExtensionId,$trusted.edgeStoreExtensionId) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
    if ($ids.Count -eq 0 -or @($ids | Where-Object { $_ -notmatch '^[a-p]{32}$' }).Count -ne 0) {
        throw 'Trusted extension configuration contains a missing or invalid extension ID.'
    }
    $manifest = [ordered]@{
        name = 'com.audiosourcemixer.bridge'
        description = 'Audio Source Mixer browser bridge'
        path = $nativeHost
        type = 'stdio'
        allowed_origins = @($ids | ForEach-Object { "chrome-extension://$_/" })
    }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $generatedManifest -Encoding UTF8
    foreach ($key in $nativeHostKeys) {
        New-Item -Path $key -Force | Out-Null
        Set-Item -Path $key -Value $generatedManifest
    }
}

foreach ($required in @($desktop,$nativeHost,$extension,$chrome,$edge)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Browser runtime verification input is missing: $required" }
}

New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
$dataInventory = @(Get-Inventory $dataDirectory)
if ($dataExisted) {
    Copy-Item -LiteralPath $dataDirectory -Destination $dataBackup -Recurse -Force
    if (@(Compare-Object $dataInventory (Get-Inventory $dataBackup)).Count -ne 0) {
        throw 'Could not verify the user-data backup before browser runtime isolation.'
    }
    Remove-Item -LiteralPath $dataDirectory -Recurse -Force
}
foreach ($key in $nativeHostKeys) {
    $nativeHostBackup[$key] = if (Test-Path -LiteralPath $key) {
        [ordered]@{ Exists = $true; Value = [string](Get-Item -LiteralPath $key).GetValue('') }
    } else { [ordered]@{ Exists = $false; Value = $null } }
}

try {
    New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
    $settings = '{"CloseToTray":false,"AutoApplyProfiles":true,"RememberProfiles":true,"ShowInactiveSessions":true,"BrowserOnboardingChoice":"runtime-test","OnboardingCompletedVersion":"1.0.0","BrowserGuideDismissed":true,"Language":"en-US","SchemaVersion":8}'
    [System.IO.File]::WriteAllText((Join-Path $dataDirectory 'settings.json'), $settings, [System.Text.UTF8Encoding]::new($false))

    Register-TestNativeHost
    $desktopProcess = Start-Process -FilePath $desktop -PassThru
    $deadline = [DateTimeOffset]::Now.AddSeconds(30)
    while ([DateTimeOffset]::Now -lt $deadline) {
        Start-Sleep -Milliseconds 200
        $desktopProcess.Refresh()
        if ($desktopProcess.HasExited) { throw "Installer runtime payload exited before browser testing with code $($desktopProcess.ExitCode)." }
        if ($desktopProcess.MainWindowHandle -ne 0) { break }
    }
    if ($desktopProcess.MainWindowHandle -eq 0) { throw 'Installer runtime payload did not show its main window for browser testing.' }

    if ($Browser -in @('Both','Chrome')) {
        & node (Join-Path $PSScriptRoot 'verify-browser-runtime.mjs') $chrome $extension 'chrome'
        if ($LASTEXITCODE -ne 0) { throw "Chrome runtime verification failed with exit code $LASTEXITCODE." }
        & node (Join-Path $PSScriptRoot 'verify-browser-authorization-runtime.mjs') $chrome $extension 'chrome'
        if ($LASTEXITCODE -ne 0) { throw "Chrome authorization runtime verification failed with exit code $LASTEXITCODE." }
    }
    if ($Browser -in @('Both','Edge')) {
        & node (Join-Path $PSScriptRoot 'verify-browser-runtime.mjs') $edge $extension 'edge'
        if ($LASTEXITCODE -ne 0) { throw "Edge runtime verification failed with exit code $LASTEXITCODE." }
        & node (Join-Path $PSScriptRoot 'verify-browser-authorization-runtime.mjs') $edge $extension 'edge'
        if ($LASTEXITCODE -ne 0) { throw "Edge authorization runtime verification failed with exit code $LASTEXITCODE." }
    }

    if (-not $desktopProcess.CloseMainWindow()) { throw 'Could not close installer runtime payload normally after browser testing.' }
    if (-not $desktopProcess.WaitForExit(30000)) { throw 'Installer runtime payload did not exit after browser testing.' }
    if ($desktopProcess.ExitCode -ne 0) { throw "Installer runtime payload exited with code $($desktopProcess.ExitCode)." }
    $desktopProcess = $null
    $rollback = Join-Path $dataDirectory 'rollback.json'
    if (Test-Path -LiteralPath $rollback) {
        if (@(Get-Content -LiteralPath $rollback -Raw -Encoding UTF8 | ConvertFrom-Json).Count -ne 0) {
            throw 'Browser runtime verification left audio rollback entries.'
        }
    }
    Write-Output "$Browser runtime extension/Native Messaging protocol 2 verification passed."
}
finally {
    if ($null -ne $desktopProcess -and -not $desktopProcess.HasExited) {
        if (-not $desktopProcess.CloseMainWindow() -or -not $desktopProcess.WaitForExit(10000)) {
            Stop-Process -Id $desktopProcess.Id -Force -ErrorAction SilentlyContinue
        }
    }
    foreach ($key in $nativeHostKeys) {
        if (Test-Path -LiteralPath $key) { Remove-Item -LiteralPath $key -Recurse -Force }
    }
    foreach ($key in $nativeHostKeys) {
        $saved = $nativeHostBackup[$key]
        if ($saved.Exists) {
            New-Item -Path $key -Force | Out-Null
            (Get-Item -LiteralPath $key).SetValue('', [string]$saved.Value)
        }
    }
    if (Test-Path -LiteralPath $dataDirectory) { Remove-Item -LiteralPath $dataDirectory -Recurse -Force }
    if ($dataExisted) { Copy-Item -LiteralPath $dataBackup -Destination $dataDirectory -Recurse -Force }
    if (@(Compare-Object $dataInventory (Get-Inventory $dataDirectory)).Count -ne 0) {
        throw 'User data was not restored byte-for-byte after browser runtime verification.'
    }
    if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}
