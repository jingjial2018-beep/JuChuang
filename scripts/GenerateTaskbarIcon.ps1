$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sourcePath = Join-Path $PSScriptRoot '..\Assets\app-logo.png'
$tempPath = Join-Path $PSScriptRoot '..\Assets\taskbar-logo.generated.png'
$outputPath = Join-Path $PSScriptRoot '..\Assets\taskbar.ico'
$source = [System.Drawing.Bitmap]::new((Resolve-Path $sourcePath).Path)
try {
    $left=$source.Width; $top=$source.Height; $right=-1; $bottom=-1
    for($y=0; $y -lt $source.Height; $y++) {
        for($x=0; $x -lt $source.Width; $x++) {
            if($source.GetPixel($x,$y).A -gt 16) {
                $left=[Math]::Min($left,$x); $right=[Math]::Max($right,$x)
                $top=[Math]::Min($top,$y); $bottom=[Math]::Max($bottom,$y)
            }
        }
    }
    $bitmap = [System.Drawing.Bitmap]::new($source.Width,$source.Height)
    try {
        $gfx=[System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $gfx.Clear([System.Drawing.Color]::Transparent)
            $gfx.InterpolationMode=[System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $gfx.SmoothingMode=[System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            if($right -ge $left -and $bottom -ge $top) {
                $w=$right-$left+1; $h=$bottom-$top+1
                # Preserve source design while increasing apparent taskbar footprint by 12%.
                $destW=$w*1.12; $destH=$h*1.12
                $rect=[System.Drawing.RectangleF]::new(
                    [single](($bitmap.Width-$destW)/2),
                    [single](($bitmap.Height-$destH)/2),
                    [single]$destW,[single]$destH)
                $srcRect=[System.Drawing.RectangleF]::new([single]$left,[single]$top,[single]$w,[single]$h)
                $gfx.DrawImage($source,$rect,$srcRect,[System.Drawing.GraphicsUnit]::Pixel)
            }
        } finally { $gfx.Dispose() }
        $bitmap.Save($tempPath,[System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
} finally { $source.Dispose() }
try {
    & (Join-Path $PSScriptRoot 'GenerateAppIcon.ps1') -InputPng $tempPath -OutputIco $outputPath
    if($LASTEXITCODE -ne 0) { throw 'Taskbar icon generation failed' }
} finally {
    Remove-Item $tempPath -ErrorAction SilentlyContinue
}
