param([ValidateSet('Debug','Release')][string] $Configuration = 'Release')
. (Join-Path $PSScriptRoot 'common.ps1')
$root = Get-RepositoryRoot
$dotnet = Get-DotnetExecutable
Push-Location $root
try {
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
    Invoke-Checked { & $dotnet test '.\AudioSourceMixer.sln' --configuration $Configuration --no-build --no-restore } '.NET tests'
    $browserTests = @(Get-ChildItem -LiteralPath '.\tests\browser-extension-tests' -Filter '*.test.mjs' | Select-Object -ExpandProperty FullName)
    Invoke-Checked { & node --test @browserTests } 'Browser extension tests'
    Invoke-Checked { & node '.\scripts\verify-browser-equalizer-runtime.mjs' } 'Chrome/Edge Web Audio EQ runtime tests'
    $extension = Join-Path $root 'src\AudioSourceMixer.BrowserExtension'
    $browserRuntimes = @(
        @{ Name = 'Chrome'; Id = 'chrome'; Path = 'C:\Program Files\Google\Chrome\Application\chrome.exe' },
        @{ Name = 'Edge'; Id = 'edge'; Path = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe' }
    )
    foreach ($browser in $browserRuntimes) {
        if (-not (Test-Path -LiteralPath $browser.Path)) { throw "$($browser.Name) is required for extension runtime verification." }
        Invoke-Checked { & node '.\scripts\verify-browser-runtime.mjs' $browser.Path $extension $browser.Id 'idle' } "$($browser.Name) idle extension runtime"
        Invoke-Checked { & node '.\scripts\verify-browser-authorization-runtime.mjs' $browser.Path $extension $browser.Id } "$($browser.Name) authorization runtime"
    }
    $sourceExecutable = Join-Path $root "src\AudioSourceMixer.Desktop\bin\$Configuration\net8.0-windows\win-x64\AudioSourceMixer.exe"
    Invoke-UiSmokeTest $sourceExecutable "Source $Configuration UI smoke test"
    Invoke-LiveMeterUiTest $sourceExecutable $Configuration (Join-Path $root "artifacts\live-meter-source-$($Configuration.ToLowerInvariant()).json") "Source $Configuration live WPF meter test"
} finally { Pop-Location }
