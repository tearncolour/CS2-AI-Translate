# 生成 Paradox Mods 缩略图 Properties\Thumbnail.png（512x512）。
# 列表里显示得很小：只放图标、名字和一句话功能，字要大。
param([string]$Out = (Join-Path $PSScriptRoot '..\Properties\Thumbnail.png'))
. (Join-Path $PSScriptRoot 'Brand.ps1')

$size = 512
$bmp = New-Object System.Drawing.Bitmap $size, $size
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.TextRenderingHint = 'AntiAliasGridFit'

$rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $BrandNavy, $BrandTeal, 60
$g.FillRectangle($bg, $rect)
Draw-Skyline $g $size $size 100 34

Draw-BrandIcon $g 118 34 276

$white = [System.Drawing.Brushes]::White
$fTitle = New-Object System.Drawing.Font 'Segoe UI', 66, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
Draw-CenteredText $g 'AI Translate!' $fTitle $white 0 318 $size 86

# 一句话功能：青色胶囊
$fTag = New-Object System.Drawing.Font 'Segoe UI', 27, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$pill = New-RoundRect 58 414 396 54 27
$g.FillPath((New-Object System.Drawing.SolidBrush $BrandCyan), $pill)
Draw-CenteredText $g 'MODS → YOUR LANGUAGE' $fTag (New-Object System.Drawing.SolidBrush $BrandNavy) 58 414 396 54

$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
"saved $Out"
