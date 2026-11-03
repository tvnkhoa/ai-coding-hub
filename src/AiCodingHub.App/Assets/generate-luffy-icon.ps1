Add-Type -AssemblyName System.Drawing

# Draws the Straw Hat Pirates (Luffy) Jolly Roger onto a graphics surface.
# All coordinates are in a 256x256 design box; we scale to the target size.
function Draw-Jolly {
    param([System.Drawing.Graphics]$g, [int]$N)

    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    $k = $N / 256.0
    $g.ScaleTransform($k, $k)

    # ---- Palette ----
    $navy    = [System.Drawing.Color]::FromArgb(255, 15, 27, 45)     # dark backdrop
    $gold    = [System.Drawing.Color]::FromArgb(255, 227, 178, 60)   # straw
    $goldDk  = [System.Drawing.Color]::FromArgb(255, 184, 134, 43)   # straw shade / ring
    $red     = [System.Drawing.Color]::FromArgb(255, 192, 57, 43)    # hat band
    $white   = [System.Drawing.Color]::FromArgb(255, 245, 245, 245)  # bone
    $whiteDk = [System.Drawing.Color]::FromArgb(255, 210, 210, 210)  # bone shade
    $ink     = [System.Drawing.Color]::FromArgb(255, 20, 20, 24)     # features

    $bgBrush   = New-Object System.Drawing.SolidBrush $navy
    $goldBrush = New-Object System.Drawing.SolidBrush $gold
    $redBrush  = New-Object System.Drawing.SolidBrush $red
    $boneBrush = New-Object System.Drawing.SolidBrush $white
    $inkBrush  = New-Object System.Drawing.SolidBrush $ink
    $ringPen   = New-Object System.Drawing.Pen $goldDk, 8
    $boneOutline = New-Object System.Drawing.Pen $whiteDk, 3
    $goldOutline = New-Object System.Drawing.Pen $goldDk, 3

    # ---- Circular backdrop + gold ring ----
    $g.FillEllipse($bgBrush, 6, 6, 244, 244)
    $g.DrawEllipse($ringPen, 8, 8, 240, 240)

    # ---- Crossbones (behind skull) ----
    function Bone {
        param($angle)
        $st = $g.Save()
        $g.TranslateTransform(128, 150)
        $g.RotateTransform($angle)
        # shaft
        $g.FillRectangle($boneBrush, -95, -11, 190, 22)
        # knuckle ends (two lobes each side)
        $g.FillEllipse($boneBrush, -108, -22, 30, 30)
        $g.FillEllipse($boneBrush, -108,  -8, 30, 30)
        $g.FillEllipse($boneBrush,  78, -22, 30, 30)
        $g.FillEllipse($boneBrush,  78,  -8, 30, 30)
        $g.Restore($st)
    }
    Bone 45
    Bone -45

    # ---- Skull ----
    # cranium
    $g.FillEllipse($boneBrush, 74, 96, 108, 100)
    # jaw
    $g.FillRectangle($boneBrush, 100, 176, 56, 34)
    $g.FillEllipse($boneBrush, 100, 190, 56, 34)
    # eye sockets
    $g.FillEllipse($inkBrush, 92, 128, 32, 36)
    $g.FillEllipse($inkBrush, 132, 128, 32, 36)
    # nose
    $nose = New-Object System.Drawing.Drawing2D.GraphicsPath
    $nose.AddPolygon(@(
        (New-Object System.Drawing.PointF 128, 168),
        (New-Object System.Drawing.PointF 118, 184),
        (New-Object System.Drawing.PointF 138, 184)
    ))
    $g.FillPath($inkBrush, $nose)
    # teeth gaps
    $g.FillRectangle($inkBrush, 116, 196, 5, 16)
    $g.FillRectangle($inkBrush, 126, 196, 5, 16)
    $g.FillRectangle($inkBrush, 136, 196, 5, 16)

    # ---- Straw hat (on top of skull) ----
    # brim
    $g.FillEllipse($goldBrush, 34, 74, 188, 44)
    $g.DrawEllipse($goldOutline, 34, 74, 188, 44)
    # crown (dome) — clip to upper half
    $g.FillEllipse($goldBrush, 80, 40, 96, 72)
    # red band across base of crown
    $bandClip = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bandClip.AddEllipse(80, 40, 96, 72)
    $st2 = $g.Save()
    $g.SetClip($bandClip)
    $g.FillRectangle($redBrush, 78, 84, 100, 22)
    $g.Restore($st2)
    # re-stroke crown outline
    $g.DrawArc($goldOutline, 80, 40, 96, 72, 180, 180)

    $g.ResetTransform()
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngStreams = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    Draw-Jolly -g $g -N $s
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngStreams += ,$ms.ToArray()
    $bmp.Dispose()
    # also dump 256 as preview
    if ($s -eq 256) {
        [System.IO.File]::WriteAllBytes("$PSScriptRoot\preview.png", $ms.ToArray())
    }
    $ms.Dispose()
}

# ---- Build .ico container (PNG-compressed frames, Vista+ format) ----
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$count = $pngStreams.Count
$bw.Write([UInt16]0)      # reserved
$bw.Write([UInt16]1)      # type: icon
$bw.Write([UInt16]$count) # image count

$offset = 6 + (16 * $count)
for ($i = 0; $i -lt $count; $i++) {
    $s = $sizes[$i]
    $data = $pngStreams[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([Byte]$dim)   # width
    $bw.Write([Byte]$dim)   # height
    $bw.Write([Byte]0)      # palette
    $bw.Write([Byte]0)      # reserved
    $bw.Write([UInt16]1)    # color planes
    $bw.Write([UInt16]32)   # bpp
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($data in $pngStreams) { $bw.Write($data) }
$bw.Flush()

$target = $args[0]
[System.IO.File]::WriteAllBytes($target, $out.ToArray())
$out.Dispose()
Write-Host "Wrote $target ($($out.Length) bytes, $count frames)"
