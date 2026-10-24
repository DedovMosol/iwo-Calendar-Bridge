param(
    # Square product logo (build\make-logo.ps1): two calendar sheets and an arrow - copy from one to another.
    [string]$Logo = (Join-Path $PSScriptRoot '..\docs\images\logo.png'),
    [string]$OutDir = (Join-Path $PSScriptRoot '..\docs\images')
)
# Dev-only: draws the README banner (1600x444) and the GitHub social preview (1280x640) in the same style
# as the other iwo projects: blue gradient, square logo on the left, title and a one-line summary.
# ASCII-only on purpose (Windows PowerShell 5.1 reads BOM-less UTF-8 as ANSI).
Add-Type -AssemblyName System.Drawing

function New-Card([int]$width, [int]$height, [int]$logoSize, [int]$titleSize, [int]$subtitleSize, [string[]]$subtitle, [string]$file) {
    $bmp = New-Object System.Drawing.Bitmap $width, $height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    $rect = New-Object System.Drawing.Rectangle 0, 0, $width, $height
    $back = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(21, 101, 192)), ([System.Drawing.Color]::FromArgb(13, 71, 161)), 30.0
    $g.FillRectangle($back, $rect)

    $logoImage = [System.Drawing.Image]::FromFile((Resolve-Path $Logo))
    $logoX = [int]($height * 0.23)
    $logoY = [int](($height - $logoSize) / 2)
    $g.DrawImage($logoImage, $logoX, $logoY, $logoSize, $logoSize)
    $logoImage.Dispose()

    $textLeft = $logoX + $logoSize + [int]($height * 0.1)
    $textWidth = $width - $textLeft - [int]($height * 0.1)
    $center = New-Object System.Drawing.StringFormat
    $center.Alignment = [System.Drawing.StringAlignment]::Center

    $titleFont = New-Object System.Drawing.Font 'Segoe UI', $titleSize, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $subFont = New-Object System.Drawing.Font 'Segoe UI', $subtitleSize, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
    $white = [System.Drawing.Brushes]::White
    $soft = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(235, 255, 255, 255))

    $lineHeight = [int]($subtitleSize * 1.45)
    $block = $titleSize * 1.25 + $lineHeight * $subtitle.Count
    $top = [int](($height - $block) / 2)
    $g.DrawString('iwo Calendar Bridge', $titleFont, $white, (New-Object System.Drawing.RectangleF $textLeft, $top, $textWidth, ($titleSize * 1.4)), $center)
    $y = $top + [int]($titleSize * 1.3)
    foreach ($line in $subtitle) {
        $g.DrawString($line, $subFont, $soft, (New-Object System.Drawing.RectangleF $textLeft, $y, $textWidth, $lineHeight), $center)
        $y += $lineHeight
    }

    $g.Dispose()
    $path = Join-Path $OutDir $file
    if ($file.EndsWith('.jpg')) {
        $codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
        $quality = New-Object System.Drawing.Imaging.EncoderParameters 1
        $quality.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), 92L
        $bmp.Save($path, $codec, $quality)
    } else {
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    }

    $bmp.Dispose()
    "saved $path"
}

New-Card 1600 444 290 92 30 @('Calendar copy between Exchange mailboxes  |  safe cleanup  |  Windows 7+') 'banner.jpg'
New-Card 1280 640 300 84 32 @('Copy a calendar from one Exchange mailbox to another', 'Outlook  ->  EWS  |  safe cleanup  |  Windows 7+') 'social-preview.png'
