. (Join-Path $PSScriptRoot 'common.ps1')

function Get-RuntimeAllowlist {
    $path = Join-Path (Get-RepositoryRoot) 'packaging\runtime-allowlist.json'
    return Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
}

function Get-PayloadInventory([string] $Directory) {
    $resolved = [IO.Path]::GetFullPath($Directory).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $resolved)) { return @() }
    return @(Get-ChildItem -LiteralPath $resolved -File -Recurse | ForEach-Object {
        [ordered]@{
            path = $_.FullName.Substring($resolved.Length + 1).Replace('\', '/')
            size = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    } | Sort-Object path)
}

function Get-ExpectedPayloadPaths([ValidateSet('Portable','InstallerPayload','Installed')][string] $Mode) {
    $allowlist = Get-RuntimeAllowlist
    $paths = @($allowlist.runtimeFiles.path)
    if ($Mode -eq 'Portable') { $paths += @($allowlist.portableOnlyFiles.path) }
    if ($Mode -eq 'Installed') { $paths += @($allowlist.installerGeneratedFiles.path) }
    return @($paths | ForEach-Object { ([string]$_).Replace('\', '/') } | Sort-Object -Unique)
}

function Assert-ExtensionRuntimeGraph([string] $PayloadDirectory) {
    $extensionRoot = Join-Path $PayloadDirectory 'BrowserExtension'
    $manifestPath = Join-Path $extensionRoot 'manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.manifest_version -ne 3) { throw 'Runtime extension is not Manifest V3.' }
    $queue = [Collections.Generic.Queue[string]]::new()
    $reachable = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $queue.Enqueue('manifest.json')
    $queue.Enqueue([string]$manifest.background.service_worker)
    $queue.Enqueue([string]$manifest.options_ui.page)
    # The MV3 offscreen document is opened dynamically through chrome.offscreen.createDocument,
    # so it is a runtime root even though manifest.json cannot declare it.
    $queue.Enqueue('offscreen/offscreen.html')
    if ($null -ne $manifest.icons) {
        foreach ($property in $manifest.icons.PSObject.Properties) { $queue.Enqueue([string]$property.Value) }
    }
    if ($null -ne $manifest.action.default_icon) {
        foreach ($property in $manifest.action.default_icon.PSObject.Properties) { $queue.Enqueue([string]$property.Value) }
    }

    while ($queue.Count -gt 0) {
        $relative = $queue.Dequeue().Replace('/', '\')
        if (-not $reachable.Add($relative)) { continue }
        $path = Join-Path $extensionRoot $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Extension runtime reference is missing: $relative" }
        $extension = [IO.Path]::GetExtension($path).ToLowerInvariant()
        if ($extension -notin @('.js','.html','.css')) { continue }
        $content = Get-Content -LiteralPath $path -Raw -Encoding UTF8
        $references = @()
        if ($extension -eq '.js') {
            $references += [regex]::Matches($content, '(?:from\s+|import\s*)["'']([^"'']+)["'']') | ForEach-Object { $_.Groups[1].Value }
        } elseif ($extension -eq '.html') {
            $references += [regex]::Matches($content, '(?:src|href)=["'']([^"''#?]+)["'']') | ForEach-Object { $_.Groups[1].Value }
        } else {
            $references += [regex]::Matches($content, 'url\(["'']?([^"'')]+)') | ForEach-Object { $_.Groups[1].Value }
        }
        foreach ($reference in $references) {
            if ($reference -match '^(?:[a-z]+:|#|data:)') { continue }
            $resolved = [IO.Path]::GetFullPath((Join-Path (Split-Path $path) $reference))
            $prefix = [IO.Path]::GetFullPath($extensionRoot).TrimEnd('\') + '\'
            if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Extension reference leaves runtime root: $reference" }
            $queue.Enqueue($resolved.Substring($prefix.Length))
        }
    }

    $actual = @(Get-ChildItem -LiteralPath $extensionRoot -File -Recurse | ForEach-Object {
        $_.FullName.Substring([IO.Path]::GetFullPath($extensionRoot).TrimEnd('\').Length + 1)
    } | Sort-Object)
    $difference = @(Compare-Object @($reachable | Sort-Object) $actual)
    if ($difference.Count -ne 0) { throw "Extension runtime contains missing or orphan files: $($difference | Out-String)" }
}

function Assert-RuntimePayload([string] $Directory,
    [ValidateSet('Portable','InstallerPayload','Installed')][string] $Mode) {
    $actual = @(Get-PayloadInventory $Directory)
    $expected = @(Get-ExpectedPayloadPaths $Mode)
    $difference = @(Compare-Object $expected @($actual.path))
    if ($difference.Count -ne 0) { throw "$Mode payload differs from allowlist: $($difference | Out-String)" }

    foreach ($entry in $actual) {
        $relative = [string]$entry.path
        if ($relative -match '(^|/)(docs|tests|tools|diagnostics)(/|$)' -or
            $relative -match '(^|/)package\.json$' -or
            $relative -match '\.(pdb|obj|iobj|ipdb|tlog|recipe|cs|csproj|sln|map)$' -or
            $relative -match '(^|/)AudioSourceMixer-0\.[01]\.') {
            throw "Development or obsolete file entered $Mode payload: $relative"
        }
        if ($relative.EndsWith('.md', [StringComparison]::OrdinalIgnoreCase) -and
            $relative -notin @('THIRD_PARTY_NOTICES.md','USER_GUIDE.md')) {
            throw "Unapproved Markdown file entered $Mode payload: $relative"
        }
    }

    $root = Get-RepositoryRoot
    $textExtensions = @('.json','.js','.html','.css','.md','.ps1','.txt')
    foreach ($entry in $actual) {
        $path = Join-Path $Directory ([string]$entry.path)
        if ([IO.Path]::GetExtension($path).ToLowerInvariant() -notin $textExtensions) { continue }
        if ($Mode -eq 'Installed' -and [string]$entry.path -in @('native-host-manifest.json','install-identity.json')) { continue }
        $content = Get-Content -LiteralPath $path -Raw -Encoding UTF8
        foreach ($needle in @($root, '/mnt/data', '\\tests\\', '\\src\\')) {
            if ($content.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                throw "Build-machine or test path '$needle' leaked into $Mode payload file $($entry.path)."
            }
        }
        if ($content -match 'C:\\Users\\[^\\]+\\Documents\\audio-control') {
            throw "Build-machine user path leaked into $Mode payload file $($entry.path)."
        }
    }
    Assert-ExtensionRuntimeGraph $Directory
    return $actual
}
