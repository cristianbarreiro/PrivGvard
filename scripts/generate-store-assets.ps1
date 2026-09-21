# =====================================================================
# PrivGvard - Generate Microsoft Store / MSIX Visual Assets
# =====================================================================

param (
    [string]$SourceLogoPath = "$PSScriptRoot\..\assets\logo\privgvard_logo.png",
    [string]$OutputDir = "$PSScriptRoot\..\packaging\Assets"
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

if (-not (Test-Path -LiteralPath $SourceLogoPath)) {
    throw "Source logo not found at: $SourceLogoPath"
}

if (-not (Test-Path -LiteralPath $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$sourceImg = [System.Drawing.Image]::FromFile($SourceLogoPath)

# Bounding box of the PrivGvard emblem in the 1024x1024 master image:
# minX = 50, minY = 50, maxX = 973, maxY = 973 -> 924x924 px
$SymbolSrcRect = New-Object System.Drawing.Rectangle(50, 50, 924, 924)

function Resize-IconSymbol {
    param (
        [System.Drawing.Image]$Source,
        [int]$TargetWidth,
        [int]$TargetHeight,
        [string]$DestinationPath,
        [float]$PaddingRatio = 0.04 # 4% optical padding for anti-aliasing margin
    )

    $bmp = New-Object System.Drawing.Bitmap($TargetWidth, $TargetHeight, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $margin = [Math]::Max(1, [int][Math]::Round($TargetWidth * $PaddingRatio))
    $destWidth = $TargetWidth - (2 * $margin)
    $destHeight = $TargetHeight - (2 * $margin)
    $destRect = New-Object System.Drawing.Rectangle($margin, $margin, $destWidth, $destHeight)

    $g.DrawImage($Source, $destRect, $SymbolSrcRect, [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()

    $bmp.Save($DestinationPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "Generated: $([System.IO.Path]::GetFileName($DestinationPath)) ($($TargetWidth)x$($TargetHeight))" -ForegroundColor DarkGray
}

function Resize-ImageCentered {
    param (
        [System.Drawing.Image]$Source,
        [int]$TargetWidth,
        [int]$TargetHeight,
        [string]$DestinationPath,
        [bool]$IsWide = $false
    )

    $bmp = New-Object System.Drawing.Bitmap($TargetWidth, $TargetHeight, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    if ($IsWide) {
        # Fit square logo centered vertically within wide canvas
        $side = [int]($TargetHeight * 0.85)
        $offsetX = [int](($TargetWidth - $side) / 2)
        $offsetY = [int](($TargetHeight - $side) / 2)
        $destRect = New-Object System.Drawing.Rectangle($offsetX, $offsetY, $side, $side)
    } else {
        $destRect = New-Object System.Drawing.Rectangle(0, 0, $TargetWidth, $TargetHeight)
    }

    $srcRect = New-Object System.Drawing.Rectangle(0, 0, $Source.Width, $Source.Height)
    $g.DrawImage($Source, $destRect, $srcRect, [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()

    $bmp.Save($DestinationPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "Generated: $([System.IO.Path]::GetFileName($DestinationPath)) ($($TargetWidth)x$($TargetHeight))" -ForegroundColor DarkGray
}

Write-Host "Generating Store visual assets from: $SourceLogoPath" -ForegroundColor Cyan

# 1. Square44x44 - App list, taskbar, task switcher, search, shortcuts
# Scale-based variants (for Start menu and general scale contexts)
Resize-IconSymbol $sourceImg 44 44 (Join-Path $OutputDir "Square44x44Logo.png")
Resize-IconSymbol $sourceImg 44 44 (Join-Path $OutputDir "Square44x44Logo.scale-100.png")
Resize-IconSymbol $sourceImg 55 55 (Join-Path $OutputDir "Square44x44Logo.scale-125.png")
Resize-IconSymbol $sourceImg 66 66 (Join-Path $OutputDir "Square44x44Logo.scale-150.png")
Resize-IconSymbol $sourceImg 88 88 (Join-Path $OutputDir "Square44x44Logo.scale-200.png")
Resize-IconSymbol $sourceImg 176 176 (Join-Path $OutputDir "Square44x44Logo.scale-400.png")

# Targetsize variants (for Taskbar, Desktop shortcuts, Task Switcher, Start all-apps list)
# Generates plated, unplated (_altform-unplated), and light-unplated (_altform-lightunplated) assets
$targetSizes = @(16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256)
foreach ($size in $targetSizes) {
    Resize-IconSymbol $sourceImg $size $size (Join-Path $OutputDir "Square44x44Logo.targetsize-$size.png")
    Resize-IconSymbol $sourceImg $size $size (Join-Path $OutputDir "Square44x44Logo.targetsize-${size}_altform-unplated.png")
    Resize-IconSymbol $sourceImg $size $size (Join-Path $OutputDir "Square44x44Logo.targetsize-${size}_altform-lightunplated.png")
}

# 2. Square71x71
Resize-ImageCentered $sourceImg 71 71 (Join-Path $OutputDir "Square71x71Logo.png")
Resize-ImageCentered $sourceImg 71 71 (Join-Path $OutputDir "Square71x71Logo.scale-100.png")
Resize-ImageCentered $sourceImg 142 142 (Join-Path $OutputDir "Square71x71Logo.scale-200.png")

# 3. Square150x150
Resize-ImageCentered $sourceImg 150 150 (Join-Path $OutputDir "Square150x150Logo.png")
Resize-ImageCentered $sourceImg 150 150 (Join-Path $OutputDir "Square150x150Logo.scale-100.png")
Resize-ImageCentered $sourceImg 188 188 (Join-Path $OutputDir "Square150x150Logo.scale-125.png")
Resize-ImageCentered $sourceImg 225 225 (Join-Path $OutputDir "Square150x150Logo.scale-150.png")
Resize-ImageCentered $sourceImg 300 300 (Join-Path $OutputDir "Square150x150Logo.scale-200.png")
Resize-ImageCentered $sourceImg 600 600 (Join-Path $OutputDir "Square150x150Logo.scale-400.png")

# 4. Square310x310
Resize-ImageCentered $sourceImg 310 310 (Join-Path $OutputDir "Square310x310Logo.png")
Resize-ImageCentered $sourceImg 310 310 (Join-Path $OutputDir "Square310x310Logo.scale-100.png")
Resize-ImageCentered $sourceImg 620 620 (Join-Path $OutputDir "Square310x310Logo.scale-200.png")

# 5. Wide310x150
Resize-ImageCentered $sourceImg 310 150 (Join-Path $OutputDir "Wide310x150Logo.png") -IsWide $true
Resize-ImageCentered $sourceImg 310 150 (Join-Path $OutputDir "Wide310x150Logo.scale-100.png") -IsWide $true
Resize-ImageCentered $sourceImg 620 300 (Join-Path $OutputDir "Wide310x150Logo.scale-200.png") -IsWide $true

# 6. StoreLogo (50x50)
Resize-ImageCentered $sourceImg 50 50 (Join-Path $OutputDir "StoreLogo.png")
Resize-ImageCentered $sourceImg 50 50 (Join-Path $OutputDir "StoreLogo.scale-100.png")
Resize-ImageCentered $sourceImg 63 63 (Join-Path $OutputDir "StoreLogo.scale-125.png")
Resize-ImageCentered $sourceImg 75 75 (Join-Path $OutputDir "StoreLogo.scale-150.png")
Resize-ImageCentered $sourceImg 100 100 (Join-Path $OutputDir "StoreLogo.scale-200.png")
Resize-ImageCentered $sourceImg 200 200 (Join-Path $OutputDir "StoreLogo.scale-400.png")

# 7. SplashScreen (620x300)
Resize-ImageCentered $sourceImg 620 300 (Join-Path $OutputDir "SplashScreen.png") -IsWide $true
Resize-ImageCentered $sourceImg 620 300 (Join-Path $OutputDir "SplashScreen.scale-100.png") -IsWide $true
Resize-ImageCentered $sourceImg 1240 600 (Join-Path $OutputDir "SplashScreen.scale-200.png") -IsWide $true

$sourceImg.Dispose()
Write-Host "`nAll Store visual assets generated successfully in: $OutputDir" -ForegroundColor Green
