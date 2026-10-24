# Generates app.ico (16–256 px): a green crescent on a dark glass rounded square.
# Run once; build.bat embeds the result into PrayerWidget.exe.
Add-Type -AssemblyName System.Drawing
$out = Join-Path (Split-Path $PSScriptRoot) 'app.ico'
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Object Drawing.Bitmap $s, $s, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.Clear([Drawing.Color]::Transparent)
    $r = $s * 0.22; $d = $r * 2; $w = $s - 1
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($w - $d, 0, $d, $d, 270, 90)
    $path.AddArc($w - $d, $w - $d, $d, $d, 0, 90); $path.AddArc(0, $w - $d, $d, $d, 90, 90); $path.CloseFigure()
    $bg = New-Object Drawing.Drawing2D.LinearGradientBrush((New-Object Drawing.RectangleF 0, 0, $s, $s), [Drawing.Color]::FromArgb(255, 52, 54, 66), [Drawing.Color]::FromArgb(255, 18, 18, 24), 90)
    $g.FillPath($bg, $path)
    if ($s -ge 24) { $pen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(120, 255, 255, 255)), ([Math]::Max(1, $s / 48)); $g.DrawPath($pen, $path) }
    # crescent
    $m = $s * 0.2; $c = $s - 2 * $m
    $moon = New-Object Drawing.Drawing2D.GraphicsPath; $moon.AddEllipse($m, $m, $c, $c)
    $rg = New-Object Drawing.Region $moon
    $cut = New-Object Drawing.Drawing2D.GraphicsPath; $cut.AddEllipse($m - $c * 0.38, $m - $c * 0.22, $c * 0.9, $c * 0.9)
    $rg.Exclude($cut)
    $g.FillRegion((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 74, 222, 128))), $rg)
    $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $pngs += , $ms.ToArray()
    $g.Dispose(); $bmp.Dispose()
}
# ICO container with PNG-compressed entries
$fs = [IO.File]::Create($out); $bw = New-Object IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$pngs[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
"wrote $out"
