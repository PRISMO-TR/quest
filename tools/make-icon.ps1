# Generates Assets\quest.png and Assets\quest.ico (PNG-framed, multi-size).
param([string]$OutDir = (Join-Path $PSScriptRoot '..\Assets'))
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-QuestBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $r = [single]($s * 0.24)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $w = [single]($s - 1)
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
    $path.AddArc($w - $r * 2, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($w - $r * 2, $w - $r * 2, $r * 2, $r * 2, 0, 90)
    $path.AddArc(0, $w - $r * 2, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $c1 = [System.Drawing.Color]::FromArgb(255, 139, 92, 246)
    $c2 = [System.Drawing.Color]::FromArgb(255, 6, 182, 212)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new($s, $s)), $c1, $c2
    $g.FillPath($grad, $path)
    $white = [System.Drawing.Color]::FromArgb(245, 255, 255, 255)
    $cx = $s / 2.0
    $ring = [single]($s * 0.29)
    $pen = New-Object System.Drawing.Pen $white, ([single][Math]::Max(1.5, $s * 0.075))
    $g.DrawEllipse($pen, [single]($cx - $ring), [single]($cx - $ring), [single]($ring * 2), [single]($ring * 2))
    $dot = [single]($s * 0.11)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $white), [single]($cx - $dot), [single]($cx - $dot), [single]($dot * 2), [single]($dot * 2))
    $g.Dispose()
    return $bmp
}

$sizes = 16, 24, 32, 48, 64, 256
$pngs = @()
foreach ($s in $sizes) {
    $b = New-QuestBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($s -eq 256) { $b.Save((Join-Path $OutDir 'quest.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $b.Dispose()
    $pngs += , $ms.ToArray()
}

$fs = [System.IO.File]::Create((Join-Path $OutDir 'quest.ico'))
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
"icon written to $OutDir"
