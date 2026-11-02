# 生成视频封面：左边程序图标 ≈ 右边击倒骷髅
# 全部用 [Type]::new() 显式构造，避免 New-Object 参数被当成一个数组
Add-Type -AssemblyName System.Drawing

$W = 2560; $H = 1440
$bmp = [System.Drawing.Bitmap]::new($W, $H)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

# ---- 背景 ----
$bgRect = [System.Drawing.Rectangle]::new(0, 0, $W, $H)
$bg = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
    $bgRect,
    [System.Drawing.Color]::FromArgb(255, 9, 11, 15),
    [System.Drawing.Color]::FromArgb(255, 22, 26, 34),
    90.0)
$g.FillRectangle($bg, $bgRect)
$bg.Dispose()

# 中间一团青色辉光
for ($i = 0; $i -lt 24; $i++) {
    $a = 15 - $i * 0.6
    if ($a -le 0) { continue }
    $r = 240 + $i * 30
    $brush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb([int]$a, 57, 215, 232))
    $g.FillEllipse($brush, [single]($W / 2 - $r), [single]($H / 2 - $r), [single]($r * 2), [single]($r * 2))
    $brush.Dispose()
}

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc(($x + $w - $d), $y, $d, $d, 270, 90)
    $p.AddArc(($x + $w - $d), ($y + $h - $d), $d, $d, 0, 90)
    $p.AddArc($x, ($y + $h - $d), $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

$iconSize = 600
$iconY = ($H - $iconSize) / 2
$gap = 300
$leftX = $W / 2 - $gap / 2 - $iconSize
$rightX = $W / 2 + $gap / 2
$cyan = [System.Drawing.Color]::FromArgb(255, 57, 215, 232)
$black = [System.Drawing.Color]::FromArgb(255, 10, 12, 16)
$white = [System.Drawing.Color]::FromArgb(255, 246, 248, 252)

# ---- 左：程序图标（深色圆角方块 + 青色准星）----
$shadowBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(26, 0, 0, 0))
for ($k = 0; $k -lt 14; $k++) {
    $rr = New-RoundRect ($leftX - $k) ($iconY - $k + 22) ($iconSize + $k * 2) ($iconSize + $k * 2) 148
    $g.FillPath($shadowBrush, $rr); $rr.Dispose()
}
$shadowBrush.Dispose()

$leftPath = New-RoundRect $leftX $iconY $iconSize $iconSize 148
$leftFill = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
    [System.Drawing.Rectangle]::new([int]$leftX, [int]$iconY, $iconSize, $iconSize),
    [System.Drawing.Color]::FromArgb(255, 28, 32, 41),
    [System.Drawing.Color]::FromArgb(255, 13, 15, 20),
    90.0)
$g.FillPath($leftFill, $leftPath)
$leftFill.Dispose()

$cx = $leftX + $iconSize / 2
$cy = $iconY + $iconSize / 2
$ringPen = [System.Drawing.Pen]::new($cyan, 25)
$g.DrawEllipse($ringPen, [single]($cx - 185), [single]($cy - 185), 370, 370)
$ringPen.Dispose()

$tickPen = [System.Drawing.Pen]::new($cyan, 25)
$tickPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$tickPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
foreach ($d in @(@(0, -1), @(0, 1), @(-1, 0), @(1, 0))) {
    $g.DrawLine($tickPen,
        [single]($cx + $d[0] * 145), [single]($cy + $d[1] * 145),
        [single]($cx + $d[0] * 245), [single]($cy + $d[1] * 245))
}
$tickPen.Dispose()

$dot = [System.Drawing.SolidBrush]::new($cyan)
$g.FillEllipse($dot, [single]($cx - 33), [single]($cy - 33), 66, 66)
$dot.Dispose()
$leftPath.Dispose()

# ---- 右：击倒骷髅 ----
$rightPath = New-RoundRect $rightX $iconY $iconSize $iconSize 148
$rightFill = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
    [System.Drawing.Rectangle]::new([int]$rightX, [int]$iconY, $iconSize, $iconSize),
    [System.Drawing.Color]::FromArgb(255, 8, 10, 14),
    [System.Drawing.Color]::FromArgb(255, 18, 22, 30),
    90.0)
$g.FillPath($rightFill, $rightPath)
$rightFill.Dispose()
$bluePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 31, 111, 235), 9)
$g.DrawPath($bluePen, $rightPath)
$bluePen.Dispose()
$rightPath.Dispose()

$bx = $rightX + $iconSize / 2
$by = $iconY + $iconSize / 2 + 8
$wb = [System.Drawing.SolidBrush]::new($white)
$bb = [System.Drawing.SolidBrush]::new($black)

$g.FillEllipse($wb, [single]($bx - 155), [single]($by - 172), 310, 292)
$jaw = New-RoundRect ($bx - 104) ($by + 68) 208 118 24
$g.FillPath($wb, $jaw); $jaw.Dispose()

$g.FillEllipse($bb, [single]($bx - 116), [single]($by - 92), 92, 100)
$g.FillEllipse($bb, [single]($bx + 24), [single]($by - 92), 92, 100)

$nose = [System.Drawing.Drawing2D.GraphicsPath]::new()
$nose.AddPolygon([System.Drawing.PointF[]]@(
    [System.Drawing.PointF]::new([single]($bx - 25), [single]($by + 4)),
    [System.Drawing.PointF]::new([single]($bx + 25), [single]($by + 4)),
    [System.Drawing.PointF]::new([single]$bx, [single]($by + 58))
))
$g.FillPath($bb, $nose); $nose.Dispose()

for ($i = 0; $i -lt 4; $i++) {
    $g.FillRectangle($bb, [single]($bx - 92 + $i * 50), [single]($by + 104), 15, 66)
}
$wb.Dispose(); $bb.Dispose()

# ---- 中间：约等号 ≈ ----
$halfW = 150.0
$wave = [System.Drawing.Drawing2D.GraphicsPath]::new()
foreach ($oy in @(-92, 92)) {
    $pts = [System.Collections.Generic.List[System.Drawing.PointF]]::new()
    for ($t = -1.0; $t -le 1.0001; $t += 0.02) {
        $px = $W / 2 + $t * $halfW
        $py = $H / 2 + $oy + [Math]::Sin($t * 3.14159265) * 32
        $pts.Add([System.Drawing.PointF]::new([single]$px, [single]$py))
    }
    $wave.AddCurve([System.Drawing.PointF[]]$pts.ToArray(), 0.4)
}

for ($i = 10; $i -ge 1; $i--) {
    $wa = [int](8 + 5 * (10 - $i) / 10.0)
    $wp = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb($wa, 57, 215, 232), (44 + $i * 10))
    $wp.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $wp.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawPath($wp, $wave)
    $wp.Dispose()
}
$gradBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
    [System.Drawing.Rectangle]::new([int]($W / 2 - $halfW), 0, [int]($halfW * 2), $H),
    $cyan,
    [System.Drawing.Color]::FromArgb(255, 245, 250, 255),
    90.0)
$wavePen = [System.Drawing.Pen]::new($gradBrush, 44)
$wavePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$wavePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$g.DrawPath($wavePen, $wave)
$wavePen.Dispose(); $gradBrush.Dispose(); $wave.Dispose()

$g.Dispose()

$out = [System.Drawing.Bitmap]::new(1920, 1080)
$g2 = [System.Drawing.Graphics]::FromImage($out)
$g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g2.DrawImage($bmp, [System.Drawing.Rectangle]::new(0, 0, 1920, 1080))
$g2.Dispose(); $bmp.Dispose()
$out.Save('D:\played\captures\cover_0.8.23.png', [System.Drawing.Imaging.ImageFormat]::Png)
$out.Dispose()
"ok"
