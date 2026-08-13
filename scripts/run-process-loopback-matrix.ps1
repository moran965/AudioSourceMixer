param(
    [Parameter(Mandatory)][uint32] $TargetProcessId,
    [string] $TargetLabel = "process-$TargetProcessId",
    [ValidateRange(5, 60)][int] $DurationSeconds = 5,
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts\diagnostics\process-loopback-matrix'
}
$probe = Join-Path $repositoryRoot 'tools\ProcessLoopbackProbe\x64\Release\ProcessLoopbackProbe.exe'
$stateProbe = Join-Path $repositoryRoot 'src\AudioSourceMixer.CapabilityProbe\bin\Release\net8.0-windows\AudioSourceMixer.CapabilityProbe.exe'
if (-not (Test-Path -LiteralPath $probe)) { throw "Build the x64 Release ProcessLoopbackProbe first: $probe" }
if (-not (Test-Path -LiteralPath $stateProbe)) { throw "Build the Release capability probe first: $stateProbe" }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$safeLabel = $TargetLabel -replace '[^A-Za-z0-9._-]', '-'

function Get-TargetTree([uint32] $RootProcessId) {
    $all = @(Get-CimInstance Win32_Process)
    $ids = [Collections.Generic.HashSet[uint32]]::new()
    [void]$ids.Add($RootProcessId)
    do {
        $before = $ids.Count
        foreach ($process in $all) {
            if ($ids.Contains([uint32]$process.ParentProcessId)) { [void]$ids.Add([uint32]$process.ProcessId) }
        }
    } while ($ids.Count -ne $before)
    return @($all | Where-Object { $ids.Contains([uint32]$_.ProcessId) } |
        Select-Object Name,ProcessId,ParentProcessId,ExecutablePath,CommandLine)
}

$metadataPath = Join-Path $OutputDirectory "$safeLabel-metadata.json"
[ordered]@{
    timestamp = [DateTimeOffset]::Now
    operatingSystem = [Environment]::OSVersion.VersionString
    architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    targetProcessId = $TargetProcessId
    targetLabel = $TargetLabel
    processTreeMode = 'PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE'
    processTree = @(Get-TargetTree $TargetProcessId)
    captureFlags = '0x80060000 (LOOPBACK | EVENTCALLBACK | AUTOCONVERTPCM)'
    streamOptions = [ordered]@{ default = '0x0'; postVolume = '0x8' }
    durationSecondsPerRow = $DurationSeconds
    audibleObservation = 'No microphone acoustic measurement. normal means original endpoint enabled; volume0/mute are objectively applied through ISimpleAudioVolume and logged.'
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $metadataPath -Encoding UTF8

foreach ($stateName in @('normal', 'volume0', 'mute')) {
    $stateOut = Join-Path $OutputDirectory "$safeLabel-$stateName-state.log"
    $stateErr = Join-Path $OutputDirectory "$safeLabel-$stateName-state.err.log"
    $holdSeconds = ($DurationSeconds * 2) + 4
    $stateProcess = Start-Process -FilePath $stateProbe `
        -ArgumentList @('--hold-state', $TargetProcessId, $stateName, $holdSeconds) `
        -RedirectStandardOutput $stateOut -RedirectStandardError $stateErr -WindowStyle Hidden -PassThru
    try {
        $ready = $false
        for ($attempt = 0; $attempt -lt 100; $attempt++) {
            if ((Test-Path -LiteralPath $stateOut) -and
                ((Get-Content -LiteralPath $stateOut -Raw -ErrorAction SilentlyContinue) -match 'STATE_READY')) {
                $ready = $true
                break
            }
            Start-Sleep -Milliseconds 100
        }
        if (-not $ready) { throw "State probe did not become ready for $stateName." }

        foreach ($option in @('none', 'post-volume')) {
            $optionLabel = if ($option -eq 'none') { 'default' } else { 'post' }
            $wave = Join-Path $OutputDirectory "$safeLabel-$optionLabel-$stateName.wav"
            $captureLog = Join-Path $OutputDirectory "$safeLabel-$optionLabel-$stateName.log"
            $captureStdout = "$captureLog.stdout.tmp"
            $captureStderr = "$captureLog.stderr.tmp"
            $captureProcess = Start-Process -FilePath $probe `
                -ArgumentList @($TargetProcessId, 'includetree', $option, $DurationSeconds, $wave) `
                -RedirectStandardOutput $captureStdout -RedirectStandardError $captureStderr `
                -WindowStyle Hidden -Wait -PassThru
            $captureExit = $captureProcess.ExitCode
            for ($flushAttempt = 0; $flushAttempt -lt 20; $flushAttempt++) {
                if ((Test-Path -LiteralPath $captureStdout) -and (Get-Item -LiteralPath $captureStdout).Length -gt 0) { break }
                Start-Sleep -Milliseconds 50
            }
            $captureOutput = @()
            if (Test-Path -LiteralPath $captureStdout) { $captureOutput += Get-Content -LiteralPath $captureStdout }
            if ((Test-Path -LiteralPath $captureStderr) -and (Get-Item -LiteralPath $captureStderr).Length -gt 0) {
                $captureOutput += 'STDERR:'
                $captureOutput += Get-Content -LiteralPath $captureStderr
            }
            @(
                "MATRIX_ROW target=$TargetLabel pid=$TargetProcessId state=$stateName option=$option durationSeconds=$DurationSeconds"
                $captureOutput
                "CAPTURE_EXIT=$captureExit"
            ) | Set-Content -LiteralPath $captureLog -Encoding UTF8
            if (Test-Path -LiteralPath $captureStdout) { Remove-Item -LiteralPath $captureStdout -Force }
            if (Test-Path -LiteralPath $captureStderr) { Remove-Item -LiteralPath $captureStderr -Force }
        }
    }
    finally {
        Wait-Process -Id $stateProcess.Id -Timeout ($holdSeconds + 10) -ErrorAction SilentlyContinue
        if (-not $stateProcess.HasExited) {
            throw "State helper did not exit and restore deterministically for $stateName."
        }
    }
}

Write-Output "Process-loopback matrix complete: $OutputDirectory"
