param(
    [string] $InstallerPath = '',
    [string] $ManifestPath = '',
    [switch] $RequireTrustedSignature
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$root = Get-RepositoryRoot
$version = Get-ProductVersion
if ([string]::IsNullOrWhiteSpace($InstallerPath)) { $InstallerPath = Join-Path $root "artifacts\AudioSourceMixer-$version-win-x64-setup.exe" }
if ([string]::IsNullOrWhiteSpace($ManifestPath)) { $ManifestPath = Join-Path $root "artifacts\AudioSourceMixer-$version-build-manifest.json" }
$installer = [IO.Path]::GetFullPath($InstallerPath)
$manifestFile = [IO.Path]::GetFullPath($ManifestPath)
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw "Installer is missing: $installer" }
if (-not (Test-Path -LiteralPath $manifestFile -PathType Leaf)) { throw "Build manifest is missing: $manifestFile" }

$signature = Get-AuthenticodeSignature -LiteralPath $installer
if ($RequireTrustedSignature -and $signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
    throw "Installer Authenticode status is $($signature.Status), expected Valid."
}
$hash = Get-Sha256 $installer
$manifest = Get-Content -LiteralPath $manifestFile -Raw -Encoding UTF8 | ConvertFrom-Json
$manifest.installer.size = (Get-Item -LiteralPath $installer).Length
$manifest.installer.sha256 = $hash
$manifest.installer | Add-Member -NotePropertyName authenticodeStatus -NotePropertyValue ([string]$signature.Status) -Force
$manifest.installer | Add-Member -NotePropertyName signerSubject -NotePropertyValue ([string]$signature.SignerCertificate.Subject) -Force
$manifest.installer | Add-Member -NotePropertyName signerThumbprint -NotePropertyValue ([string]$signature.SignerCertificate.Thumbprint) -Force
$manifest.installer | Add-Member -NotePropertyName timestampSubject -NotePropertyValue ([string]$signature.TimeStamperCertificate.Subject) -Force
$manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestFile -Encoding UTF8

$checksums = Join-Path (Split-Path $installer -Parent) 'SHA256SUMS.txt'
"$hash  $([IO.Path]::GetFileName($installer))" | Set-Content -LiteralPath $checksums -Encoding ASCII
Write-Output "Installer SHA-256: $hash"
Write-Output "Authenticode: $($signature.Status)"
Write-Output "Checksums: $checksums"
