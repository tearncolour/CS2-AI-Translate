# 截图助手：游戏窗口在前台时每隔几秒截一张屏，供挑选模组页面插图。
# 用法：.\Tools\CaptureGame.ps1 [-Minutes 5] [-Interval 2.5]
param(
    [double]$Minutes = 5,
    [double]$Interval = 2.5,
    [string]$Out = (Join-Path $PSScriptRoot '..\bin\Captures'),
    [switch]$Any   # test: capture whatever window is in front
)
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Fg {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
'@
[Fg]::SetProcessDPIAware() | Out-Null
New-Item -ItemType Directory -Force $Out | Out-Null
$jpeg = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
$params = New-Object System.Drawing.Imaging.EncoderParameters 1
$params.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), 92L

$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$end = (Get-Date).AddMinutes($Minutes)
$n = 0
$lastSig = $null
while ((Get-Date) -lt $end) {
    $procId = 0
    [Fg]::GetWindowThreadProcessId([Fg]::GetForegroundWindow(), [ref]$procId) | Out-Null
    $proc = Get-Process -Id $procId -ErrorAction SilentlyContinue
    if ($Any -or ($proc -and $proc.ProcessName -eq 'Cities2')) {
        $bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
        $g.Dispose()
        # 画面没变就不重复保存（按缩略图粗略比较）。
        $thumb = New-Object System.Drawing.Bitmap 32, 18
        $tg = [System.Drawing.Graphics]::FromImage($thumb)
        $tg.DrawImage($bmp, 0, 0, 32, 18)
        $tg.Dispose()
        $sb = New-Object System.Text.StringBuilder
        for ($y = 0; $y -lt 18; $y++) {
            for ($x = 0; $x -lt 32; $x++) {
                [void]$sb.Append([int]($thumb.GetPixel($x, $y).GetBrightness() * 9))
            }
        }
        $sig = $sb.ToString()
        $thumb.Dispose()
        if ($sig -ne $lastSig) {
            $lastSig = $sig
            $n++
            $file = Join-Path $Out ('{0:D3}_{1}.jpg' -f $n, (Get-Date -Format 'HHmmss'))
            $bmp.Save($file, $jpeg, $params)
        }
        $bmp.Dispose()
    }
    Start-Sleep -Milliseconds ([int]($Interval * 1000))
}
"captured $n frames to $Out"
