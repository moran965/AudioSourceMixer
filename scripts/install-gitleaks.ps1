param([string] $DestinationDirectory = '')
$ErrorActionPreference = 'Stop'

$version = '8.30.1'
$archiveName = "gitleaks_${version}_windows_x64.zip"
$expectedSha256 = 'D29144DEFF3A68AA93CED33DDDF84B7FDC26070ADD4AA0F4513094C8332AFC4E'
if ([string]::IsNullOrWhiteSpace($DestinationDirectory)) {
    $DestinationDirectory = Join-Path ([IO.Path]::GetTempPath()) "AudioSourceMixer-tools\gitleaks-$version"
}
$destination = [IO.Path]::GetFullPath($DestinationDirectory)
$executable = Join-Path $destination 'gitleaks.exe'
if (Test-Path -LiteralPath $executable) {
    $reported = & $executable version
    if ($LASTEXITCODE -eq 0 -and $reported -match [regex]::Escape($version)) {
        Write-Output $executable
        exit 0
    }
}

$archive = Join-Path ([IO.Path]::GetTempPath()) $archiveName
try {
    Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/gitleaks/gitleaks/releases/download/v$version/$archiveName" `
        -OutFile $archive -Headers @{ 'User-Agent' = 'AudioSourceMixer-release-audit' }
    $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    if ($actual -ne $expectedSha256) { throw "Gitleaks archive SHA-256 mismatch: $actual" }
    if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Expand-Archive -LiteralPath $archive -DestinationPath $destination -Force
    if (-not (Test-Path -LiteralPath $executable)) { throw 'The verified archive did not contain gitleaks.exe.' }
    Write-Output $executable
} finally {
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
}
