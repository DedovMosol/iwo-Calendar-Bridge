param(
    [string]$OutDir = ''
)
# Dev-only: draws the product logo and the application icon.
# Idea: two calendar sheets - the source behind, the copy in front - and an arrow from one to the other: the
# program copies a calendar from one mailbox to another. Same family look as other iwo products (blue tile,
# bright rounded shapes), but its own picture.
# Output: docs\images\logo.png (512 px) and src\IwoBridge.Wpf\app.ico (16..256 px, PNG frames, Windows Vista+).
# ASCII-only on purpose (Windows PowerShell 5.1 reads BOM-less UTF-8 as ANSI).
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
if ($OutDir -eq '') { $OutDir = [System.IO.Path]::Combine($root, 'docs', 'images') }

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw-Sheet($g, [float]$x, [float]$y, [float]$w, [float]$h, [float]$s, $pageColor, $bandColor, [bool]$rings) {
    $page = New-RoundRect $x $y $w $h (0.09 * $s)
    $g.FillPath((New-Object System.Drawing.SolidBrush $pageColor), $page)
    # Header band of the sheet.
    $g.SetClip($page)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $bandColor), $x, $y, $w, $h * 0.27)
    $g.ResetClip()
    if ($rings) {
        $ring = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 33, 48, 74))
        foreach ($cx in @(($x + $w * 0.3), ($x + $w * 0.7))) {
            $g.FillPath($ring, (New-RoundRect ($cx - 0.028 * $s) ($y - 0.055 * $s) (0.056 * $s) (0.13 * $s) (0.028 * $s)))
        }
    }
}

function New-Logo([int]$size, [bool]$detailed) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = [float]$size

    # Blue tile with soft diagonal light.
    $tile = New-RoundRect 0 0 ($s - 1) ($s - 1) (0.2 * $s)
    $back = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.RectangleF 0, 0, $s, $s), ([System.Drawing.Color]::FromArgb(255, 30, 136, 229)), ([System.Drawing.Color]::FromArgb(255, 13, 71, 161)), 45.0
    $g.FillPath($back, $tile)

    # Source sheet (behind, translucent) and the copy (in front).
    $sheetW = 0.5 * $s; $sheetH = 0.52 * $s
    Draw-Sheet $g (0.13 * $s) (0.17 * $s) $sheetW $sheetH $s ([System.Drawing.Color]::FromArgb(150, 255, 255, 255)) ([System.Drawing.Color]::FromArgb(170, 255, 214, 0)) $detailed
    Draw-Sheet $g (0.37 * $s) (0.31 * $s) $sheetW $sheetH $s ([System.Drawing.Color]::White) ([System.Drawing.Color]::FromArgb(255, 124, 196, 35)) $detailed

    # Day grid on the copy (only where it can be seen).
    if ($detailed) {
        $dot = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 176, 190, 210))
        for ($row = 0; $row -lt 2; $row++) {
            for ($col = 0; $col -lt 3; $col++) {
                $g.FillPath($dot, (New-RoundRect (0.43 * $s + $col * 0.13 * $s) (0.52 * $s + $row * 0.11 * $s) (0.08 * $s) (0.065 * $s) (0.015 * $s)))
            }
        }
    }

    # Arrow from the source to the copy: a short bold arc with a head, yellow like the family "i".
    $arrowPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 255, 214, 0)), ([Math]::Max(1.5, 0.075 * $s))
    $arrowPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $arrowPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $arc = New-Object System.Drawing.RectangleF (0.17 * $s), (0.52 * $s), (0.34 * $s), (0.3 * $s)
    $g.DrawArc($arrowPen, $arc, 90, 90)
    $tipX = 0.34 * $s; $tipY = 0.82 * $s
    $head = New-Object System.Drawing.Drawing2D.GraphicsPath
    $head.AddPolygon(@(
        (New-Object System.Drawing.PointF ($tipX + 0.14 * $s), $tipY),
        (New-Object System.Drawing.PointF ($tipX + 0.01 * $s), ($tipY - 0.1 * $s)),
        (New-Object System.Drawing.PointF ($tipX + 0.01 * $s), ($tipY + 0.1 * $s))))
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 214, 0))), $head)

    $g.Dispose()
    return $bmp
}

function Get-PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $ms.ToArray()
}

# Logo for documents and the banner.
$logo = New-Logo 512 $true
$logo.Save([System.IO.Path]::Combine($OutDir, 'logo.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$logo.Dispose()
"saved logo.png"

# Application icon: every frame drawn at its own size (not scaled down), small ones without fine detail.
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @()
foreach ($size in $sizes) {
    $bmp = New-Logo $size ($size -ge 32)
    $frames += , (Get-PngBytes $bmp)
    $bmp.Dispose()
}
$icoPath = [System.IO.Path]::Combine($root, 'src', 'IwoBridge.Wpf', 'app.ico')
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$frames[$i].Length); $w.Write([UInt32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $w.Write($frame) }
$w.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $out.ToArray())
"saved app.ico ($($sizes.Count) sizes)"
