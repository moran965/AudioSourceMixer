$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$root = Get-RepositoryRoot
$required = @(
    'README.md','README.zh-CN.md','LICENSE','THIRD_PARTY_NOTICES.md','CHANGELOG.md','CODE_SIGNING_POLICY.md',
    'CONTRIBUTING.md','CONTRIBUTING.zh-CN.md','SECURITY.md','CODE_OF_CONDUCT.md','SUPPORT.md',
    'AI_DEVELOPMENT.md','docs/privacy.md','docs/privacy.zh-CN.md','docs/architecture.md',
    'docs/building.md','docs/releasing.md','.gitattributes','.editorconfig',
    '.github/workflows/ci.yml','.github/workflows/release.yml','.gitleaks.toml','.gitleaksignore'
)
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $relative) -PathType Leaf)) {
        throw "Required open-source file is missing: $relative"
    }
}

$version = Get-ProductVersion
if ($version -ne '1.0.0') { throw "Unexpected product version: $version" }
$manifest = Get-Content -LiteralPath (Join-Path $root 'src\AudioSourceMixer.BrowserExtension\manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ([string]$manifest.version -ne $version) { throw "Extension version $($manifest.version) does not match $version." }

Push-Location $root
try {
    $tracked = @(git -c safe.directory=$root ls-files)
    if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed.' }
    $forbidden = $tracked | Where-Object {
        $_ -match '(^|/)(artifacts|staging|bin|obj|TestResults|node_modules|\.vs|\.idea|browser-profile[^/]*)/' -or
        $_ -match '\.(exe|msi|pdb|pfx|p12|pem|key|log|dmp)$'
    }
    if ($forbidden) { throw "Forbidden generated/private files are tracked: $($forbidden -join ', ')" }

    $productionProjects = Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter *.csproj -Recurse
    foreach ($project in $productionProjects) {
        [xml]$xml = Get-Content -LiteralPath $project.FullName
        if ($xml.SelectNodes('//PackageReference').Count -ne 0) {
            throw "Production project has an unreviewed PackageReference: $($project.FullName)"
        }
    }
    $allowedTestPackages = @(
        'coverlet.collector@6.0.0','Microsoft.NET.Test.Sdk@17.8.0',
        'xunit@2.5.3','xunit.runner.visualstudio@2.5.3'
    )
    foreach ($project in Get-ChildItem -LiteralPath (Join-Path $root 'tests') -Filter *.csproj -Recurse) {
        [xml]$xml = Get-Content -LiteralPath $project.FullName
        foreach ($reference in $xml.SelectNodes('//PackageReference')) {
            $identity = "$($reference.Include)@$($reference.Version)"
            if ($identity -notin $allowedTestPackages) { throw "Unreviewed test dependency: $identity" }
        }
    }

    $absoluteMatches = @(git -c safe.directory=$root grep -I -n -E '[A-Za-z]:\\Users\\|/Users/|/home/' -- 2>$null)
    $absoluteMatches = $absoluteMatches | Where-Object {
        $_ -notmatch '^scripts/(runtime-payload|audit-repository)\.ps1:'
    }
    if ($absoluteMatches) { throw "Tracked text contains a user-specific absolute path: $($absoluteMatches -join '; ')" }

    $workflows = Get-ChildItem -LiteralPath (Join-Path $root '.github\workflows') -Filter *.yml
    foreach ($workflow in $workflows) {
        $unfixed = Get-Content -LiteralPath $workflow.FullName | Where-Object {
            $_ -match '^\s*-?\s*uses:\s*([^\s#]+)' -and $Matches[1] -notmatch '^\./' -and $Matches[1] -notmatch '@[0-9a-f]{40}$'
        }
        if ($unfixed) { throw "Workflow action is not pinned to a full commit SHA in $($workflow.Name): $($unfixed -join '; ')" }
    }

    foreach ($readme in 'README.md','README.zh-CN.md') {
        $content = Get-Content -LiteralPath (Join-Path $root $readme) -Raw -Encoding UTF8
        if ($content -notmatch 'vibe coding') { throw "$readme is missing the required vibe coding disclosure." }
        if ($content -notmatch '1\.0\.0') { throw "$readme does not identify version 1.0.0." }
        if ($content -notmatch 'NotSigned' -or $content -notmatch 'Artifact Attestation') {
            throw "$readme does not transparently describe the v1.0.0 unsigned fallback and provenance boundary."
        }
    }

    $testing = Get-Content -LiteralPath (Join-Path $root 'docs\testing.md') -Raw -Encoding UTF8
    foreach ($expected in '151/151','56/56','23FFE8CF79FBF09033CE4E2AA8015C01A83D25446566D0D57FA94FFA2E8A1EBF','3C9E1E88920E76F1D71B5D6FF465C52264F9DF09A2F8033F960445F1524B92F2','2026-08-25') {
        if ($testing -notmatch [regex]::Escape($expected)) { throw "docs/testing.md is missing current QA evidence: $expected" }
    }
    foreach ($obsolete in '48/48','DEEEA7F9959B91AC8EFC3A0599A75A623773E6D7945FC46CC4BBFA1672EC932A','F76CF0018B0D951CE36FE76942494451E2F0A4395588C1600F08DA82713021E7') {
        if ($testing -match [regex]::Escape($obsolete)) { throw "docs/testing.md still contains obsolete QA evidence: $obsolete" }
    }

    $releaseWorkflow = Get-Content -LiteralPath (Join-Path $root '.github\workflows\release.yml') -Raw -Encoding UTF8
    if ($releaseWorkflow -match '(?m)^\s{2}push:') { throw 'Release workflow must not publish from a tag push.' }
    foreach ($requiredReleaseToken in 'workflow_dispatch:','hardware_gate_attested:','signing_mode:','allow_unsigned_release:','unsigned_risk_ack:','PUBLISH UNSIGNED V1.0.0','concurrency:','actions/attest@') {
        if ($releaseWorkflow -notmatch [regex]::Escape($requiredReleaseToken)) { throw "Release workflow is missing: $requiredReleaseToken" }
    }

    $releaseNotesScript = Join-Path $root 'scripts\prepare-release-notes.ps1'
    $releaseNotesScriptText = [IO.File]::ReadAllText($releaseNotesScript, [Text.Encoding]::UTF8)
    if ($releaseNotesScriptText -match '[^\x00-\x7F]') {
        throw 'prepare-release-notes.ps1 must remain ASCII-only for Windows PowerShell 5.1 compatibility.'
    }
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $releaseNotesAuditDirectory = Join-Path $tempRoot ('AudioSourceMixer-release-notes-audit-' + [guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($releaseNotesAuditDirectory) | Out-Null
    try {
        $releaseNotesManifest = Join-Path $releaseNotesAuditDirectory 'manifest.json'
        $releaseNotesOutput = Join-Path $releaseNotesAuditDirectory 'release-notes.md'
        $auditManifest = [pscustomobject]@{
            installer = [pscustomobject]@{
                authenticodeStatus = 'NotSigned'
                signerSubject = $null
                timestampSubject = $null
                sha256 = ('A' * 64)
                size = 1
            }
        }
        [IO.File]::WriteAllText($releaseNotesManifest, ($auditManifest | ConvertTo-Json -Depth 3), [Text.UTF8Encoding]::new($false))
        & $releaseNotesScript -SigningMode unsigned -Tag "v$version" -Commit ('a' * 40) -ManifestPath $releaseNotesManifest -BaseNotesPath (Join-Path $root "docs\release-notes-$version.md") -OutputPath $releaseNotesOutput | Out-Null
        $generatedReleaseNotes = Get-Content -LiteralPath $releaseNotesOutput -Raw -Encoding UTF8
        $expectedEncodingFragments = @(
            '5pyq562+5ZCN5a6J6KOF56iL5bqP',
            '5q2kIHYxLjAuMCDlronoo4XnqIvluo/msqHmnIkgQXV0aGVudGljb2RlIOWPkeW4g+iAheOAgg==',
            '5Y+R6KGM6K+B5o2u',
            'bm9uZSAvIOaXoA=='
        ) | ForEach-Object { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($_)) }
        foreach ($expectedEncodingFragment in $expectedEncodingFragments) {
            if (-not $generatedReleaseNotes.Contains($expectedEncodingFragment)) {
                throw 'Generated release notes do not contain the expected UTF-8 Chinese text.'
            }
        }
        if ($generatedReleaseNotes -match '[\u00E3\u00E5-\u00E8\uFFFD]') {
            throw 'Generated release notes contain likely mojibake.'
        }
    } finally {
        $resolvedAuditDirectory = [IO.Path]::GetFullPath($releaseNotesAuditDirectory)
        if (-not $resolvedAuditDirectory.StartsWith($tempRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean unexpected release-notes audit path: $resolvedAuditDirectory"
        }
        if ([IO.Directory]::Exists($resolvedAuditDirectory)) { [IO.Directory]::Delete($resolvedAuditDirectory, $true) }
    }

    $signingPolicy = Get-Content -LiteralPath (Join-Path $root 'CODE_SIGNING_POLICY.md') -Raw -Encoding UTF8
    foreach ($requiredPolicyText in 'Free code signing provided by SignPath.io, certificate by SignPath Foundation.','https://github.com/moran965/AudioSourceMixer','@moran965','docs/privacy.md','human') {
        if ($signingPolicy -notmatch [regex]::Escape($requiredPolicyText)) { throw "Code signing policy is missing: $requiredPolicyText" }
    }

    $audioFixtures = @(git -c safe.directory=$root ls-files 'tests/audio/*.wav')
    if ($audioFixtures.Count -ne 1 -or $audioFixtures[0] -ne 'tests/audio/short-loop.wav') {
        throw "Only the referenced synthetic short-loop.wav fixture may be tracked: $($audioFixtures -join ', ')"
    }
    if (Test-Path -LiteralPath (Join-Path $root 'src\AudioSourceMixer.BrowserExtension\diagnostics\runtime-probe.html')) {
        throw 'The unreferenced browser runtime-probe page must not be present.'
    }
    git -c safe.directory=$root diff --check
    if ($LASTEXITCODE -ne 0) { throw 'git diff --check failed.' }
} finally { Pop-Location }

Write-Output "Repository audit passed for Audio Source Mixer $version."
