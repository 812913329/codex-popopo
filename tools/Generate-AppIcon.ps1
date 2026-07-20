param(
    [string] $OutputPath = (Join-Path $PSScriptRoot '..\src\CodexProfileLauncher\Assets\AppIcon.ico'),
    [string] $PreviewPath = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing.Common

function New-RoundedRectanglePath {
    param(
        [System.Drawing.RectangleF] $Rectangle,
        [float] $Radius
    )

    $diameter = $Radius * 2
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($Rectangle.X, $Rectangle.Y, $diameter, $diameter, 180, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Y, $diameter, $diameter, 270, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($Rectangle.X, $Rectangle.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconPngBytes {
    param([int] $Size)

    $scale = $Size / 256.0
    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $stream = [System.IO.MemoryStream]::new()
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)

        $backgroundRect = [System.Drawing.RectangleF]::new(12 * $scale, 12 * $scale, 232 * $scale, 232 * $scale)
        $backgroundPath = New-RoundedRectanglePath -Rectangle $backgroundRect -Radius (54 * $scale)
        $backgroundBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            $backgroundRect,
            [System.Drawing.Color]::FromArgb(255, 0, 120, 212),
            [System.Drawing.Color]::FromArgb(255, 0, 73, 145),
            45.0)
        try {
            $graphics.FillPath($backgroundBrush, $backgroundPath)
        }
        finally {
            $backgroundBrush.Dispose()
            $backgroundPath.Dispose()
        }

        $highlightBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            [System.Drawing.RectangleF]::new(26 * $scale, 22 * $scale, 200 * $scale, 98 * $scale),
            [System.Drawing.Color]::FromArgb(70, 255, 255, 255),
            [System.Drawing.Color]::FromArgb(0, 255, 255, 255),
            90.0)
        try {
            $graphics.FillEllipse($highlightBrush, 30 * $scale, 20 * $scale, 196 * $scale, 118 * $scale)
        }
        finally {
            $highlightBrush.Dispose()
        }

        $railPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(245, 255, 255, 255), [Math]::Max(2.0, 14 * $scale))
        $railPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $railPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        try {
            $graphics.DrawLine($railPen, 72 * $scale, 61 * $scale, 72 * $scale, 195 * $scale)
            $graphics.DrawLine($railPen, 128 * $scale, 48 * $scale, 128 * $scale, 208 * $scale)
            $graphics.DrawLine($railPen, 184 * $scale, 72 * $scale, 184 * $scale, 184 * $scale)
        }
        finally {
            $railPen.Dispose()
        }

        $nodeBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 0, 93, 178))
        $nodeBorder = [System.Drawing.Pen]::new([System.Drawing.Color]::White, [Math]::Max(1.5, 7 * $scale))
        try {
            foreach ($node in @(
                    @(72, 103),
                    @(128, 154),
                    @(184, 117))) {
                $diameter = 30 * $scale
                $x = ($node[0] * $scale) - ($diameter / 2)
                $y = ($node[1] * $scale) - ($diameter / 2)
                $graphics.FillEllipse($nodeBrush, $x, $y, $diameter, $diameter)
                $graphics.DrawEllipse($nodeBorder, $x, $y, $diameter, $diameter)
            }
        }
        finally {
            $nodeBorder.Dispose()
            $nodeBrush.Dispose()
        }

        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$stream.ToArray()
    }
    finally {
        $stream.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$sizes = @(256, 128, 64, 48, 32, 24, 16)
$frames = foreach ($size in $sizes) {
    [pscustomobject]@{ Size = $size; Bytes = [byte[]] (New-IconPngBytes -Size $size) }
}

if (-not [string]::IsNullOrWhiteSpace($PreviewPath)) {
    $previewTarget = [System.IO.Path]::GetFullPath($PreviewPath)
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($previewTarget)) | Out-Null
    [System.IO.File]::WriteAllBytes($previewTarget, $frames[0].Bytes)
}

$target = [System.IO.Path]::GetFullPath($OutputPath)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($target)) | Out-Null
$file = [System.IO.File]::Open($target, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16] 0)
    $writer.Write([uint16] 1)
    $writer.Write([uint16] $frames.Count)

    $offset = 6 + (16 * $frames.Count)
    foreach ($frame in $frames) {
        $writer.Write([byte] $(if ($frame.Size -eq 256) { 0 } else { $frame.Size }))
        $writer.Write([byte] $(if ($frame.Size -eq 256) { 0 } else { $frame.Size }))
        $writer.Write([byte] 0)
        $writer.Write([byte] 0)
        $writer.Write([uint16] 1)
        $writer.Write([uint16] 32)
        $writer.Write([uint32] $frame.Bytes.Length)
        $writer.Write([uint32] $offset)
        $offset += $frame.Bytes.Length
    }

    foreach ($frame in $frames) {
        $writer.Write($frame.Bytes)
    }
}
finally {
    $writer.Dispose()
    $file.Dispose()
}

Get-Item -LiteralPath $target | Select-Object FullName, Length, LastWriteTimeUtc
