$ErrorActionPreference = 'Stop'

function Get-RepositoryRoot {
    return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
}

function Get-DotnetExecutable {
    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($env:DOTNET_ROOT)) { $candidates += (Join-Path $env:DOTNET_ROOT 'dotnet.exe') }
    if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) { $candidates += (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe') }
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -ne $command) { $candidates += $command.Source }
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $candidate)) { continue }
        $sdks = & $candidate --list-sdks
        if ($LASTEXITCODE -eq 0 -and $sdks) { return $candidate }
    }
    throw 'A .NET 8 SDK could not be found. Install the .NET 8 SDK or set DOTNET_ROOT.'
}

function Get-ProductVersion {
    $props = Join-Path (Get-RepositoryRoot) 'Directory.Build.props'
    [xml] $document = Get-Content -LiteralPath $props
    $node = $document.SelectSingleNode('//AudioSourceMixerVersion')
    if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
        throw "AudioSourceMixerVersion is missing from $props"
    }
    return $node.InnerText.Trim()
}

function Get-Sha256([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "Cannot hash missing file: $Path" }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Invoke-UiSmokeTest([string] $Executable, [string] $Description, [int] $TimeoutMilliseconds = 60000) {
    if (-not (Test-Path -LiteralPath $Executable)) { throw "$Description executable is missing: $Executable" }
    $process = Start-Process -FilePath $Executable -ArgumentList '--ui-smoke-test' -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit($TimeoutMilliseconds)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw "$Description timed out after $TimeoutMilliseconds ms."
    }
    if ($process.ExitCode -ne 0) { throw "$Description failed with exit code $($process.ExitCode)." }
    Write-Output "$Description passed with exit code 0."
}

function Assert-PathInsideRepository([string] $Path) {
    $root = (Get-RepositoryRoot).TrimEnd('\') + '\'
    $resolved = [System.IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the repository: $resolved"
    }
}

function Invoke-Checked([scriptblock] $Command, [string] $Description) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Description failed with exit code $LASTEXITCODE." }
}
