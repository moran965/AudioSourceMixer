$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$root = Get-RepositoryRoot
$source = Join-Path $root 'assets\product-icon.svg'
$generated = Join-Path $root 'assets\generated'
$extensionAssets = Join-Path $root 'src\AudioSourceMixer.BrowserExtension\assets'
if (-not (Test-Path -LiteralPath $source)) { throw "Editable icon source is missing: $source" }
New-Item -ItemType Directory -Path $generated,$extensionAssets -Force | Out-Null
Add-Type -AssemblyName System.Drawing

[xml]$iconSource = [IO.File]::ReadAllText($source, [Text.Encoding]::UTF8)
$namespaces = [Xml.XmlNamespaceManager]::new($iconSource.NameTable)
$namespaces.AddNamespace('svg', 'http://www.w3.org/2000/svg')
$backgroundNode = $iconSource.SelectSingleNode('/svg:svg/svg:rect', $namespaces)
$lineGroup = $iconSource.SelectSingleNode('/svg:svg/svg:g[@stroke and not(@fill)]', $namespaces)
$knobGroup = $iconSource.SelectSingleNode('/svg:svg/svg:g[@fill and @stroke]', $namespaces)
if ($null -eq $backgroundNode -or $null -eq $lineGroup -or $null -eq $knobGroup) {
    throw 'product-icon.svg does not match the supported mixer icon structure.'
}

function Get-Number($Node, [string] $Name) { return [double]::Parse($Node.GetAttribute($Name), [Globalization.CultureInfo]::InvariantCulture) }
function New-RoundedRectanglePath([double] $X, [double] $Y, [double] $Width, [double] $Height, [double] $Radius) {
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = [Math]::Min([Math]::Min($Radius * 2, $Width), $Height)
    if ($diameter -le 0) { $path.AddRectangle([Drawing.RectangleF]::new($X, $Y, $Width, $Height)); return $path }
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-MixerIconPng([int] $Size) {
    $renderSize = if ($Size -le 128) { $Size * 4 } else { $Size }
    $bitmap = [Drawing.Bitmap]::new($renderSize, $renderSize, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::GammaCorrected
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([Drawing.Color]::Transparent)
        $scale = $renderSize / 256.0
        $path = New-RoundedRectanglePath ((Get-Number $backgroundNode 'x')*$scale) ((Get-Number $backgroundNode 'y')*$scale) `
            ((Get-Number $backgroundNode 'width')*$scale) ((Get-Number $backgroundNode 'height')*$scale) ((Get-Number $backgroundNode 'rx')*$scale)
        try {
            $background = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($backgroundNode.GetAttribute('fill')))
            try { $graphics.FillPath($background, $path) } finally { $background.Dispose() }
        } finally { $path.Dispose() }
        $line = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml($lineGroup.GetAttribute('stroke')), (Get-Number $lineGroup 'stroke-width')*$scale)
        $line.StartCap = $line.EndCap = [Drawing.Drawing2D.LineCap]::Round
        try {
            foreach ($lineNode in $lineGroup.SelectNodes('./svg:path', $namespaces)) {
                if ($lineNode.GetAttribute('d') -notmatch '^M([0-9.]+) ([0-9.]+)v([0-9.]+)$') { throw "Unsupported SVG line path: $($lineNode.GetAttribute('d'))" }
                $x=[double]$Matches[1]*$scale; $y=[double]$Matches[2]*$scale; $length=[double]$Matches[3]*$scale
                $graphics.DrawLine($line, $x, $y, $x, $y+$length)
            }
        }
        finally { $line.Dispose() }
        $knobBrush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($knobGroup.GetAttribute('fill')))
        $knobPen = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml($knobGroup.GetAttribute('stroke')), (Get-Number $knobGroup 'stroke-width')*$scale)
        try {
            foreach ($knobNode in $knobGroup.SelectNodes('./svg:rect', $namespaces)) {
                $knobPath = New-RoundedRectanglePath ((Get-Number $knobNode 'x')*$scale) ((Get-Number $knobNode 'y')*$scale) `
                    ((Get-Number $knobNode 'width')*$scale) ((Get-Number $knobNode 'height')*$scale) ((Get-Number $knobNode 'rx')*$scale)
                try { $graphics.FillPath($knobBrush, $knobPath); $graphics.DrawPath($knobPen, $knobPath) } finally { $knobPath.Dispose() }
            }
        } finally { $knobBrush.Dispose(); $knobPen.Dispose() }

        $outputBitmap = $bitmap
        if ($renderSize -ne $Size) {
            $outputBitmap = [Drawing.Bitmap]::new($Size, $Size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $downsample = [Drawing.Graphics]::FromImage($outputBitmap)
            try {
                $downsample.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::GammaCorrected
                $downsample.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $downsample.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::Half
                $downsample.DrawImage($bitmap, [Drawing.Rectangle]::new(0,0,$Size,$Size), 0,0,$renderSize,$renderSize,[Drawing.GraphicsUnit]::Pixel)
            } finally { $downsample.Dispose() }
        }
        try {
            $stream = [IO.MemoryStream]::new()
            try { $outputBitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png); return $stream.ToArray() }
            finally { $stream.Dispose() }
        } finally { if ($outputBitmap -ne $bitmap) { $outputBitmap.Dispose() } }
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
}

$sizes = @(16,20,24,32,40,48,64,96,128,256)
$images = [Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) {
    $bytes = New-MixerIconPng $size
    $images.Add($bytes)
    if ($size -in @(16,32,48,128)) { [IO.File]::WriteAllBytes((Join-Path $extensionAssets "icon-$size.png"), $bytes) }
}
[IO.File]::WriteAllBytes((Join-Path $generated 'AudioSourceMixer-page.png'), (New-MixerIconPng 512))
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
