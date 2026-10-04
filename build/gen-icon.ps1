# Generates Assets\icon.ico - rounded gradient tile with a music note.
# PNG-compressed ICO frames (supported since Vista). ASCII-only on purpose:
# Windows PowerShell 5.1 misparses BOM-less UTF-8 scripts.
param([string]$OutPath = "$PSScriptRoot\..\Assets\icon.ico")
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = "AntiAlias"
    $g.TextRenderingHint = "AntiAliasGridFit"
    $g.Clear([System.Drawing.Color]::Transparent)

    $r = [int]($size * 0.22)
    if ($r -lt 2) { $r = 2 }
    $w = $size - 1
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($w - 2 * $r, 0, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($w - 2 * $r, $w - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc(0, $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()

    $c1 = [System.Drawing.Color]::FromArgb(255, 59, 200, 214)
    $c2 = [System.Drawing.Color]::FromArgb(255, 24, 96, 180)
    $p1 = New-Object System.Drawing.Point(0, 0)
    $p2 = New-Object System.Drawing.Point($size, $size)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($p1, $p2, $c1, $c2)
    $g.FillPath($grad, $path)

    $font = New-Object System.Drawing.Font("Segoe UI Symbol", [float]($size * 0.55), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = "Center"
    $sf.LineAlignment = "Center"
    $note = [string][char]0x266A
    $rect = New-Object System.Drawing.RectangleF(0, [float](-$size * 0.03), $size, $size)
    $g.DrawString($note, $font, [System.Drawing.Brushes]::White, $rect, $sf)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,($ms.ToArray())
}

$sizes = @(256, 64, 48, 32, 16)
$pngs = @()
foreach ($s in $sizes) { $pngs += ,(New-IconPng $s) }

$msOut = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($msOut)
$bw.Write([UInt16]0)               # reserved
$bw.Write([UInt16]1)               # type: icon
$bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $data = $pngs[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([Byte]$dim)          # width (0 = 256)
    $bw.Write([Byte]$dim)          # height
    $bw.Write([Byte]0)             # palette
    $bw.Write([Byte]0)             # reserved
    $bw.Write([UInt16]1)           # planes
    $bw.Write([UInt16]32)          # bpp
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($d in $pngs) { $bw.Write([byte[]]$d) }
$bw.Flush()

New-Item -ItemType Directory -Force -Path (Split-Path $OutPath) | Out-Null
[System.IO.File]::WriteAllBytes($OutPath, $msOut.ToArray())
Write-Host "Wrote $OutPath ($($msOut.Length) bytes)"
