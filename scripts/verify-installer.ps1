param([string] $BaselineInstallerPath = '')
. (Join-Path $PSScriptRoot 'common.ps1')
$ErrorActionPreference = 'Stop'

$root = Get-RepositoryRoot
$version = Get-ProductVersion
$artifacts = Join-Path $root 'artifacts'
$setup = Join-Path $artifacts "AudioSourceMixer-$version-win-x64-setup.exe"
$publishExe = Join-Path $artifacts "staging\$version\publish\desktop\AudioSourceMixer.exe"
$portableDirectory = Join-Path $artifacts "portable\AudioSourceMixer-$version"
$portableExe = Join-Path $portableDirectory 'AudioSourceMixer.exe'
$manifestPath = Join-Path $artifacts "AudioSourceMixer-$version-build-manifest.json"
$defaultInstall = Join-Path $env:LOCALAPPDATA 'Programs\AudioSourceMixer'
$customSpace = Join-Path $env:LOCALAPPDATA 'Audio Source Mixer Test\Custom Path'
$customChineseLeaf = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('6Z+z6aKR5re36Z+z5Zmo5rWL6K+VXOiHquWumuS5ieS9jee9rg=='))
$customChinese = Join-Path $env:LOCALAPPDATA $customChineseLeaf
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AudioSourceMixer'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$chromeKey = 'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.audiosourcemixer.bridge'
$edgeKey = 'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.audiosourcemixer.bridge'
$dataDirectory = Join-Path $env:LOCALAPPDATA 'AudioSourceMixer'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "AudioSourceMixer-v020-$PID-$([Guid]::NewGuid().ToString('N'))"
$dataBackup = Join-Path $temporaryRoot 'user-data'
$baselineCopy = Join-Path $temporaryRoot 'AudioSourceMixer-0.1.2-win-x64-setup.exe'
$preexistingRun = $null
$installedHash = $null
$results = [ordered]@{}

function Start-Checked([string] $Path, [string[]] $Arguments, [string] $Description, [int] $Expected = 0, [int] $Timeout = 90000) {
    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit($Timeout)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw "$Description timed out."
    }
    if ($process.ExitCode -ne $Expected) { throw "$Description exited $($process.ExitCode), expected $Expected." }
    Write-Output "$Description passed (exit $Expected)."
}

function Quote-Argument([string] $Value) { return ([char]34).ToString() + $Value + ([char]34).ToString() }

function Get-Inventory([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return @() }
    $prefix = [IO.Path]::GetFullPath($Path).TrimEnd('\') + '\'
    return @(Get-ChildItem -LiteralPath $Path -File -Recurse | ForEach-Object {
        "$($_.FullName.Substring($prefix.Length))|$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
    } | Sort-Object)
}

function Assert-Install([string] $Directory, [bool] $StartupExpected = $false) {
    $directory = [IO.Path]::GetFullPath($Directory).TrimEnd('\')
    $exe = Join-Path $directory 'AudioSourceMixer.exe'
    $uninstaller = Join-Path $directory 'AudioSourceMixer.Uninstall.exe'
    foreach ($file in @($exe,$uninstaller,(Join-Path $directory 'install-identity.json'),(Join-Path $directory 'native-host-manifest.json'))) {
        if (-not (Test-Path -LiteralPath $file)) { throw "Installed file missing: $file" }
    }
    if (-not (Test-Path -LiteralPath $uninstallKey)) { throw 'Uninstall registry key is missing.' }
    $registration = Get-ItemProperty -LiteralPath $uninstallKey
    if ([string]$registration.DisplayVersion -ne $version) { throw "DisplayVersion is $($registration.DisplayVersion)." }
    if (-not ([IO.Path]::GetFullPath([string]$registration.InstallLocation).TrimEnd('\').Equals($directory, [StringComparison]::OrdinalIgnoreCase))) {
        throw "InstallLocation mismatch: $($registration.InstallLocation)"
    }
    if ([string]$registration.UninstallString -ne "`"$uninstaller`" --uninstall") { throw 'UninstallString is not the installed uninstaller.' }
    foreach ($key in @($chromeKey,$edgeKey)) {
        $manifest = [string](Get-Item -LiteralPath $key).GetValue('')
        if (-not $manifest.Equals((Join-Path $directory 'native-host-manifest.json'), [StringComparison]::OrdinalIgnoreCase)) {
            throw "Native host registration mismatch: $key = $manifest"
        }
    }
    $hostManifest = Get-Content -LiteralPath (Join-Path $directory 'native-host-manifest.json') -Raw | ConvertFrom-Json
    if (-not ([IO.Path]::GetFullPath([string]$hostManifest.path).Equals((Join-Path $directory 'AudioSourceMixer.NativeHost.exe'), [StringComparison]::OrdinalIgnoreCase))) {
        throw 'Native host executable path is not the selected install directory.'
    }
    $run = if (Test-Path -LiteralPath $runKey) { [string](Get-ItemProperty -LiteralPath $runKey -Name AudioSourceMixer -ErrorAction SilentlyContinue).AudioSourceMixer } else { '' }
    if ($StartupExpected) {
        $expected = "`"$exe`" --background"
        if ($run -ne $expected) { throw "Startup command '$run' != '$expected'." }
    } elseif (-not [string]::IsNullOrEmpty($run)) { throw "Startup must default off, found: $run" }
    Invoke-UiSmokeTest $exe "Installed UI smoke ($directory)"
}

function Verify-UninstallerWindow([string] $Directory) {
    $uninstaller = Join-Path $Directory 'AudioSourceMixer.Uninstall.exe'
    $process = Start-Process -FilePath $uninstaller -PassThru
    $deadline = [DateTimeOffset]::Now.AddSeconds(30)
    do { Start-Sleep -Milliseconds 200; $process.Refresh() } while (-not $process.HasExited -and $process.MainWindowHandle -eq 0 -and [DateTimeOffset]::Now -lt $deadline)
    $expectedTitle = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('5Y246L29IEF1ZGlvIFNvdXJjZSBNaXhlcg=='))
    if ($process.HasExited -or $process.MainWindowHandle -eq 0 -or $process.MainWindowTitle -ne $expectedTitle) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
        throw "No-argument uninstaller did not show the dedicated uninstall window. Title='$($process.MainWindowTitle)'"
    }
    if (-not $process.CloseMainWindow() -or -not $process.WaitForExit(15000)) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue; throw 'Could not close uninstaller mode probe.' }
    Write-Output 'No-argument installed uninstaller showed the dedicated uninstall UI.'
}

function Verify-NormalLaunch([string] $Directory) {
    $started = [DateTimeOffset]::Now
    $process = Start-Process -FilePath (Join-Path $Directory 'AudioSourceMixer.exe') -PassThru
    $deadline = [DateTimeOffset]::Now.AddSeconds(30)
    do { Start-Sleep -Milliseconds 200; $process.Refresh() } while (-not $process.HasExited -and $process.MainWindowHandle -eq 0 -and [DateTimeOffset]::Now -lt $deadline)
    if ($process.HasExited -or $process.MainWindowHandle -eq 0 -or $process.MainWindowTitle -ne 'Audio Source Mixer') {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
        throw 'Normal launch did not show the Audio Source Mixer main window.'
    }
    $log = Join-Path $dataDirectory 'logs\AudioSourceMixer.log'
    $startup = $null
    $deadline = [DateTimeOffset]::Now.AddSeconds(15)
    while ($null -eq $startup -and [DateTimeOffset]::Now -lt $deadline) {
        Start-Sleep -Milliseconds 200
        if (Test-Path -LiteralPath $log) {
            $startup = Get-Content -LiteralPath $log -Tail 100 | Where-Object {
                $_ -match 'Application startup completed successfully\. WindowShown=True; Sources=(\d+); MaterializedItems=(\d+)\.' -and
                [DateTimeOffset]::Parse($_.Split(' ')[0]) -ge $started.AddSeconds(-1)
            } | Select-Object -Last 1
        }
    }
    if ($null -eq $startup -or $startup -notmatch 'Sources=(\d+); MaterializedItems=(\d+)' -or [int]$Matches[1] -lt 1 -or [int]$Matches[2] -lt 1) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw "Normal launch did not materialize a real audio source: $startup"
    }
    $signal = [Threading.EventWaitHandle]::OpenExisting('Local\AudioSourceMixer.Exit')
    try { $signal.Set() | Out-Null } finally { $signal.Dispose() }
    if (-not $process.WaitForExit(30000) -or $process.ExitCode -ne 0) { throw 'Normal app did not restore audio and exit cleanly.' }
    Write-Output "Normal visible launch passed: $startup"
}

function Wait-Removed([string] $Directory) {
    # A single-file installed uninstaller can remain locked briefly while Windows Defender
    # finishes scanning it. The helper logs completion and this bounded wait observes it.
    $deadline = [DateTimeOffset]::Now.AddSeconds(60)
    while ((Test-Path -LiteralPath $Directory) -and [DateTimeOffset]::Now -lt $deadline) { Start-Sleep -Milliseconds 250 }
    if (Test-Path -LiteralPath $Directory) { throw "Uninstall left directory: $Directory" }
    foreach ($key in @($uninstallKey,$chromeKey,$edgeKey)) { if (Test-Path -LiteralPath $key) { throw "Uninstall left registry key: $key" } }
    $run = if (Test-Path -LiteralPath $runKey) { (Get-ItemProperty -LiteralPath $runKey -Name AudioSourceMixer -ErrorAction SilentlyContinue).AudioSourceMixer } else { $null }
    if ($null -ne $run) { throw "Uninstall left startup value: $run" }
}

function Uninstall-Checked([string] $Directory, [switch] $WithRunningApp) {
    $process = $null
    if ($WithRunningApp) {
        $process = Start-Process -FilePath (Join-Path $Directory 'AudioSourceMixer.exe') -ArgumentList '--background' -WindowStyle Hidden -PassThru
        Start-Sleep -Seconds 2
        $process.Refresh()
        if ($process.HasExited -or $process.MainWindowHandle -ne 0) { throw 'Background startup did not remain in tray-only mode.' }
    }
    Start-Checked (Join-Path $Directory 'AudioSourceMixer.Uninstall.exe') @('--silent-uninstall') 'Silent uninstall'
    if ($null -ne $process -and -not $process.WaitForExit(15000)) { throw 'Running desktop did not exit gracefully for uninstall.' }
    Wait-Removed $Directory
}

foreach ($required in @($setup,$publishExe,$portableExe,$manifestPath)) { if (-not (Test-Path -LiteralPath $required)) { throw "Missing verification input: $required" } }
if (Test-Path -LiteralPath $uninstallKey) { throw 'Installer verification refuses to replace a pre-existing Audio Source Mixer installation.' }
foreach ($path in @($defaultInstall,$customSpace,$customChinese)) { if (Test-Path -LiteralPath $path) { throw "Verification target already exists: $path" } }

New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
$dataExisted = Test-Path -LiteralPath $dataDirectory
$dataInventory = @(Get-Inventory $dataDirectory)
if ($dataExisted) { Copy-Item -LiteralPath $dataDirectory -Destination $dataBackup -Recurse -Force }
if (Test-Path -LiteralPath $runKey) { $preexistingRun = (Get-ItemProperty -LiteralPath $runKey -Name AudioSourceMixer -ErrorAction SilentlyContinue).AudioSourceMixer }
if (-not [string]::IsNullOrWhiteSpace($BaselineInstallerPath) -and (Test-Path -LiteralPath $BaselineInstallerPath)) {
    Copy-Item -LiteralPath ([IO.Path]::GetFullPath($BaselineInstallerPath)) -Destination $baselineCopy -Force
}

try {
    Start-Checked $setup @('--silent-install','--install-dir',(Quote-Argument $defaultInstall)) 'Fresh default install'
    Assert-Install $defaultInstall
    Verify-UninstallerWindow $defaultInstall
    $firstHash = Get-Sha256 (Join-Path $defaultInstall 'AudioSourceMixer.exe')
    $sentinel = Join-Path $defaultInstall 'repair-sentinel.txt'; Set-Content -LiteralPath $sentinel -Value 'must be replaced'
    Start-Checked $setup @('--silent-install') 'Same-version repair'
    Assert-Install $defaultInstall
    if (Test-Path -LiteralPath $sentinel) { throw 'Repair did not atomically replace the previous directory.' }
    $rollbackSentinel = Join-Path $defaultInstall 'rollback-sentinel.txt'; Set-Content -LiteralPath $rollbackSentinel -Value 'must survive rollback'
    Start-Checked $setup @('--silent-install','--test-fail-after-backup') 'Injected rollback test' 1
    if (-not (Test-Path -LiteralPath $rollbackSentinel) -or (Get-Sha256 (Join-Path $defaultInstall 'AudioSourceMixer.exe')) -ne $firstHash) {
        throw 'Failed install did not restore the previous product directory.'
    }
    $results.defaultInstall = 'passed'; $results.sameVersionRepair = 'passed'; $results.rollback = 'passed'; $results.manualUninstallerMode = 'passed'
    Uninstall-Checked $defaultInstall -WithRunningApp

    Start-Checked $setup @('--silent-install','--install-dir',(Quote-Argument $customSpace)) 'Custom path with spaces install'
    Assert-Install $customSpace
    Uninstall-Checked $customSpace
    $results.customPathWithSpaces = 'passed'

    Start-Checked $setup @('--silent-install','--install-dir',(Quote-Argument $customChinese),'--startup-background') 'Chinese custom path and startup install'
    Assert-Install $customChinese $true
    Uninstall-Checked $customChinese -WithRunningApp
    $results.customChinesePath = 'passed'; $results.startupEnableDisableCleanup = 'passed'; $results.backgroundTrayStartup = 'passed'

    if (Test-Path -LiteralPath $baselineCopy) {
        Start-Checked $baselineCopy @('--silent-install') 'Install 0.1.2 upgrade baseline'
        $baselineExe = Join-Path $defaultInstall 'AudioSourceMixer.exe'
        if ([string](Get-Item -LiteralPath $baselineExe).VersionInfo.FileVersion -ne '0.1.2.0') { throw 'Baseline installer is not 0.1.2.' }
        $upgradeSentinel = Join-Path $defaultInstall 'v0.1.2-upgrade-sentinel.txt'; Set-Content -LiteralPath $upgradeSentinel -Value 'old payload'
        $baselineHash = Get-Sha256 $baselineExe
        Start-Checked $setup @('--silent-install') 'In-place 0.1.2 to 0.2.0 upgrade'
        Assert-Install $defaultInstall
        if (Test-Path -LiteralPath $upgradeSentinel) { throw 'Upgrade retained an old payload sentinel.' }
        if ((Get-Sha256 (Join-Path $defaultInstall 'AudioSourceMixer.exe')) -eq $baselineHash) { throw 'Upgrade executable hash did not change.' }
        $results.inPlaceUpgradeFrom012 = 'passed'
        Uninstall-Checked $defaultInstall
    } else { $results.inPlaceUpgradeFrom012 = 'not executed: baseline artifact unavailable' }

    Start-Checked $setup @('--silent-install','--install-dir',(Quote-Argument $defaultInstall)) 'Final hash verification install'
    Assert-Install $defaultInstall
    Verify-NormalLaunch $defaultInstall
    $publishHash = Get-Sha256 $publishExe; $portableHash = Get-Sha256 $portableExe; $installedHash = Get-Sha256 (Join-Path $defaultInstall 'AudioSourceMixer.exe')
    if ($publishHash -ne $portableHash -or $portableHash -ne $installedHash) { throw "Executable hash mismatch: $publishHash / $portableHash / $installedHash" }
    $sourceExtension = Get-Inventory (Join-Path $root 'src\AudioSourceMixer.BrowserExtension')
    $installedExtension = Get-Inventory (Join-Path $defaultInstall 'BrowserExtension')
    if (@(Compare-Object $sourceExtension $installedExtension).Count -ne 0) { throw 'Installed browser extension inventory differs from source.' }
    Uninstall-Checked $defaultInstall
    $results.publishPortableInstalledHash = 'passed'; $results.installedExtensionInventory = 'passed'; $results.silentUninstall = 'passed'; $results.runningAppGracefulUninstall = 'passed'; $results.normalVisibleLaunch = 'passed'

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $manifest | Add-Member -NotePropertyName installedExecutable -NotePropertyValue (Join-Path $defaultInstall 'AudioSourceMixer.exe') -Force
    $manifest | Add-Member -NotePropertyName installedSha256 -NotePropertyValue $installedHash -Force
    $manifest | Add-Member -NotePropertyName installerVerification -NotePropertyValue $results -Force
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    Write-Output "Publish SHA-256: $publishHash"
    Write-Output "Portable SHA-256: $portableHash"
    Write-Output "Installed SHA-256: $installedHash"
}
finally {
    foreach ($directory in @($defaultInstall,$customSpace,$customChinese)) {
        $uninstaller = Join-Path $directory 'AudioSourceMixer.Uninstall.exe'
        if (Test-Path -LiteralPath $uninstaller) { try { Start-Checked $uninstaller @('--silent-uninstall') 'Failure cleanup uninstall' } catch { Write-Warning $_ } }
    }
    if (Test-Path -LiteralPath $dataDirectory) { Remove-Item -LiteralPath $dataDirectory -Recurse -Force }
    if ($dataExisted) { Copy-Item -LiteralPath $dataBackup -Destination $dataDirectory -Recurse -Force }
    if (@(Compare-Object $dataInventory (Get-Inventory $dataDirectory)).Count -ne 0) { throw 'User data was not restored byte-for-byte after installer verification.' }
    if ($null -ne $preexistingRun) { New-Item -Path $runKey -Force | Out-Null; Set-ItemProperty -LiteralPath $runKey -Name AudioSourceMixer -Value $preexistingRun }
    if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}
