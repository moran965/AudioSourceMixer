param([ValidateSet('Debug','Release')][string] $Configuration = 'Release')
. (Join-Path $PSScriptRoot 'common.ps1')
$root = Get-RepositoryRoot
$dotnet = Get-DotnetExecutable
Push-Location $root
try {
    Invoke-Checked { & $dotnet restore '.\AudioSourceMixer.sln' } 'Dependency restore'
    Invoke-Checked { & $dotnet build '.\AudioSourceMixer.sln' --configuration $Configuration --no-restore } 'Solution build'
} finally { Pop-Location }
