# Generates the Arkimentum AppMonitor tray/app icons as multi-size .ico files (32-bit BGRA, uncompressed DIB frames).
#
# Artwork follows the Arkimentum favicon: a square of off-white paper, a thin terracotta bar over the top-left
# half and a sky-blue bar over the top-right half, and a line-art globe (outline + meridians + parallels) in
# grey-olive with a solid dot at its lower right. The tray variants add a status dot in the lower-right corner:
#   normal    = the globe alone
#   updates   = + gold  #C7CA5C dot
#   attention = + terracotta #C76239 dot
#
# Small frames (16-24 px) swap the line-art globe for a solid grey-olive disc with the meridian and equator cut
# out of it, which keeps the silhouette but stays legible at tray sizes.
#
# Usage: pwsh -File tools/make-icons.ps1 -OutDir Assets
param([Parameter(Mandatory=$true)][string]$OutDir)

Add-Type -AssemblyName System.Drawing

$sizes = @(16,20,24,32,48,256)

# --- brand palette ------------------------------------------------------------------------------------------
$ColPaper      = [System.Drawing.Color]::FromArgb(255, 245, 241, 238)   # #F5F1EE
$ColTerracotta = [System.Drawing.Color]::FromArgb(255, 199,  98,  57)   # #C76239
$ColSky        = [System.Drawing.Color]::FromArgb(255,  56, 157, 198)   # #389DC6
$ColGlobe      = [System.Drawing.Color]::FromArgb(255, 125, 123, 107)   # #7D7B6B
$ColGold       = [System.Drawing.Color]::FromArgb(255, 199, 202,  92)   # #C7CA5C
$ColInk        = [System.Drawing.Color]::FromArgb(255,  42,  46,  34)   # #2A2E22
$ColGlobeSmall = [System.Drawing.Color]::FromArgb(255, 107, 105,  89)   # #6B6959 (16-24 px)

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    if ($r -le 0.01) { $p.AddRectangle((New-Object System.Drawing.RectangleF($x, $y, $w, $h))); return $p }
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-Frame([int]$s, [string]$badge) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    $f = [single]$s
    $tiny = ($s -le 24)

    # ---- paper tile (full bleed, rounded) ---------------------------------------------------------------
    $radius = if ($tiny) { $f * 0.12 } else { $f * 0.16 }
    $tile = New-RoundedPath 0.0 0.0 $f $f $radius
    $paper = New-Object System.Drawing.SolidBrush($ColPaper)
    $g.FillPath($paper, $tile)
    $paper.Dispose()

    # everything else stays inside the rounded square
    $g.SetClip($tile)

    # ---- the two brand bars across the top (each ~12% of the height) ------------------------------------
    $barH = [single]([math]::Max(2.0, $f * 0.12))
    $mid  = $f / 2.0
    $bt = New-Object System.Drawing.SolidBrush($ColTerracotta)
    $bs = New-Object System.Drawing.SolidBrush($ColSky)
    $g.FillRectangle($bt, 0.0, 0.0, $mid, $barH)
    $g.FillRectangle($bs, $mid, 0.0, ($f - $mid), $barH)
    $bt.Dispose(); $bs.Dispose()

    # ---- line-art globe ---------------------------------------------------------------------------------
    # Centred in the area below the bars, so it never touches the tile edge.
    $area = $f - $barH
    $cx = $f * 0.50
    $cy = $barH + $area * 0.50
    $r  = if ($tiny) { $area * 0.38 } else { $area * 0.345 }

    if ($tiny) {
        # 16-24 px: hairline line-art collapses into mush, so the globe becomes a solid grey-olive disc with
        # the meridian and the equator cut out of it in paper. Same silhouette, far more legible in the tray.
        $disc = New-Object System.Drawing.SolidBrush($ColGlobeSmall)
        $g.FillEllipse($disc, ($cx - $r), ($cy - $r), ($r * 2), ($r * 2))
        $disc.Dispose()

        $cutW = [single]([math]::Max(1.15, $f * 0.075))
        $cut = New-Object System.Drawing.Pen($ColPaper, $cutW)
        $g.DrawLine($cut, ($cx - $r), $cy, ($cx + $r), $cy)
        $g.DrawLine($cut, $cx, ($cy - $r), $cx, ($cy + $r))
        $cut.Dispose()
    } else {
        $pen = New-Object System.Drawing.Pen($ColGlobe, [single]($f * 0.035))
        $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round

        # outline, central meridian, equator
        $g.DrawEllipse($pen, ($cx - $r), ($cy - $r), ($r * 2), ($r * 2))
        $g.DrawLine($pen, ($cx - $r), $cy, ($cx + $r), $cy)
        $g.DrawLine($pen, $cx, ($cy - $r), $cx, ($cy + $r))

        # side meridians as a narrow ellipse, plus two parallels
        $mw = $r * 0.92
        $g.DrawEllipse($pen, ($cx - $mw / 2.0), ($cy - $r), $mw, ($r * 2))
        $py = $r * 0.55
        $px = [single]([math]::Sqrt([math]::Max(0.0, ($r * $r) - ($py * $py))))
        $g.DrawLine($pen, ($cx - $px), ($cy - $py), ($cx + $px), ($cy - $py))
        $g.DrawLine($pen, ($cx - $px), ($cy + $py), ($cx + $px), ($cy + $py))
        $pen.Dispose()
    }

    if ($badge -eq 'none') {
        # solid grey-olive dot at the globe's lower right (the favicon's mark)
        if (-not $tiny) {
            $dotR = $f * 0.055
            $dotX = $cx + $r * 0.60
            $dotY = $cy + $r * 0.60
            $ring = New-Object System.Drawing.SolidBrush($ColPaper)
            $g.FillEllipse($ring, ($dotX - $dotR * 1.45), ($dotY - $dotR * 1.45), ($dotR * 2.90), ($dotR * 2.90))
            $ring.Dispose()
            $solid = New-Object System.Drawing.SolidBrush($ColGlobe)
            $g.FillEllipse($solid, ($dotX - $dotR), ($dotY - $dotR), ($dotR * 2), ($dotR * 2))
            $solid.Dispose()
        }
    } else {
        # ---- status badge: replaces the globe's own dot ---------------------------------------------
        $col = if ($badge -eq 'attention') { $ColTerracotta } else { $ColGold }
        $br  = if ($tiny) { $f * 0.21 } else { $f * 0.17 }
        $bcx = $f - $br * 1.08
        $bcy = $f - $br * 1.08
        # paper rim so the dot separates from the globe behind it
        $rim = New-Object System.Drawing.SolidBrush($ColPaper)
        $g.FillEllipse($rim, ($bcx - $br * 1.14), ($bcy - $br * 1.14), ($br * 2.28), ($br * 2.28))
        $rim.Dispose()
        $dot = New-Object System.Drawing.SolidBrush($col)
        $g.FillEllipse($dot, ($bcx - $br), ($bcy - $br), ($br * 2), ($br * 2))
        $dot.Dispose()
        if ($badge -eq 'updates') {
            # gold is light: give it an ink hairline so it survives on a light taskbar
            $bp = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(200, $ColInk.R, $ColInk.G, $ColInk.B), [single]([math]::Max(0.75, $f * 0.013)))
            $g.DrawEllipse($bp, ($bcx - $br), ($bcy - $br), ($br * 2), ($br * 2))
            $bp.Dispose()
        }
    }

    $g.ResetClip()
    $tile.Dispose()
    $g.Dispose()
    return $bmp
}

function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $data = $bmp.LockBits((New-Object System.Drawing.Rectangle(0, 0, $w, $h)),
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $buf = New-Object byte[] ($stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buf, 0, $buf.Length)
    $bmp.UnlockBits($data)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    # BITMAPINFOHEADER
    $bw.Write([uint32]40); $bw.Write([int32]$w); $bw.Write([int32]($h * 2))
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
    $bw.Write([uint32]($w * $h * 4)); $bw.Write([int32]0); $bw.Write([int32]0)
    $bw.Write([uint32]0); $bw.Write([uint32]0)
    # XOR data, bottom-up BGRA
    for ($y = $h - 1; $y -ge 0; $y--) { $bw.Write($buf, $y * $stride, $w * 4) }
    # AND mask, 1bpp bottom-up, rows padded to 4 bytes, all zero (alpha carries transparency)
    $maskStride = [math]::Floor((($w + 31) / 32)) * 4
    $zero = New-Object byte[] ($maskStride * $h)
    $bw.Write($zero, 0, $zero.Length)
    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    return ,$bytes
}

function Write-Ico([string]$path, [string]$badge) {
    $frames = @()
    foreach ($s in $sizes) {
        $bmp = New-Frame $s $badge
        $frames += ,@{ Size = $s; Bytes = (Get-DibBytes $bmp) }
        $bmp.Dispose()
    }
    $fs = [System.IO.File]::Create($path)
    $bw = New-Object System.IO.BinaryWriter($fs)
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($fr in $frames) {
        $dim = if ($fr.Size -ge 256) { 0 } else { $fr.Size }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$fr.Bytes.Length); $bw.Write([uint32]$offset)
        $offset += $fr.Bytes.Length
    }
    foreach ($fr in $frames) { $bw.Write($fr.Bytes, 0, $fr.Bytes.Length) }
    $bw.Flush(); $bw.Dispose(); $fs.Dispose()
    Write-Output ("wrote {0} ({1} bytes)" -f $path, (Get-Item $path).Length)
}

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
Write-Ico (Join-Path $OutDir 'app.ico') 'none'
Write-Ico (Join-Path $OutDir 'tray-normal.ico') 'none'
Write-Ico (Join-Path $OutDir 'tray-updates.ico') 'updates'
Write-Ico (Join-Path $OutDir 'tray-attention.ico') 'attention'
