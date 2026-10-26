# Картинки для Discord Rich Presence (квадрат 1024×1024, прозрачный фон).
# Загрузите их в Discord Developer Portal → ваше приложение → Rich Presence → Art Assets
# под ИМЕНАМИ ФАЙЛОВ без .png (frog_logo, playing, download, java) — эти ключи использует лаунчер.
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "assets\discord"
New-Item -ItemType Directory -Force $out | Out-Null

$map = @{
    "frog_logo" = "assets\out\main\logo.png"
    "playing"   = "assets\out\ui\frog_cool.png"
    "download"  = "assets\out\main\loader.png"
    "java"      = "assets\out\ui\java.png"
}

foreach ($key in $map.Keys) {
    $src = [System.Drawing.Image]::FromFile((Join-Path $root $map[$key]))
    $size = 1024; $pad = 48
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $k = [Math]::Min(($size - 2 * $pad) / $src.Width, ($size - 2 * $pad) / $src.Height)
    $w = $src.Width * $k; $h = $src.Height * $k
    $g.DrawImage($src, ($size - $w) / 2, ($size - $h) / 2, $w, $h)
    $bmp.Save((Join-Path $out "$key.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose(); $src.Dispose()
    "assets\discord\$key.png"
}
