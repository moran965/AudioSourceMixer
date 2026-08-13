$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$root = Get-RepositoryRoot
$source = Join-Path $root 'assets\product-icon.svg'
$generated = Join-Path $root 'assets\generated'
$extensionAssets = Join-Path $root 'src\AudioSourceMixer.BrowserExtension\assets'
if (-not (Test-Path -LiteralPath $source)) { throw "Editable icon source is missing: $source" }
New-Item -ItemType Directory -Path $generated,$extensionAssets -Force | Out-Null
Add-Type -AssemblyName System.Drawing

function New-MixerIconPng([int] $Size) {
    $bitmap = [Drawing.Bitmap]::new($Size, $Size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([Drawing.Color]::Transparent)
        $scale = $Size / 256.0
        $path = [Drawing.Drawing2D.GraphicsPath]::new()
        try {
            $radius = 54 * $scale; $diameter = $radius * 2; $left = 8 * $scale; $top = 8 * $scale; $width = 240 * $scale
            $path.AddArc($left, $top, $diameter, $diameter, 180, 90)
            $path.AddArc($left + $width - $diameter, $top, $diameter, $diameter, 270, 90)
            $path.AddArc($left + $width - $diameter, $top + $width - $diameter, $diameter, $diameter, 0, 90)
            $path.AddArc($left, $top + $width - $diameter, $diameter, $diameter, 90, 90)
            $path.CloseFigure()
            $background = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255,15,23,42))
            try { $graphics.FillPath($background, $path) } finally { $background.Dispose() }
        } finally { $path.Dispose() }
        $line = [Drawing.Pen]::new([Drawing.Color]::FromArgb(255,248,250,252), [Math]::Max(1.5, 18 * $scale))
        $line.StartCap = $line.EndCap = [Drawing.Drawing2D.LineCap]::Round
        try { foreach ($x in @(70,128,186)) { $graphics.DrawLine($line, $x*$scale, 48*$scale, $x*$scale, 208*$scale) } }
        finally { $line.Dispose() }
        $knob = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255,34,211,238))
        try { foreach ($item in @(@(45,78),@(103,145),@(161,101))) { $graphics.FillRectangle($knob, $item[0]*$scale, $item[1]*$scale, 50*$scale, 34*$scale) } }
        finally { $knob.Dispose() }
        $stream = [IO.MemoryStream]::new()
        try { $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png); return $stream.ToArray() }
        finally { $stream.Dispose() }
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
}

$sizes = @(16,32,48,128,256)
$images = [Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) {
    $bytes = New-MixerIconPng $size
    $images.Add($bytes)
    if ($size -in @(16,32,48,128)) { [IO.File]::WriteAllBytes((Join-Path $extensionAssets "icon-$size.png"), $bytes) }
}
$icoPath = Join-Path $generated 'AudioSourceMixer.ico'
$stream = [IO.File]::Create($icoPath)
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + (16 * $sizes.Count)
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $size = $sizes[$index]; $bytes = $images[$index]
        $dimension = if ($size -eq 256) { 0 } else { $size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$bytes.Length); $writer.Write([uint32]$offset); $offset += $bytes.Length
    }
    foreach ($bytes in $images) { $writer.Write($bytes) }
} finally { $writer.Dispose(); $stream.Dispose() }
Write-Output "Generated product icon: $icoPath"
