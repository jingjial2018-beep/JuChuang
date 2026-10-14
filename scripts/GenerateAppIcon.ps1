param(
    [string]$InputPng = (Join-Path $PSScriptRoot '..\Assets\app-logo.png'),
    [string]$OutputIco = (Join-Path $PSScriptRoot '..\Assets\app.ico')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$source = [System.Drawing.Image]::FromFile((Resolve-Path $InputPng))
try {
    $sizes = @(16,24,32,48,64,128,256)
    $entries = [System.Collections.Generic.List[byte[]]]::new()
    foreach ($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new($size, $size)
        try {
            $gfx = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $gfx.Clear([System.Drawing.Color]::Transparent)
                $gfx.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $gfx.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $gfx.DrawImage($source, 0, 0, $size, $size)
            } finally { $gfx.Dispose() }
            $stream = [System.IO.MemoryStream]::new()
            try {
                $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
                $entries.Add($stream.ToArray())
            } finally { $stream.Dispose() }
        } finally { $bitmap.Dispose() }
    }
    $file = [System.IO.File]::Create($OutputIco)
    try {
        $writer = [System.IO.BinaryWriter]::new($file)
        try {
            $writer.Write([uint16]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]$sizes.Count)
            $offset = 6 + 16 * $sizes.Count
            for ($i=0; $i -lt $sizes.Count; $i++) {
                $size = $sizes[$i]
                $writer.Write([byte]($size % 256))
                $writer.Write([byte]($size % 256))
                $writer.Write([byte]0)
                $writer.Write([byte]0)
                $writer.Write([uint16]1)
                $writer.Write([uint16]32)
                $writer.Write([uint32]$entries[$i].Length)
                $writer.Write([uint32]$offset)
                $offset += $entries[$i].Length
            }
            foreach ($entry in $entries) { $writer.Write([byte[]]$entry) }
        } finally { $writer.Dispose() }
    } finally { $file.Dispose() }
} finally { $source.Dispose() }
