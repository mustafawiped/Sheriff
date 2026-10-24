$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class SheriffIconNative { [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon); }'
$assetDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Sheriff.App\Assets'))
[IO.Directory]::CreateDirectory($assetDirectory) | Out-Null
$bitmap = [Drawing.Bitmap]::new(256, 256)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$gold = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(223, 190, 117))
$dark = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(33, 32, 34))
try {
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([Drawing.Color]::Transparent)
    $points = [Drawing.PointF[]]::new(12)
    for ($index = 0; $index -lt 12; $index++) {
        $angle = (-90 + $index * 30) * [Math]::PI / 180
        $radius = if ($index % 2 -eq 0) { 117 } else { 64 }
        $points[$index] = [Drawing.PointF]::new([single](128 + [Math]::Cos($angle) * $radius), [single](128 + [Math]::Sin($angle) * $radius))
    }
    $graphics.FillPolygon($gold, $points)
    $graphics.FillEllipse($dark, 91, 91, 74, 74)
    $graphics.FillEllipse($gold, 112, 112, 32, 32)
    $handle = $bitmap.GetHicon()
    try {
        $icon = [Drawing.Icon]::FromHandle($handle)
        $output = [IO.File]::Create((Join-Path $assetDirectory 'Sheriff.ico'))
        try { $icon.Save($output) } finally { $output.Dispose(); $icon.Dispose() }
    } finally { $null = [SheriffIconNative]::DestroyIcon($handle) }
} finally { $graphics.Dispose(); $bitmap.Dispose(); $gold.Dispose(); $dark.Dispose() }
