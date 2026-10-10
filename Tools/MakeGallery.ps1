# 生成 Paradox Mods 图库图片（1920x1080，翻译前 / 翻译后对比）。
# 原始截图放在 Gallery\，输出到 Properties\Screenshots\。
param(
    [string]$Src = (Join-Path $PSScriptRoot '..\Gallery'),
    [string]$Out = (Join-Path $PSScriptRoot '..\Properties\Screenshots')
)
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $Out | Out-Null

$W = 1920; $H = 1080
$bgTop = [System.Drawing.Color]::FromArgb(255, 16, 30, 48)
$bgBottom = [System.Drawing.Color]::FromArgb(255, 22, 64, 86)
$accent = [System.Drawing.Color]::FromArgb(255, 0, 200, 255)
$beforeColor = [System.Drawing.Color]::FromArgb(255, 150, 160, 175)
$afterColor = [System.Drawing.Color]::FromArgb(255, 90, 220, 120)
$fontFamily = 'Microsoft YaHei UI'

function New-Canvas {
    $bmp = New-Object System.Drawing.Bitmap $W, $H
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $rect = New-Object System.Drawing.Rectangle 0, 0, $W, $H
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $bgTop, $bgBottom, 90
    $g.FillRectangle($brush, $rect)
    $brush.Dispose()
    return @($bmp, $g)
}

function Draw-Title($g, [string]$title, [string]$subtitle) {
    $f1 = New-Object System.Drawing.Font $fontFamily, 44, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $f2 = New-Object System.Drawing.Font $fontFamily, 26, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $accent), 60, 44, 8, 92)
    $g.DrawString($title, $f1, [System.Drawing.Brushes]::White, 84, 34)
    $g.DrawString($subtitle, $f2, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 170, 200, 220))), 86, 100)
    $f1.Dispose(); $f2.Dispose()
}

function Draw-Label($g, [string]$text, $color, [int]$x, [int]$y) {
    $f = New-Object System.Drawing.Font $fontFamily, 24, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $size = $g.MeasureString($text, $f)
    $pill = New-Object System.Drawing.Drawing2D.GraphicsPath
    $w = [int]$size.Width + 28; $h = 38; $r = 19
    $pill.AddArc($x, $y, $r * 2, $h, 90, 180)
    $pill.AddArc($x + $w - $r * 2, $y, $r * 2, $h, 270, 180)
    $pill.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush $color), $pill)
    $g.DrawString($text, $f, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 12, 20, 30))), $x + 14, $y + 4)
    $f.Dispose(); $pill.Dispose()
}

function Draw-Shot($g, [string]$file, [System.Drawing.Rectangle]$crop, [int]$x, [int]$y, [int]$width) {
    $img = [System.Drawing.Image]::FromFile($file)
    $height = [int]($crop.Height * $width / $crop.Width)
    # 阴影 + 截图
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(90, 0, 0, 0))), $x + 8, $y + 10, $width, $height)
    $dest = New-Object System.Drawing.Rectangle $x, $y, $width, $height
    $g.DrawImage($img, $dest, $crop, [System.Drawing.GraphicsUnit]::Pixel)
    $img.Dispose()
    return $height
}

function Save-Jpeg($bmp, [string]$path) {
    $codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
    $p = New-Object System.Drawing.Imaging.EncoderParameters 1
    $p.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), 92L
    $bmp.Save($path, $codec, $p)
}


# ---- 0. 封面：图标 + 名字 + 四个主要功能（真实截图）
. (Join-Path $PSScriptRoot 'Brand.ps1')
function Draw-Fit($g, [string]$file, [System.Drawing.Rectangle]$crop, [int]$x, [int]$y, [int]$w, [int]$h) {
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 10, 18, 30))), (New-RoundRect $x $y $w $h 14))
    $img = [System.Drawing.Image]::FromFile($file)
    $scale = [Math]::Min(($w - 24) / $crop.Width, ($h - 24) / $crop.Height)
    $dw = [int]($crop.Width * $scale); $dh = [int]($crop.Height * $scale)
    $dest = New-Object System.Drawing.Rectangle ($x + [int](($w - $dw) / 2)), ($y + [int](($h - $dh) / 2)), $dw, $dh
    $g.DrawImage($img, $dest, $crop, [System.Drawing.GraphicsUnit]::Pixel)
    $img.Dispose()
}
$bmp, $g = New-Canvas
Draw-Skyline $g $W $H 260 18
Draw-BrandIcon $g 150 170 380
$fT = New-Object System.Drawing.Font 'Segoe UI', 88, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$fS = New-Object System.Drawing.Font $fontFamily, 34, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$fS2 = New-Object System.Drawing.Font $fontFamily, 28, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$soft = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 170, 200, 220))
Draw-CenteredText $g 'AI Translate!' $fT ([System.Drawing.Brushes]::White) 40 600 600 110
Draw-CenteredText $g 'Mods and assets in your language' (New-Object System.Drawing.Font $fontFamily, 30, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)) ([System.Drawing.Brushes]::White) 10 712 660 50
Draw-CenteredText $g '让 mod 和资产说你的语言' $fS2 $soft 40 764 600 46
$pill = New-RoundRect 130 850 420 56 28
$g.FillPath((New-Object System.Drawing.SolidBrush $BrandCyan), $pill)
$fP = New-Object System.Drawing.Font $fontFamily, 24, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
Draw-CenteredText $g 'DeepSeek / OpenAI-compatible' $fP (New-Object System.Drawing.SolidBrush $BrandNavy) 130 850 420 56

$tiles = @(
    @{ File = '2-2.png';    Crop = (New-Object System.Drawing.Rectangle 170, 905, 660, 280); En = 'Mod panels translated'; Zh = 'mod 界面翻译成你的语言' },
    @{ File = '1-2.png';    Crop = (New-Object System.Drawing.Rectangle 11, 13, 700, 194);    En = 'Asset names and descriptions'; Zh = '资产补名字、写简介' },
    @{ File = '3-log.jpg';  Crop = (New-Object System.Drawing.Rectangle 128, 690, 560, 600);  En = 'Readable mod list'; Zh = 'mod 列表：译名 (原名)' },
    @{ File = '3-log.jpg';  Crop = (New-Object System.Drawing.Rectangle 755, 672, 860, 528); En = 'Translated once, in the background'; Zh = '后台自动翻译，只翻一次' }
)
$tw = 560; $th = 330; $gx = 30; $gy = 110
for ($i = 0; $i -lt 4; $i++) {
    $tx = 720 + ($i % 2) * ($tw + $gx)
    $ty = 70 + [Math]::Floor($i / 2) * ($th + $gy)
    Draw-Fit $g (Join-Path $Src $tiles[$i].File) $tiles[$i].Crop $tx $ty $tw $th
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $BrandCyan), $tx + 2, $ty + $th + 22, 14, 14)
    $g.DrawString($tiles[$i].En, (New-Object System.Drawing.Font 'Segoe UI', 26, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)), [System.Drawing.Brushes]::White, $tx + 24, $ty + $th + 10)
    $g.DrawString($tiles[$i].Zh, $fS2, $soft, $tx + 24, $ty + $th + 46)
}
Save-Jpeg $bmp (Join-Path $Out '00_cover.jpg'); $g.Dispose(); $bmp.Dispose()
# ---- 1. 模组界面：Parking Lot Tool 英文 → 中文
$bmp, $g = New-Canvas
Draw-Title $g 'Mod panels in your language  ·  模组界面变成你的语言' 'Parking Lot Tool, before and after AI Translate!   翻译前后对比'
$shotW = 1760; $x = 80
Draw-Label $g 'BEFORE  翻译前' $beforeColor $x 168
$h1 = Draw-Shot $g (Join-Path $Src '2-1.png') (New-Object System.Drawing.Rectangle 158, 858, 2244, 444) $x 214 $shotW
$y2 = 214 + $h1 + 40
Draw-Label $g 'AFTER  翻译后' $afterColor $x $y2
Draw-Shot $g (Join-Path $Src '2-2.png') (New-Object System.Drawing.Rectangle 158, 836, 2244, 466) $x ($y2 + 46) $shotW | Out-Null
Save-Jpeg $bmp (Join-Path $Out '01_mod_panel.jpg'); $g.Dispose(); $bmp.Dispose()

# ---- 2. 资产：只有英文名、没有描述 → 本地化名称 + 生成的简介
$bmp, $g = New-Canvas
Draw-Title $g 'Assets get names and descriptions  ·  资产补上名字和简介' 'Missing descriptions are written for you, in your language   缺少描述的资产自动生成简介'
$shotW = 1500; $x = [int](($W - $shotW) / 2)
Draw-Label $g 'BEFORE  翻译前' $beforeColor $x 180
$h1 = Draw-Shot $g (Join-Path $Src '1-1.png') (New-Object System.Drawing.Rectangle 18, 20, 948, 192) $x 226 $shotW
$y2 = 226 + $h1 + 50
Draw-Label $g 'AFTER  翻译后' $afterColor $x $y2
Draw-Shot $g (Join-Path $Src '1-2.png') (New-Object System.Drawing.Rectangle 11, 13, 955, 194) $x ($y2 + 46) $shotW | Out-Null
Save-Jpeg $bmp (Join-Path $Out '02_asset.jpg'); $g.Dispose(); $bmp.Dispose()

Get-ChildItem $Out | ForEach-Object { "$($_.Name) $([int]($_.Length / 1024)) KB" }

# ---- 3. 选项菜单：mod 列表“译名 (原名)” + 实时日志
$bmp, $g = New-Canvas
Draw-Title $g 'Readable mod list and a live log  ·  看得懂的 mod 列表和实时日志' 'Every batch shows up as it finishes   每一批结果实时显示'
$crop = New-Object System.Drawing.Rectangle 107, 154, 1557, 1216
$shotH = 880
$shotW = [int]($crop.Width * $shotH / $crop.Height)
Draw-Shot $g (Join-Path $Src '3-log.jpg') $crop 80 172 $shotW | Out-Null
$fx = 80 + $shotW + 60
$fb = New-Object System.Drawing.Font $fontFamily, 30, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$fs = New-Object System.Drawing.Font $fontFamily, 24, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$soft = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 170, 200, 220))
$items = @(
    @('Mod list', 'Translated name (Original Name)', 'mod 列表：译名 (原名)'),
    @('Live log', 'Each batch, with samples', '日志：每批结果和样例'),
    @('Translated once', 'Saved and reused next time', '只翻一次，永久保存')
)
$y = 260
foreach ($it in $items) {
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $accent), $fx, $y + 12, 14, 14)
    $g.DrawString($it[0], $fb, [System.Drawing.Brushes]::White, $fx + 26, $y)
    $g.DrawString($it[1], $fs, $soft, $fx + 26, $y + 44)
    $g.DrawString($it[2], $fs, $soft, $fx + 26, $y + 78)
    $y += 190
}
Save-Jpeg $bmp (Join-Path $Out '03_list_log.jpg'); $g.Dispose(); $bmp.Dispose()
Get-ChildItem $Out | ForEach-Object { "$($_.Name) $([int]($_.Length / 1024)) KB" }
