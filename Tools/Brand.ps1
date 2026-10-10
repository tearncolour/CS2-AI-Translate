# 图标与配色（MakeThumbnail.ps1 和 MakeGallery.ps1 共用）。用法：. (Join-Path $PSScriptRoot 'Brand.ps1')
Add-Type -AssemblyName System.Drawing

$BrandNavy = [System.Drawing.Color]::FromArgb(255, 14, 28, 48)
$BrandTeal = [System.Drawing.Color]::FromArgb(255, 22, 92, 118)
$BrandCyan = [System.Drawing.Color]::FromArgb(255, 0, 196, 255)
$BrandGold = [System.Drawing.Color]::FromArgb(255, 255, 206, 64)
$BrandFont = 'Microsoft YaHei UI'

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# 气泡：圆角矩形 + 底部小尾巴（$tailLeft 决定尾巴在左还是右）。
function Draw-Bubble($g, [single]$x, [single]$y, [single]$w, [single]$h, $fill, [bool]$tailLeft) {
    $body = New-RoundRect $x $y $w $h ($h * 0.22)
    $tail = New-Object System.Drawing.Drawing2D.GraphicsPath
    if ($tailLeft) {
        $tail.AddPolygon(@(
            (New-Object System.Drawing.PointF ($x + $w * 0.16), ($y + $h - 2)),
            (New-Object System.Drawing.PointF ($x + $w * 0.10), ($y + $h * 1.28)),
            (New-Object System.Drawing.PointF ($x + $w * 0.38), ($y + $h - 2))))
    } else {
        $tail.AddPolygon(@(
            (New-Object System.Drawing.PointF ($x + $w * 0.62), ($y + $h - 2)),
            (New-Object System.Drawing.PointF ($x + $w * 0.90), ($y + $h * 1.28)),
            (New-Object System.Drawing.PointF ($x + $w * 0.84), ($y + $h - 2))))
    }
    $brush = New-Object System.Drawing.SolidBrush $fill
    $g.FillPath($brush, $body)
    $g.FillPath($brush, $tail)
    $brush.Dispose(); $body.Dispose(); $tail.Dispose()
}

# 四角星（AI 的“闪光”）。
function Draw-Sparkle($g, [single]$cx, [single]$cy, [single]$r, $fill) {
    $k = $r * 0.28
    $pts = @(
        (New-Object System.Drawing.PointF $cx, ($cy - $r)),
        (New-Object System.Drawing.PointF ($cx + $k), ($cy - $k)),
        (New-Object System.Drawing.PointF ($cx + $r), $cy),
        (New-Object System.Drawing.PointF ($cx + $k), ($cy + $k)),
        (New-Object System.Drawing.PointF $cx, ($cy + $r)),
        (New-Object System.Drawing.PointF ($cx - $k), ($cy + $k)),
        (New-Object System.Drawing.PointF ($cx - $r), $cy),
        (New-Object System.Drawing.PointF ($cx - $k), ($cy - $k)))
    $b = New-Object System.Drawing.SolidBrush $fill
    $g.FillPolygon($b, $pts)
    $b.Dispose()
}

function Draw-CenteredText($g, [string]$text, $font, $brush, [single]$x, [single]$y, [single]$w, [single]$h) {
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
    $g.DrawString($text, $font, $brush, (New-Object System.Drawing.RectangleF $x, $y, $w, $h), $sf)
    $sf.Dispose()
}

# 品牌图标：白色 “A” 气泡 → 青色 “文” 气泡 + 金色闪光。占用 $s x $s 的方块。
function Draw-BrandIcon($g, [single]$x, [single]$y, [single]$s) {
    $bw = $s * 0.60; $bh = $s * 0.46
    # 阴影
    $shadow = [System.Drawing.Color]::FromArgb(80, 0, 0, 0)
    Draw-Bubble $g ($x + $s * 0.02) ($y + $s * 0.03) $bw $bh $shadow $true
    Draw-Bubble $g ($x + $s * 0.40 + $s * 0.02) ($y + $s * 0.34 + $s * 0.03) $bw $bh $shadow $false
    # 背后：原文
    Draw-Bubble $g $x $y $bw $bh ([System.Drawing.Color]::White) $true
    $fa = New-Object System.Drawing.Font 'Segoe UI', ($s * 0.30), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    Draw-CenteredText $g 'A' $fa (New-Object System.Drawing.SolidBrush $BrandNavy) $x ($y - $s * 0.01) $bw $bh
    # 前面：译文
    $fx = $x + $s * 0.40; $fy = $y + $s * 0.34
    Draw-Bubble $g $fx $fy $bw $bh $BrandCyan $false
    $fz = New-Object System.Drawing.Font $BrandFont, ($s * 0.27), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    Draw-CenteredText $g '文' $fz ([System.Drawing.Brushes]::White) $fx ($fy + $s * 0.005) $bw $bh
    # AI 闪光
    Draw-Sparkle $g ($x + $s * 0.90) ($y + $s * 0.12) ($s * 0.11) $BrandGold
    Draw-Sparkle $g ($x + $s * 0.76) ($y + $s * 0.02) ($s * 0.05) $BrandGold
    $fa.Dispose(); $fz.Dispose()
}

# 城市天际线剪影（底部装饰）。
function Draw-Skyline($g, [int]$width, [int]$bottom, [int]$scale, [int]$alpha) {
    $b = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb($alpha, 255, 255, 255))
    $heights = 38, 70, 52, 96, 60, 84, 44, 112, 66, 78, 54, 92, 48, 72, 58, 100, 62
    $x = 0; $i = 0
    while ($x -lt $width) {
        $h = $heights[$i % $heights.Count] * $scale / 100
        $w = (22 + ($i * 7) % 14) * $scale / 100
        $g.FillRectangle($b, $x, $bottom - $h, $w - [Math]::Max(2, $scale / 50), $h)
        $x += $w; $i++
    }
    $b.Dispose()
}
