# Redraws the application icon.
#
# The icon is a drawing rather than a picture, so it is kept as the code that produces it and
# the .ico is a build product that happens to be committed: the shape can be adjusted, and every
# size redrawn from the same description, without anyone opening an image editor.
#
#   pwsh tools/generate-icon.ps1
#
# Writes JsonToolbox.App/Assets/icon.ico (all sizes) and icon.png (256 px, for anything that
# cannot read an .ico).

[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\JsonToolbox.App\Assets')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedPath([float] $x, [float] $y, [float] $w, [float] $h, [float] $r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $path
}

function New-IconBitmap([int] $size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'

    $s = [float] $size
    # A hair of inset, so the rounded corners are not clipped by the edge of the bitmap.
    $inset = $s * 0.02
    $box = $s - ($inset * 2)

    $path = New-RoundedPath $inset $inset $box $box ($s * 0.225)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)),
        (New-Object System.Drawing.Point($size, $size)),
        [System.Drawing.Color]::FromArgb(255, 92, 133, 250),
        [System.Drawing.Color]::FromArgb(255, 42, 87, 201))
    $g.FillPath($brush, $path)

    # The braces are strokes rather than a typeface: a glyph shrunk to 16 px turns to grey mush,
    # where a stroke whose width is a fraction of the icon stays legible at every size. For the
    # same reason the small sizes get a proportionally heavier pen.
    $weight = if ($size -lt 32) { 0.095 } else { 0.075 }
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [float] ($s * $weight))
    $pen.StartCap = 'Round'
    $pen.EndCap = 'Round'
    $pen.LineJoin = 'Round'

    $cy = $s * 0.5          # the centre, in both directions
    $half = $s * 0.235      # half the height of a brace
    $arm = $s * 0.10        # how far the arms reach in from the spine
    $waist = $s * 0.055     # how far the cusp reaches out from it

    foreach ($side in @(-1, 1)) {
        $spine = $cy + ($side * $s * 0.22)
        $armEnd = $spine - ($side * $arm)
        $cusp = $spine + ($side * $waist)

        $brace = New-Object System.Drawing.Drawing2D.GraphicsPath
        $brace.AddBezier(
            (New-Object System.Drawing.PointF($armEnd, ($cy - $half))),
            (New-Object System.Drawing.PointF($spine, ($cy - $half + $s * 0.02))),
            (New-Object System.Drawing.PointF($spine, ($cy - $half * 0.45))),
            (New-Object System.Drawing.PointF($spine, ($cy - $half * 0.30))))
        $brace.AddBezier(
            (New-Object System.Drawing.PointF($spine, ($cy - $half * 0.30))),
            (New-Object System.Drawing.PointF($spine, ($cy - $s * 0.03))),
            (New-Object System.Drawing.PointF($cusp, ($cy - $s * 0.02))),
            (New-Object System.Drawing.PointF($cusp, $cy)))
        $brace.AddBezier(
            (New-Object System.Drawing.PointF($cusp, $cy)),
            (New-Object System.Drawing.PointF($cusp, ($cy + $s * 0.02))),
            (New-Object System.Drawing.PointF($spine, ($cy + $s * 0.03))),
            (New-Object System.Drawing.PointF($spine, ($cy + $half * 0.30))))
        $brace.AddBezier(
            (New-Object System.Drawing.PointF($spine, ($cy + $half * 0.30))),
            (New-Object System.Drawing.PointF($spine, ($cy + $half * 0.45))),
            (New-Object System.Drawing.PointF($spine, ($cy + $half - $s * 0.02))),
            (New-Object System.Drawing.PointF($armEnd, ($cy + $half))))
        $g.DrawPath($pen, $brace)
        $brace.Dispose()
    }

    # One value between the braces. Three dots would say "JSON" more loudly and blur into a
    # smudge at 16 px, which is the size that decides whether an icon is recognised at all.
    $dot = $s * $(if ($size -lt 32) { 0.135 } else { 0.105 })
    $g.FillEllipse([System.Drawing.Brushes]::White, ($cy - $dot / 2), ($cy - $dot / 2), $dot, $dot)

    $g.Dispose()
    $pen.Dispose()
    $brush.Dispose()
    $path.Dispose()
    $bmp
}

$directory = (Resolve-Path $OutputDirectory).Path
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$images = @()

foreach ($size in $sizes) {
    $bmp = New-IconBitmap $size
    $buffer = New-Object System.IO.MemoryStream
    $bmp.Save($buffer, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += , @($size, $buffer.ToArray())

    if ($size -eq 256) {
        $bmp.Save((Join-Path $directory 'icon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    }

    $bmp.Dispose()
    $buffer.Dispose()
}

# An .ico is a directory of images: the header, one 16-byte entry per size, then the payloads.
# Every size is stored as a PNG, which Windows has understood since Vista and which keeps the
# 256 px image from costing a quarter of a megabyte.
$stream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($stream)

$writer.Write([UInt16] 0)                 # reserved
$writer.Write([UInt16] 1)                 # 1 = icon
$writer.Write([UInt16] $images.Count)

$offset = 6 + (16 * $images.Count)
foreach ($image in $images) {
    $size = $image[0]
    $data = $image[1]

    # 256 is written as 0: the field is one byte, and 256 does not fit in it.
    $dimension = [byte] $(if ($size -ge 256) { 0 } else { $size })
    $writer.Write($dimension)             # width
    $writer.Write($dimension)             # height
    $writer.Write([byte] 0)               # colours in the palette; 0 for truecolour
    $writer.Write([byte] 0)               # reserved
    $writer.Write([UInt16] 1)             # colour planes
    $writer.Write([UInt16] 32)            # bits per pixel
    $writer.Write([UInt32] $data.Length)
    $writer.Write([UInt32] $offset)
    $offset += $data.Length
}

foreach ($image in $images) {
    $writer.Write($image[1])
}

$writer.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $directory 'icon.ico'), $stream.ToArray())
$writer.Dispose()
$stream.Dispose()

Write-Output "Wrote icon.ico ($($sizes -join ', ') px) and icon.png to $directory"
