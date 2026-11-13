Add-Type -AssemblyName System.Drawing

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = $size / 256.0

    # 圆角方形背景 + 对角渐变
    $pad = 6 * $s
    $r = 56 * $s
    $rect = New-Object System.Drawing.RectangleF($pad, $pad, ($size - 2*$pad), ($size - 2*$pad))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $c1 = [System.Drawing.Color]::FromArgb(255, 0x5A, 0x8D, 0xFF)
    $c2 = [System.Drawing.Color]::FromArgb(255, 0x8B, 0x6D, 0xF2)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0,0)),
        (New-Object System.Drawing.PointF($size,$size)), $c1, $c2)
    $g.FillPath($grad, $path)

    # 内层高光
    $inner = New-Object System.Drawing.RectangleF($pad, $pad, ($size-2*$pad), ($size-2*$pad)*0.55)
    $hlPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $hlPath.AddEllipse($inner)
    $hlBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $inner,
        [System.Drawing.Color]::FromArgb(38,255,255,255),
        [System.Drawing.Color]::FromArgb(0,255,255,255),
        [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $g.FillPath($hlBrush, $hlPath)

    # 两个人形：左上实心白，右下半透明白
    function Draw-Person($g, $cx, $cy, $scale, $color) {
        $headR = 20 * $scale
        $br = New-Object System.Drawing.SolidBrush($color)
        $g.FillEllipse($br, ($cx - $headR), ($cy - $headR), (2*$headR), (2*$headR))
        # 肩膀：圆角拱形
        $bodyW = 62 * $scale
        $bodyH = 44 * $scale
        $body = New-Object System.Drawing.Drawing2D.GraphicsPath
        $bx = $cx - $bodyW/2
        $by = $cy + 14 * $scale
        $body.AddArc($bx, $by, $bodyW, $bodyH, 180, 180)
        $body.AddLine(($bx + $bodyW), ($by + $bodyH/2), $bx, ($by + $bodyH/2))
        $body.CloseFigure()
        $g.FillPath($br, $body)
        $body.Dispose(); $br.Dispose()
    }

    Draw-Person $g (88*$s) (96*$s) $s ([System.Drawing.Color]::FromArgb(255,255,255,255))
    Draw-Person $g (168*$s) (150*$s) $s ([System.Drawing.Color]::FromArgb(130,255,255,255))

    # 交换弧线箭头（双向）
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255,255,255,255), (13*$s))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $arcRect = New-Object System.Drawing.RectangleF((56*$s), (56*$s), (144*$s), (144*$s))
    $g.DrawArc($pen, $arcRect, 200, 110)
    $g.DrawArc($pen, $arcRect, 20, 110)

    # 箭头头部
    $capBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255,255,255,255))
    function Draw-ArrowHead($g, $x, $y, $angleDeg, $sz, $brush) {
        $a = $angleDeg * [Math]::PI / 180
        $p1 = New-Object System.Drawing.PointF($x, $y)
        $p2 = New-Object System.Drawing.PointF(
            ($x - $sz*[Math]::Cos($a - 0.62)),
            ($y - $sz*[Math]::Sin($a - 0.62)))
        $p3 = New-Object System.Drawing.PointF(
            ($x - $sz*[Math]::Cos($a + 0.62)),
            ($y - $sz*[Math]::Sin($a + 0.62)))
        $g.FillPolygon($brush, @($p1,$p2,$p3))
    }
    Draw-ArrowHead $g (200*$s) (128*$s) -58 (26*$s) $capBrush
    Draw-ArrowHead $g (56*$s) (128*$s) 122 (26*$s) $capBrush

    $pen.Dispose(); $capBrush.Dispose(); $grad.Dispose(); $hlBrush.Dispose()
    $path.Dispose(); $hlPath.Dispose()
    $g.Dispose()
    return $bmp
}

# 先输出 512 PNG 预览
$big = New-IconBitmap 512
$big.Save("E:\ai\CursorAccountSwitcher\assets\icon-preview.png", [System.Drawing.Imaging.ImageFormat]::Png)
$big.Dispose()
"预览已生成"
