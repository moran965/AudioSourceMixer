$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$root = Get-RepositoryRoot
$artifacts = Join-Path $root 'artifacts'
$baseline = Join-Path $artifacts 'AudioSourceMixer-0.2.1-win-x64-setup.exe'

Push-Location $root
try {
    & (Join-Path $PSScriptRoot 'test.ps1') -Configuration Release
    if ($LASTEXITCODE -ne 0) { throw "Release tests failed with exit code $LASTEXITCODE." }
    & (Join-Path $PSScriptRoot 'package-installer.ps1') -Configuration Release
    if ($LASTEXITCODE -ne 0) { throw "Packaging failed with exit code $LASTEXITCODE." }
    if (Test-Path -LiteralPath $baseline) {
        & (Join-Path $PSScriptRoot 'verify-installer.ps1') -BaselineInstallerPath $baseline
    } else {
        & (Join-Path $PSScriptRoot 'verify-installer.ps1')
    }
    if ($LASTEXITCODE -ne 0) { throw "Installer verification failed with exit code $LASTEXITCODE." }
    & (Join-Path $PSScriptRoot 'verify-browser-runtime.ps1') -Browser Both
    if ($LASTEXITCODE -ne 0) { throw "Packaged Chrome/Edge runtime verification failed with exit code $LASTEXITCODE." }
    Write-Output "All v$(Get-ProductVersion) deliverables are under $artifacts."
} finally { Pop-Location }
