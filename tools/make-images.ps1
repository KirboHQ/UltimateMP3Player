# Renders src\UltimateMP3Player\Logo.xaml into app.ico, assets\logo.png and the installer wizard images.
# Run: powershell -Sta -File .\tools\make-images.ps1
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$root = Split-Path $PSScriptRoot -Parent
$imgDir = Join-Path $root 'installer\images'
$assets = Join-Path $root 'assets'
New-Item -ItemType Directory -Force $imgDir, $assets | Out-Null

$dict = [Windows.Markup.XamlReader]::Parse([IO.File]::ReadAllText((Join-Path $root 'src\UltimateMP3Player\Logo.xaml')))
$logo = $dict['LogoDrawing']

function Render([int]$w, [int]$h, [scriptblock]$draw) {
    $dv = New-Object Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    & $draw $dc
    $dc.Close()
    $bmp = New-Object Windows.Media.Imaging.RenderTargetBitmap $w, $h, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    return $bmp
}

function Png($bmp) {
    $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object IO.MemoryStream
    $enc.Save($ms)
    return , $ms.ToArray()
}

function Logo([int]$s, [double]$margin = 0) {
    Render $s $s {
        param($dc)
        $m = $s * $margin
        $dc.PushTransform((New-Object Windows.Media.TranslateTransform $m, $m))
        $dc.PushTransform((New-Object Windows.Media.ScaleTransform (($s - 2 * $m) / 256), (($s - 2 * $m) / 256)))
        $dc.DrawDrawing($logo)
        $dc.Pop(); $dc.Pop()
    }
}

# ---- app.ico (PNG entries)
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) { , (Png (Logo $s 0.02)) }
$ico = New-Object IO.MemoryStream
$bw = New-Object IO.BinaryWriter $ico
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $b = [byte]$(if ($s -ge 256) { 0 } else { $s })
    $bw.Write($b); $bw.Write($b); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset); $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush()
[IO.File]::WriteAllBytes((Join-Path $root 'src\UltimateMP3Player\app.ico'), $ico.ToArray())
[IO.File]::WriteAllBytes((Join-Path $assets 'logo.png'), (Png (Logo 512)))

# ---- installer wizard images (one per DPI scale)
foreach ($scale in 100, 125, 150, 200, 250) {
    $s = [int][Math]::Round(55 * $scale / 100)
    [IO.File]::WriteAllBytes((Join-Path $imgDir "small-$scale.png"), (Png (Logo $s 0.04)))

    $w = [int][Math]::Round(164 * $scale / 100); $h = [int][Math]::Round(314 * $scale / 100)
    $bmp = Render $w $h {
        param($dc)
        $bg = New-Object Windows.Media.LinearGradientBrush ([Windows.Media.Color]::FromRgb(0x14, 0x10, 0x2B)), ([Windows.Media.Color]::FromRgb(0x0E, 0x10, 0x14)), 90
        $dc.DrawRectangle($bg, $null, (New-Object Windows.Rect 0, 0, $w, $h))
        $glow = New-Object Windows.Media.RadialGradientBrush ([Windows.Media.Color]::FromArgb(90, 0x7C, 0x5C, 0xFF)), ([Windows.Media.Color]::FromArgb(0, 0x7C, 0x5C, 0xFF))
        $dc.DrawEllipse($glow, $null, (New-Object Windows.Point ($w / 2), ($h * 0.40)), ($w * 0.75), ($w * 0.75))
        $ls = $w * 0.52
        $dc.PushTransform((New-Object Windows.Media.TranslateTransform (($w - $ls) / 2), ($h * 0.40 - $ls / 2)))
        $dc.PushTransform((New-Object Windows.Media.ScaleTransform ($ls / 256), ($ls / 256)))
        $dc.DrawDrawing($logo)
        $dc.Pop(); $dc.Pop()
        $face = New-Object Windows.Media.Typeface 'Segoe UI Semibold'
        $ft = New-Object Windows.Media.FormattedText "Ultimate`nMP3 Player", ([Globalization.CultureInfo]::InvariantCulture), 'LeftToRight', $face, ($w * 0.09), ([Windows.Media.Brushes]::White), 1.0
        $ft.TextAlignment = 'Center'
        $dc.DrawText($ft, (New-Object Windows.Point ($w / 2), ($h * 0.40 + $ls / 2 + $h * 0.06)))
    }
    [IO.File]::WriteAllBytes((Join-Path $imgDir "large-$scale.png"), (Png $bmp))
}
Get-Item (Join-Path $root 'src\UltimateMP3Player\app.ico'), (Join-Path $assets 'logo.png') | Select-Object Name, Length
