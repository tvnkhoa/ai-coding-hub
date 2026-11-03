# Generates Assets\app.ico: the Mini Monitor mark (rounded #60CDFF tile with a dark pulse line),
# the same glyph the HUD title bar draws in XAML. Usage:
#   powershell -NoProfile -File generate-app-icon.ps1 <output.ico>
Add-Type -AssemblyName System.Drawing

function Draw-Mark {
    param([System.Drawing.Graphics]$g, [int]$N)

    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded tile (radius ~28% like the 18px/5px title-bar mark).
    $r = [Math]::Max(2.0, $N * 0.28)
    $d = $r * 2
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tile.AddArc(0, 0, $d, $d, 180, 90)
    $tile.AddArc($N - $d - 0.5, 0, $d, $d, 270, 90)
    $tile.AddArc($N - $d - 0.5, $N - $d - 0.5, $d, $d, 0, 90)
    $tile.AddArc(0, $N - $d - 0.5, $d, $d, 90, 90)
    $tile.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 96, 205, 255))), $tile)

    # Pulse line "M3 12h4l3-8 4 16 3-8h4" in a 24-unit box, drawn at 2/3 of the tile.
    $glyph = $N * (2.0 / 3.0)
    $off = ($N - $glyph) / 2.0
    $k = $glyph / 24.0
    $pts = @(
        @(3, 12), @(7, 12), @(10, 4), @(14, 20), @(17, 12), @(21, 12)
    ) | ForEach-Object { New-Object System.Drawing.PointF ([float]($off + $_[0] * $k)), ([float]($off + $_[1] * $k)) }
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 14, 16, 20)), ([float]([Math]::Max(1.4, 2.6 * $k)))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawLines($pen, [System.Drawing.PointF[]]$pts)
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    Draw-Mark -g $g -N $s
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,$ms.ToArray()
    $bmp.Dispose()
    $ms.Dispose()
}

# .ico container with PNG-compressed frames (Vista+ format).
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
for ($i = 0; $i -lt $pngs.Count; $i++) {
    $s = $sizes[$i]; $data = $pngs[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([Byte]$dim); $bw.Write([Byte]$dim); $bw.Write([Byte]0); $bw.Write([Byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($data in $pngs) { $bw.Write($data) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($args[0], $out.ToArray())
Write-Host "Wrote $($args[0]) ($($out.Length) bytes, $($pngs.Count) frames)"
