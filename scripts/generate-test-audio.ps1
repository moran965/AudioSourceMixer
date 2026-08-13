param([string] $OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'tests\audio'))
$ErrorActionPreference = 'Stop'
$sampleRate = 48000
$duration = 1.0
$samples = [int]($sampleRate * $duration)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

function Write-Wave([string] $Name, [int] $Channels, [scriptblock] $SampleFactory) {
    $path = Join-Path $OutputDirectory $Name
    $stream = [System.IO.File]::Create($path)
    $writer = [System.IO.BinaryWriter]::new($stream)
    try {
        $bits = 16; $dataSize = $samples * $Channels * 2
        $writer.Write([System.Text.Encoding]::ASCII.GetBytes('RIFF')); $writer.Write(36 + $dataSize)
        $writer.Write([System.Text.Encoding]::ASCII.GetBytes('WAVEfmt ')); $writer.Write(16); $writer.Write([int16]1)
        $writer.Write([int16]$Channels); $writer.Write($sampleRate); $writer.Write($sampleRate * $Channels * 2)
        $writer.Write([int16]($Channels * 2)); $writer.Write([int16]$bits)
        $writer.Write([System.Text.Encoding]::ASCII.GetBytes('data')); $writer.Write($dataSize)
        for ($index = 0; $index -lt $samples; $index++) {
            $values = & $SampleFactory $index $sampleRate
            foreach ($value in $values) { $writer.Write([int16]([Math]::Round([Math]::Max(-1, [Math]::Min(1, $value)) * 12000))) }
        }
    } finally { $writer.Dispose(); $stream.Dispose() }
}

Write-Wave 'stereo-440-left-880-right.wav' 2 { param($i,$rate) @([Math]::Sin(2*[Math]::PI*440*$i/$rate), [Math]::Sin(2*[Math]::PI*880*$i/$rate)) }
Write-Wave 'left-only.wav' 2 { param($i,$rate) @([Math]::Sin(2*[Math]::PI*440*$i/$rate), 0) }
Write-Wave 'right-only.wav' 2 { param($i,$rate) @(0, [Math]::Sin(2*[Math]::PI*880*$i/$rate)) }
Write-Wave 'mono-440.wav' 1 { param($i,$rate) @([Math]::Sin(2*[Math]::PI*440*$i/$rate)) }
Write-Wave 'silence.wav' 2 { param($i,$rate) @(0,0) }
Write-Wave 'short-loop.wav' 2 { param($i,$rate) $tone = [Math]::Sin(2*[Math]::PI*660*$i/$rate); @($tone,$tone) }
Write-Output "Generated test audio in $OutputDirectory"
