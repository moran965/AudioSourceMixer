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
    $sourceExecutable = Join-Path $root "src\AudioSourceMixer.Desktop\bin\$Configuration\net8.0-windows\win-x64\AudioSourceMixer.exe"
    Invoke-UiSmokeTest $sourceExecutable "Source $Configuration UI smoke test"
} finally { Pop-Location }
