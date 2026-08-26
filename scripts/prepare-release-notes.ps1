param(
    [Parameter(Mandatory)][string] $SigningMode,
    [Parameter(Mandatory)][string] $Tag,
    [Parameter(Mandatory)][string] $Commit,
    [string] $ManifestPath = '',
    [string] $BaseNotesPath = '',
    [string] $OutputPath = ''
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

function ConvertFrom-Base64Utf8([string] $Value) {
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Value))
}

# Keep this Windows PowerShell 5.1 entry point ASCII-only. PowerShell 5.1 reads
# UTF-8 scripts without a BOM using the active ANSI code page, which corrupts
# non-ASCII literals on the GitHub-hosted Windows runner.
$unsignedHeadingCn = ConvertFrom-Base64Utf8 '5pyq562+5ZCN5a6J6KOF56iL5bqP'
$unsignedNoticeCn = ConvertFrom-Base64Utf8 '5q2kIHYxLjAuMCDlronoo4XnqIvluo/msqHmnIkgQXV0aGVudGljb2RlIOWPkeW4g+iAheOAgldpbmRvd3Mg5Y+v6IO95pi+56S64oCc5pyq55+l5Y+R5biD6ICF4oCd5oiWIFNtYXJ0U2NyZWVuIOaPkOekuuOAguivt+WPquS7juacrOS7k+W6k+S4i+i9ve+8jOW5tuaguOWvuSBTSEEyNTZTVU1TLnR4dCDkuI4gR2l0SHViIEFydGlmYWN0IEF0dGVzdGF0aW9u44CCR2l0SHViIOadpea6kOivgeaYjuS4jeaYryBBdXRoZW50aWNvZGXjgIJTaWduUGF0aCBGb3VuZGF0aW9uIOWFjei0ueW8gOa6kOetvuWQjeWwmuacquaJueWHhueUqOS6juatpOS6jOi/m+WItuaWh+S7tuOAgg=='
$trustedHeadingCn = ConvertFrom-Base64Utf8 '5Y+v5L+hIEFVVEhFTlRJQ09ERQ=='
$trustedNoticeFormatCn = ConvertFrom-Base64Utf8 '562+5ZCN6Lev5b6E77yaezB944CCQXV0aGVudGljb2RlIOeKtuaAge+8mlZhbGlk44CC5Y+R5biD6ICF77yaezF944CCUkZDIDMxNjEg5pe26Ze05oiz5py65p6E77yaezJ944CC'
$noneCn = ConvertFrom-Base64Utf8 '5peg'
$evidenceHeadingCn = ConvertFrom-Base64Utf8 '5Y+R6KGM6K+B5o2u'
$evidenceNoticeCn = ConvertFrom-Base64Utf8 '5pys5qyh5LiL6L2955qEIFNIQS0yNTYg5ZSv5LiA5L6d5o2u5piv5ZCM5LiAIFJlbGVhc2Ug6ZmE5bim55qEIFNIQTI1NlNVTVMudHh044CCQXJ0aWZhY3QgQXR0ZXN0YXRpb24g55So5LqO6K+B5piOIEdpdEh1YiDlt6XkvZzmtYHmnaXmupDvvIzkuI3og73mm7/ku6MgQXV0aGVudGljb2Rl44CC'

$root = Get-RepositoryRoot
$version = Get-ProductVersion
if ($SigningMode -notin @('unsigned','signpath','azure')) { throw "Unsupported signing mode: $SigningMode" }
if ($Tag -ne "v$version") { throw "Release tag $Tag does not match product version $version." }
if ($Commit -notmatch '^[0-9a-f]{40}$') { throw "Invalid release commit: $Commit" }
if ([string]::IsNullOrWhiteSpace($ManifestPath)) { $ManifestPath = Join-Path $root "artifacts\AudioSourceMixer-$version-build-manifest.json" }
if ([string]::IsNullOrWhiteSpace($BaseNotesPath)) { $BaseNotesPath = Join-Path $root "docs\release-notes-$version.md" }
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $root 'artifacts\release-notes.md' }

$manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$baseNotes = Get-Content -LiteralPath $BaseNotesPath -Raw -Encoding UTF8
$installer = $manifest.installer
$status = [string]$installer.authenticodeStatus
$signer = [string]$installer.signerSubject
$timestamp = [string]$installer.timestampSubject
$hash = [string]$installer.sha256
$size = [long]$installer.size

if ($SigningMode -eq 'unsigned') {
    if ($status -ne 'NotSigned' -or -not [string]::IsNullOrWhiteSpace($signer) -or -not [string]::IsNullOrWhiteSpace($timestamp)) {
        throw "Unsigned release evidence is inconsistent: status=$status signer=$signer timestamp=$timestamp"
    }
    $notice = @"
> **UNSIGNED INSTALLER / $unsignedHeadingCn**
>
> This v1.0.0 installer has no Authenticode publisher. Windows may show an unknown-publisher or SmartScreen warning. Download it only from this repository, verify SHA256SUMS.txt, and verify GitHub Artifact Attestation. GitHub provenance is not Authenticode. SignPath Foundation free OSS signing is not yet approved for this binary.
>
> $unsignedNoticeCn
"@
} else {
    if ($status -ne 'Valid' -or [string]::IsNullOrWhiteSpace($signer) -or [string]::IsNullOrWhiteSpace($timestamp)) {
        throw "Trusted release evidence is incomplete: status=$status signer=$signer timestamp=$timestamp"
    }
    $trustedNoticeCn = $trustedNoticeFormatCn -f $SigningMode, $signer, $timestamp
    $notice = @"
> **TRUSTED AUTHENTICODE / $trustedHeadingCn**
>
> Signing path: $SigningMode. Authenticode status: Valid. Publisher: $signer. RFC 3161 timestamp authority: $timestamp.
>
> $trustedNoticeCn
"@
}

$pattern = '(?s)<!-- SIGNATURE_NOTICE_START -->.*?<!-- SIGNATURE_NOTICE_END -->'
if ($baseNotes -notmatch $pattern) { throw 'Release notes are missing signature notice markers.' }
$baseNotes = [regex]::Replace($baseNotes, $pattern, "<!-- SIGNATURE_NOTICE_START -->`n$notice`n<!-- SIGNATURE_NOTICE_END -->")
$publisher = if ($signer) { $signer } else { "none / $noneCn" }
$timestampText = if ($timestamp) { $timestamp } else { "none / $noneCn" }
$evidence = @"

## Release evidence / $evidenceHeadingCn

- Tag: $Tag
- Commit: $Commit
- Signing mode: $SigningMode
- Authenticode: $status
- Publisher: $publisher
- Timestamp: $timestampText
- Installer size: $size bytes
- Installer SHA-256: $hash

The SHA-256 authority for this download is SHA256SUMS.txt attached to this same Release. Artifact Attestation proves GitHub workflow provenance and does not replace Authenticode.

$evidenceNoticeCn
"@

$directory = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
[IO.Directory]::CreateDirectory($directory) | Out-Null
($baseNotes.TrimEnd() + "`n" + $evidence) | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Output "Release notes: $OutputPath"
