<#
.SYNOPSIS
  Generates the application icon and every image asset the app and the MSIX package need.

.DESCRIPTION
  The art is drawn here rather than kept as opaque binaries so the icon can be changed, reviewed
  and rebuilt: the repository stores the generated PNG/ICO *and* this script, and re-running it
  reproduces byte-identical shapes at every size instead of downscaling one bitmap (which is what
  makes a 16px taskbar icon look muddy).

  Design: a rounded badge with a violet-to-blue gradient and a white display with a play mark -
  the renderer receives media and plays it. Below 24px the display's ring would be thinner than a
  pixel, so those sizes carry the play mark alone: still legible in the notification area and the
  title bar.

.PARAMETER OutDir
  Destination for the generated files. Defaults to the app's Assets folder.

.PARAMETER Preview
  Also writes a comparison sheet of the small sizes into this directory (not part of the build).

.EXAMPLE
  pwsh tools/assets/make-icons.ps1
  pwsh tools/assets/make-icons.ps1 -Preview artifacts/icon-preview
#>
[CmdletBinding()]
param(
    [string]$OutDir = '',
    [string]$Preview = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Resolved here, not in the param block: PowerShell 5.1 does not populate $PSScriptRoot until the
# script body runs, so a default that uses it binds an empty string.
if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
    $repoRoot = Split-Path -Parent (Split-Path -Parent $scriptRoot)
    $OutDir = Join-Path $repoRoot 'src\DlnRenderer.App\Assets'
}
if ($Preview -and -not [System.IO.Path]::IsPathRooted($Preview)) {
    $Preview = Join-Path (Get-Location).Path $Preview
}

# --- palette ---------------------------------------------------------------------------------
# Violet to blue reads as "media" without borrowing any product's mark. The white glyph keeps its
# contrast on light and dark backgrounds alike.
$fromColor = [System.Drawing.Color]::FromArgb(255, 0x6A, 0x4B, 0xF5)
$toColor = [System.Drawing.Color]::FromArgb(255, 0x27, 0x60, 0xE8)

function New-RoundedPath {
    param([double]$X, [double]$Y, [double]$Width, [double]$Height, [double]$Radius)

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    if ($Radius -le 0.01) {
        $path.AddRectangle((New-Object System.Drawing.RectangleF($X, $Y, $Width, $Height)))
        return $path
    }

    $d = $Radius * 2
    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc($X + $Width - $d, $Y, $d, $d, 270, 90)
    $path.AddArc($X + $Width - $d, $Y + $Height - $d, $d, $d, 0, 90)
    $path.AddArc($X, $Y + $Height - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

# Geometry is authored in a 0..1 space inside the badge rect, so one description serves 16px and
# 1240px without a separate set of coordinates.
$screenRect = @{ X = 0.180; Y = 0.255; W = 0.640; H = 0.420; R = 0.050 }
$playMark = @(
    @{ X = 0.440; Y = 0.360 },
    @{ X = 0.440; Y = 0.570 },
    @{ X = 0.610; Y = 0.465 }
)

function Get-PointIn {
    param([hashtable]$Point, [System.Drawing.RectangleF]$Rect)
    New-Object System.Drawing.PointF(
        ($Rect.X + $Point.X * $Rect.Width),
        ($Rect.Y + $Point.Y * $Rect.Height))
}

function Draw-Badge {
    param([System.Drawing.Graphics]$Graphics, [System.Drawing.RectangleF]$Rect)

    $radius = $Rect.Width * 0.225
    $badge = New-RoundedPath $Rect.X $Rect.Y $Rect.Width $Rect.Height $radius

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $Rect, $fromColor, $toColor, [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
    $Graphics.FillPath($brush, $badge)
    $brush.Dispose()

    # A soft highlight across the upper half: without it a flat gradient reads as a sticker at
    # large sizes (splash screen, Store tile).
    $Graphics.SetClip($badge)
    $glow = New-RoundedPath ($Rect.X - $Rect.Width * 0.3) ($Rect.Y - $Rect.Height * 0.55) ($Rect.Width * 1.2) ($Rect.Height * 1.05) 0
    $glowBrush = New-Object System.Drawing.Drawing2D.PathGradientBrush($glow)
    $glowBrush.CenterColor = [System.Drawing.Color]::FromArgb(44, 255, 255, 255)
    $glowBrush.SurroundColors = [System.Drawing.Color[]]@([System.Drawing.Color]::FromArgb(0, 255, 255, 255))
    $Graphics.FillPath($glowBrush, $glow)
    $glowBrush.Dispose()
    $glow.Dispose()
    $Graphics.ResetClip()

    $badge.Dispose()
}

# The play mark is one shape at three scales: printed inside the ring, punched out of the filled
# display (where it has to be larger to survive antialiasing), and standing alone at the smallest
# sizes. Off-centre x by a couple of percent because a triangle centred by its bounding box looks
# shifted left to the eye.
function Get-MarkPoints {
    param(
        [System.Drawing.RectangleF]$Rect,
        [double]$Scale = 1.0,
        [double]$CenterX = 0.525,
        [double]$CenterY = 0.465)

    $cx = $Rect.X + $Rect.Width * $CenterX
    $cy = $Rect.Y + $Rect.Height * $CenterY
    $halfHeight = $Rect.Height * 0.105 * $Scale
    $left = $cx - $Rect.Width * 0.085 * $Scale
    $right = $cx + $Rect.Width * 0.085 * $Scale

    return [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF(([single]$left), ([single]($cy - $halfHeight)))),
        (New-Object System.Drawing.PointF(([single]$left), ([single]($cy + $halfHeight)))),
        (New-Object System.Drawing.PointF(([single]$right), ([single]$cy))))
}

function Draw-Glyph {
    param([System.Drawing.Graphics]$Graphics, [System.Drawing.RectangleF]$Rect, [string]$Mode = 'Ring')

    $white = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)

    # Mark only: the smallest sizes get one bold shape instead of a display whose ring would be a
    # sub-pixel line. A play mark on the gradient reads at 16px where nested shapes blur together.
    if ($Mode -eq 'Mark') {
        $markOnly = New-Object System.Drawing.Drawing2D.GraphicsPath
        $markOnly.AddPolygon((Get-MarkPoints $Rect 1.85 0.5 0.5))
        $brush = New-Object System.Drawing.SolidBrush($white)
        $Graphics.FillPath($brush, $markOnly)
        $brush.Dispose()
        $markOnly.Dispose()
        return
    }

    $screen = New-Object System.Drawing.RectangleF(
        ($Rect.X + $screenRect.X * $Rect.Width),
        ($Rect.Y + $screenRect.Y * $Rect.Height),
        ($screenRect.W * $Rect.Width),
        ($screenRect.H * $Rect.Height))
    $screenRadius = $screenRect.R * $Rect.Width

    # Outlined display plus a solid play mark: the ring looks like an edge rather than a border.
    $stroke = [Math]::Max(1.6, $Rect.Width * 0.05)
    $pen = New-Object System.Drawing.Pen($white, ([single]$stroke))
    $pen.Alignment = [System.Drawing.Drawing2D.PenAlignment]::Inset
    $ring = New-RoundedPath $screen.X $screen.Y $screen.Width $screen.Height $screenRadius
    $Graphics.DrawPath($pen, $ring)
    $pen.Dispose()
    $ring.Dispose()

    $mark = New-Object System.Drawing.Drawing2D.GraphicsPath
    $mark.AddPolygon((Get-MarkPoints $Rect 1.0 0.525 0.465))
    $brush = New-Object System.Drawing.SolidBrush($white)
    $Graphics.FillPath($brush, $mark)
    $brush.Dispose()
    $mark.Dispose()
}

# Which glyph treatment a given pixel size gets. Below 24 the display's ring lands under a pixel
# and the whole thing reads as a grey smudge, so those sizes carry the play mark alone; from 24 up
# the ring holds and the two-shape silhouette is sharper than a bare triangle.
function Get-GlyphMode {
    param([int]$Size)

    if ($Size -lt 24) { return 'Mark' }
    return 'Ring'
}

function New-IconBitmap {
    param([int]$Size, [double]$Padding = 0.0, [string]$Mode = '')

    $bitmap = New-Object System.Drawing.Bitmap(
        $Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $inset = $Size * $Padding
    $rect = New-Object System.Drawing.RectangleF(
        ([single]$inset), ([single]$inset), ([single]($Size - 2 * $inset)), ([single]($Size - 2 * $inset)))

    if ([string]::IsNullOrEmpty($Mode)) { $Mode = Get-GlyphMode -Size $Size }

    Draw-Badge $graphics $rect
    Draw-Glyph $graphics $rect $Mode

    $graphics.Dispose()
    return $bitmap
}

function Save-Png {
    param([System.Drawing.Bitmap]$Bitmap, [string]$Path)

    $Bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host ("  {0,-58} {1}x{2}" -f (Split-Path -Leaf $Path), $Bitmap.Width, $Bitmap.Height)
}

# --- ICO --------------------------------------------------------------------------------------
# Frames are written as 32bpp DIBs (not PNG payloads): every Windows version, WIC and the shell
# read DIB frames, which is what the title bar and the tray both go through.
function Get-DibFrame {
    param([System.Drawing.Bitmap]$Bitmap)

    $size = $Bitmap.Width
    $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $data = $Bitmap.LockBits(
        $rect,
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    try {
        $pixels = New-Object byte[] ($data.Stride * $size)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    }
    finally {
        $Bitmap.UnlockBits($data)
    }

    # AND mask: one bit per pixel, rows padded to 4 bytes. The alpha channel carries the shape, so
    # it stays zero (fully opaque) and old code paths that ignore alpha still get the full square.
    $maskStride = [int]([Math]::Floor(($size + 31) / 32) * 4)
    $mask = New-Object byte[] ($maskStride * $size)

    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)

    # BITMAPINFOHEADER. Height is doubled because the format describes XOR and AND masks as one
    # image; getting this wrong shows up as a sheared icon, not as an error.
    $writer.Write([int]40)
    $writer.Write([int]$size)
    $writer.Write([int]($size * 2))
    $writer.Write([int16]1)
    $writer.Write([int16]32)
    $writer.Write([int]0)
    $writer.Write([int]($size * $size * 4))
    $writer.Write([int]0)
    $writer.Write([int]0)
    $writer.Write([int]0)
    $writer.Write([int]0)

    # Bottom-up rows, BGRA as stored in the bitmap.
    for ($y = $size - 1; $y -ge 0; $y--) {
        $writer.Write($pixels, $y * $data.Stride, $size * 4)
    }

    $writer.Write($mask)
    $writer.Flush()
    $bytes = $stream.ToArray()
    $writer.Dispose()
    $stream.Dispose()
    return $bytes
}

function Save-Ico {
    param([string]$Path, [int[]]$Sizes)

    $frames = @()
    foreach ($size in $Sizes) {
        $bitmap = New-IconBitmap -Size $size
        $frames += , @{ Size = $size; Data = (Get-DibFrame $bitmap) }
        $bitmap.Dispose()
    }

    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)

    $writer.Write([int16]0)             # reserved
    $writer.Write([int16]1)             # type: icon
    $writer.Write([int16]$frames.Count)

    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = $frame.Size
        if ($dimension -ge 256) { $dimension = 0 }   # 256 is encoded as 0
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)          # colour count
        $writer.Write([byte]0)          # reserved
        $writer.Write([int16]1)         # planes
        $writer.Write([int16]32)        # bits per pixel
        $writer.Write([int]$frame.Data.Length)
        $writer.Write([int]$offset)
        $offset += $frame.Data.Length
    }

    foreach ($frame in $frames) {
        $writer.Write([byte[]]$frame.Data)
    }

    $writer.Flush()
    [System.IO.File]::WriteAllBytes($Path, $stream.ToArray())
    $writer.Dispose()
    $stream.Dispose()

    $length = (Get-Item -LiteralPath $Path).Length
    Write-Host ("  {0,-58} {1} sizes, {2:N0} KB" -f (Split-Path -Leaf $Path), $frames.Count, ($length / 1KB))
}

# --- run --------------------------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $OutDir)) {
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
}

Write-Host "==> app icon"
Save-Ico -Path (Join-Path $OutDir 'AppIcon.ico') -Sizes @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)

Write-Host "==> MSIX tiles and splash"
# Names and scale factors follow what the manifest's asset references resolve to: the packager picks
# the qualified file for the current DPI, so the sizes here must match the qualifier in the name.
# Width x Height are the pixel dimensions of the named scale/targetsize variant, not the logical
# tile size; the splash and the wide tile are not square, so the badge is sized from the short edge.
$assets = @(
    @{ Name = 'Square44x44Logo.scale-200.png'; W = 88; H = 88; Padding = 0.0 },
    @{ Name = 'Square44x44Logo.targetsize-24_altform-unplated.png'; W = 24; H = 24; Padding = 0.0 },
    @{ Name = 'Square44x44Logo.targetsize-48_altform-lightunplated.png'; W = 48; H = 48; Padding = 0.0 },
    @{ Name = 'Square150x150Logo.scale-200.png'; W = 300; H = 300; Padding = 0.14 },
    @{ Name = 'Wide310x150Logo.scale-200.png'; W = 620; H = 300; Padding = 0.18 },
    @{ Name = 'SplashScreen.scale-200.png'; W = 1240; H = 600; Padding = 0.30 },
    @{ Name = 'StoreLogo.png'; W = 50; H = 50; Padding = 0.0 },
    @{ Name = 'LockScreenLogo.scale-200.png'; W = 48; H = 48; Padding = 0.0 }
)

foreach ($asset in $assets) {
    $width = [int]$asset['W']
    $height = [int]$asset['H']
    $padding = [double]$asset['Padding']

    $bitmap = New-Object System.Drawing.Bitmap(
        $width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $edge = [Math]::Min($width, $height)
    $badgeSize = $edge * (1 - 2 * $padding)
    $rect = New-Object System.Drawing.RectangleF(
        ([single](($width - $badgeSize) / 2)),
        ([single](($height - $badgeSize) / 2)),
        ([single]$badgeSize),
        ([single]$badgeSize))

    Draw-Badge $graphics $rect
    Draw-Glyph $graphics $rect (Get-GlyphMode -Size $edge)
    $graphics.Dispose()

    Save-Png -Bitmap $bitmap -Path (Join-Path $OutDir $asset['Name'])
    $bitmap.Dispose()
}

if ($Preview) {
    if (-not (Test-Path -LiteralPath $Preview)) {
        New-Item -ItemType Directory -Force -Path $Preview | Out-Null
    }

    Write-Host "==> preview sheet -> $Preview"
    foreach ($size in @(16, 20, 24, 32, 48, 256)) {
        foreach ($mode in @('Ring', 'Mark')) {
            $bitmap = New-IconBitmap -Size $size -Mode $mode
            $name = "icon-{0}-{1}.png" -f $size, $mode.ToLowerInvariant()
            Save-Png -Bitmap $bitmap -Path (Join-Path $Preview $name)
            $bitmap.Dispose()
        }
    }
}

Write-Host ''
Write-Host "icons written to $OutDir" -ForegroundColor Green
