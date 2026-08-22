$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$root = Get-RepositoryRoot
$required = @(
    'README.md','README.zh-CN.md','LICENSE','THIRD_PARTY_NOTICES.md','CHANGELOG.md',
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
    }
    git -c safe.directory=$root diff --check
    if ($LASTEXITCODE -ne 0) { throw 'git diff --check failed.' }
} finally { Pop-Location }

Write-Output "Repository audit passed for Audio Source Mixer $version."
