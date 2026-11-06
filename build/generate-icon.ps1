# Generate ThermalTuner.ico (multi-size PNG-in-ICO, no external tooling).
# Usage: powershell -File build/generate-icon.ps1
# The icon is embedded into the exe via <ApplicationIcon> and used by the WPF window.

param(
    [string]$OutPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src/ThermalTuner.App/Assets/ThermalTuner.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-IconImage([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded-square background with a subtle diagonal gradient.
    $d = [int]($size * 0.22)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - $d, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d, $size - $d, $d, $d, 0, 90)
    $path.AddArc(0, $size - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $back = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)),
        (New-Object System.Drawing.Point($size, $size)),
        [System.Drawing.Color]::FromArgb(255, 15, 39, 64),
        [System.Drawing.Color]::FromArgb(255, 23, 82, 112))
    $g.FillPath($back, $path)
    $rim = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(160, 88, 176, 205), [single][Math]::Max(1, $size * 0.018))
    $g.DrawPath($rim, $path)

    # Baseline (the power/temperature axis).
    $axis = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(120, 120, 170, 200), [single][Math]::Max(1, $size * 0.025))
    $g.DrawLine($axis, [single]($size * 0.14), [single]($size * 0.82), [single]($size * 0.86), [single]($size * 0.82))

    # Rising thermal curve with a small ripple, then the set-point dot.
    $pts = @(
        (New-Object System.Drawing.PointF([single]($size * 0.16), [single]($size * 0.70))),
        (New-Object System.Drawing.PointF([single]($size * 0.34), [single]($size * 0.60))),
        (New-Object System.Drawing.PointF([single]($size * 0.50), [single]($size * 0.38))),
        (New-Object System.Drawing.PointF([single]($size * 0.66), [single]($size * 0.46))),
        (New-Object System.Drawing.PointF([single]($size * 0.84), [single]($size * 0.26)))
    )
    $curve = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 79, 209, 197), [single][Math]::Max(1, $size * 0.095))
    $curve.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $curve.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $curve.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawLines($curve, $pts)

    $r = [single]($size * 0.075)
    $dot = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 240, 250, 252))
    $g.FillEllipse($dot, [single]($size * 0.84 - $r), [single]($size * 0.26 - $r), [single]($r * 2), [single]($r * 2))

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose(); $g.Dispose(); $bmp.Dispose()
    return ,$bytes
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = @()
foreach ($s in $sizes) { $images += ,(New-IconImage $s) }

$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)
$bw.Write([uint16]0)                       # reserved
$bw.Write([uint16]1)                       # type = icon
$bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $w = if ($s -ge 256) { [byte]0 } else { [byte]$s }
    $bw.Write([byte]$w)                    # width  (0 = 256)
    $bw.Write([byte]$w)                    # height
    $bw.Write([byte]0)                     # palette
    $bw.Write([byte]0)                     # reserved
    $bw.Write([uint16]1)                   # color planes
    $bw.Write([uint16]32)                  # bits per pixel
    $bw.Write([uint32]$images[$i].Length)
    $bw.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Flush()

$dir = Split-Path -Parent $OutPath
if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
[System.IO.File]::WriteAllBytes($OutPath, $out.ToArray())
$bw.Dispose(); $out.Dispose()
"ICON=$OutPath ($([math]::Round((Get-Item -LiteralPath $OutPath).Length/1KB,1)) KB, sizes: $($sizes -join ','))"
