param([ValidateSet('Both','Chrome','Edge')][string] $Browser = 'Both')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$root = Get-RepositoryRoot
$version = Get-ProductVersion
$portable = Join-Path $root "artifacts\portable\AudioSourceMixer-$version"
$desktop = Join-Path $portable 'AudioSourceMixer.exe'
$extension = Join-Path $portable 'BrowserExtension'
$register = Join-Path $portable 'scripts\register-native-host.ps1'
$unregister = Join-Path $portable 'scripts\unregister-native-host.ps1'
$generatedManifest = Join-Path $portable 'native-host-manifest.generated.json'
$chrome = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
$edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
$dataDirectory = Join-Path $env:LOCALAPPDATA 'AudioSourceMixer'
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "AudioSourceMixer-browser-runtime-$PID-$([Guid]::NewGuid().ToString('N'))"
$dataBackup = Join-Path $temporaryRoot 'user-data'
$dataExisted = Test-Path -LiteralPath $dataDirectory
$desktopProcess = $null

foreach ($required in @($desktop,$extension,$register,$unregister,$chrome,$edge)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Browser runtime verification input is missing: $required" }
}

New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
if ($dataExisted) { Copy-Item -LiteralPath $dataDirectory -Destination $dataBackup -Recurse -Force }

try {
    New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
    $settings = '{"CloseToTray":false,"AutoApplyProfiles":true,"RememberProfiles":true,"ShowInactiveSessions":true}'
    [System.IO.File]::WriteAllText((Join-Path $dataDirectory 'settings.json'), $settings, [System.Text.UTF8Encoding]::new($false))

    & $register
    $desktopProcess = Start-Process -FilePath $desktop -PassThru
    $deadline = [DateTimeOffset]::Now.AddSeconds(30)
    while ([DateTimeOffset]::Now -lt $deadline) {
        Start-Sleep -Milliseconds 200
        $desktopProcess.Refresh()
        if ($desktopProcess.HasExited) { throw "Portable desktop exited before browser testing with code $($desktopProcess.ExitCode)." }
        if ($desktopProcess.MainWindowHandle -ne 0) { break }
    }
    if ($desktopProcess.MainWindowHandle -eq 0) { throw 'Portable desktop did not show its main window for browser testing.' }

    if ($Browser -in @('Both','Chrome')) {
        & node (Join-Path $PSScriptRoot 'verify-browser-runtime.mjs') $chrome $extension 'chrome'
        if ($LASTEXITCODE -ne 0) { throw "Chrome runtime verification failed with exit code $LASTEXITCODE." }
    }
    if ($Browser -in @('Both','Edge')) {
        & node (Join-Path $PSScriptRoot 'verify-browser-runtime.mjs') $edge $extension 'edge'
        if ($LASTEXITCODE -ne 0) { throw "Edge runtime verification failed with exit code $LASTEXITCODE." }
    }

    if (-not $desktopProcess.CloseMainWindow()) { throw 'Could not close portable desktop normally after browser testing.' }
    if (-not $desktopProcess.WaitForExit(30000)) { throw 'Portable desktop did not exit after browser testing.' }
    if ($desktopProcess.ExitCode -ne 0) { throw "Portable desktop exited with code $($desktopProcess.ExitCode)." }
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
    try { & $unregister } catch { Write-Warning "Could not unregister the temporary portable Native Host: $_" }
    if (Test-Path -LiteralPath $generatedManifest) { Remove-Item -LiteralPath $generatedManifest -Force }
    if (Test-Path -LiteralPath $dataDirectory) { Remove-Item -LiteralPath $dataDirectory -Recurse -Force }
    if ($dataExisted) { Copy-Item -LiteralPath $dataBackup -Destination $dataDirectory -Recurse -Force }
    if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}
