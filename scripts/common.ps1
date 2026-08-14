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

function Invoke-LiveMeterUiTest(
    [string] $Executable,
    [string] $Configuration,
    [string] $ReportPath,
    [string] $Description,
    [int] $TimeoutMilliseconds = 60000) {
    if (-not (Test-Path -LiteralPath $Executable)) { throw "$Description executable is missing: $Executable" }
    $root = Get-RepositoryRoot
    $player = Join-Path $root "src\AudioSourceMixer.CapabilityProbe\bin\$Configuration\net8.0-windows\AudioSourceMixer.CapabilityProbe.exe"
    $wave = Join-Path $root 'tests\audio\short-loop.wav'
    if (-not (Test-Path -LiteralPath $player)) { throw "$Description player is missing: $player" }
    if (-not (Test-Path -LiteralPath $wave)) { throw "$Description wave file is missing: $wave" }
    $report = [IO.Path]::GetFullPath($ReportPath)
    $reportDirectory = Split-Path $report -Parent
    if (-not (Test-Path -LiteralPath $reportDirectory)) { New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null }
    if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report -Force }

    $playerProcess = $null
    $meterProcess = $null
    try {
        $playerProcess = Start-Process -FilePath $player -ArgumentList @('--play-wav',('"' + $wave + '"'),'5') -WindowStyle Hidden -PassThru
        Start-Sleep -Milliseconds 300
        if ($playerProcess.HasExited) { throw "$Description test player exited before the meter probe started." }
        $meterArguments = @(
            '--ui-live-meter-pid', [string]$playerProcess.Id,
            '--ui-live-meter-duration', '8',
            '--ui-live-meter-report', ('"' + $report + '"'))
        $meterProcess = Start-Process -FilePath $Executable -ArgumentList $meterArguments -WindowStyle Hidden -PassThru
        if (-not $meterProcess.WaitForExit($TimeoutMilliseconds)) {
            Stop-Process -Id $meterProcess.Id -Force -ErrorAction SilentlyContinue
            throw "$Description timed out after $TimeoutMilliseconds ms."
        }
        if ($meterProcess.ExitCode -ne 0) { throw "$Description failed with exit code $($meterProcess.ExitCode)." }
        if (-not (Test-Path -LiteralPath $report)) { throw "$Description did not create its sample report: $report" }
        $result = Get-Content -LiteralPath $report -Raw -Encoding UTF8 | ConvertFrom-Json
        if ([double]$result.maximumRawPeak -le 0.001 -or [double]$result.maximumSmoothedPeak -le 0.001 -or
            [double]$result.maximumIndicatorWidth -le 1 -or -not [bool]$result.returnedToZero) {
            throw "$Description report did not prove a visible live meter and final zero: $report"
        }
        Write-Output ("$Description passed: PID={0}; samples={1}; maxRaw={2:F4}; maxSmoothed={3:F4}; maxIndicator={4:F2}; returnedToZero={5}; report={6}" -f
            $playerProcess.Id, $result.sampleCount, [double]$result.maximumRawPeak, [double]$result.maximumSmoothedPeak,
            [double]$result.maximumIndicatorWidth, [bool]$result.returnedToZero, $report)
    } finally {
        if ($null -ne $meterProcess -and -not $meterProcess.HasExited) { Stop-Process -Id $meterProcess.Id -Force -ErrorAction SilentlyContinue }
        if ($null -ne $playerProcess -and -not $playerProcess.HasExited) {
            if (-not $playerProcess.WaitForExit(3000)) { Stop-Process -Id $playerProcess.Id -Force -ErrorAction SilentlyContinue }
        }
    }
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
