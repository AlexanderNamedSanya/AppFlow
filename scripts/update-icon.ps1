param(
    [string]$Source = (Join-Path $PSScriptRoot '..\src\AppFlow.Desktop\Assets\AppFlow.png')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sourcePath = (Resolve-Path -LiteralPath $Source).Path
$iconPath = Join-Path $PSScriptRoot '..\src\AppFlow.Desktop\Assets\AppFlow.ico'
$sourceImage = [System.Drawing.Image]::FromFile($sourcePath)
try {
    $sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
    $frames = foreach ($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $stream = [System.IO.MemoryStream]::new()
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $attributes = [System.Drawing.Imaging.ImageAttributes]::new()
            try {
                $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
                $graphics.DrawImage($sourceImage, [System.Drawing.Rectangle]::new(0, 0, $size, $size), 0, 0, $sourceImage.Width, $sourceImage.Height, [System.Drawing.GraphicsUnit]::Pixel, $attributes)
            }
            finally { $attributes.Dispose() }
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
        }
        finally { $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
    $output = [System.IO.MemoryStream]::new()
    $writer = [System.IO.BinaryWriter]::new($output)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$frames.Count)
        $offset = 6 + 16 * $frames.Count
        foreach ($frame in $frames) {
            $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$frame.Bytes.Length)
            $writer.Write([uint32]$offset)
            $offset += $frame.Bytes.Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
        $writer.Flush()
        [System.IO.File]::WriteAllBytes($iconPath, $output.ToArray())
    }
    finally { $writer.Dispose(); $output.Dispose() }
}
finally { $sourceImage.Dispose() }
Write-Host "Icon: $iconPath ($($sizes -join ', ') px)"
