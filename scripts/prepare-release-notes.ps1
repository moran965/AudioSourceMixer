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
> **UNSIGNED INSTALLER / 未签名安装程序**
>
> This v1.0.0 installer has no Authenticode publisher. Windows may show an unknown-publisher or SmartScreen warning. Download it only from this repository, verify SHA256SUMS.txt, and verify GitHub Artifact Attestation. GitHub provenance is not Authenticode. SignPath Foundation free OSS signing is not yet approved for this binary.
>
> 此 v1.0.0 安装程序没有 Authenticode 发布者。Windows 可能显示“未知发布者”或 SmartScreen 提示。请只从本仓库下载，并核对 SHA256SUMS.txt 与 GitHub Artifact Attestation。GitHub 来源证明不是 Authenticode。SignPath Foundation 免费开源签名尚未批准用于此二进制文件。
"@
} else {
    if ($status -ne 'Valid' -or [string]::IsNullOrWhiteSpace($signer) -or [string]::IsNullOrWhiteSpace($timestamp)) {
        throw "Trusted release evidence is incomplete: status=$status signer=$signer timestamp=$timestamp"
    }
    $notice = @"
> **TRUSTED AUTHENTICODE / 可信 AUTHENTICODE**
>
> Signing path: $SigningMode. Authenticode status: Valid. Publisher: $signer. RFC 3161 timestamp authority: $timestamp.
>
> 签名路径：$SigningMode。Authenticode 状态：Valid。发布者：$signer。RFC 3161 时间戳机构：$timestamp。
"@
}

$pattern = '(?s)<!-- SIGNATURE_NOTICE_START -->.*?<!-- SIGNATURE_NOTICE_END -->'
if ($baseNotes -notmatch $pattern) { throw 'Release notes are missing signature notice markers.' }
$baseNotes = [regex]::Replace($baseNotes, $pattern, "<!-- SIGNATURE_NOTICE_START -->`n$notice`n<!-- SIGNATURE_NOTICE_END -->")
$publisher = if ($signer) { $signer } else { 'none / 无' }
$timestampText = if ($timestamp) { $timestamp } else { 'none / 无' }
$evidence = @"

## Release evidence / 发行证据

- Tag: $Tag
- Commit: $Commit
- Signing mode: $SigningMode
- Authenticode: $status
- Publisher: $publisher
- Timestamp: $timestampText
- Installer size: $size bytes
- Installer SHA-256: $hash

The SHA-256 authority for this download is SHA256SUMS.txt attached to this same Release. Artifact Attestation proves GitHub workflow provenance and does not replace Authenticode.

本次下载的 SHA-256 唯一依据是同一 Release 附带的 SHA256SUMS.txt。Artifact Attestation 用于证明 GitHub 工作流来源，不能替代 Authenticode。
"@

$directory = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
[IO.Directory]::CreateDirectory($directory) | Out-Null
($baseNotes.TrimEnd() + "`n" + $evidence) | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Output "Release notes: $OutputPath"
