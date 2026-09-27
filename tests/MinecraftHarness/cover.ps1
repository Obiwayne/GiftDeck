# Draws Packs\minecraft\cover.png (640x360): a pixel grass block, a gift box and the title.
# Windows PowerShell 5.1, from the repo folder: powershell -File tests\MinecraftHarness\cover.ps1
param([string]$Out = "Packs\minecraft\cover.png")
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$W = 640; $H = 360
$dv = New-Object System.Windows.Media.DrawingVisual
$dc = $dv.RenderOpen()
function Brush($hex) { $b = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($hex)); $b.Freeze(); $b }
function Pt($x, $y) { New-Object System.Windows.Point($x, $y) }
function Rect($x, $y, $w, $h) { New-Object System.Windows.Rect($x, $y, $w, $h) }

# Sky and pixel clouds
$sky = New-Object System.Windows.Media.LinearGradientBrush ([System.Windows.Media.ColorConverter]::ConvertFromString("#4F97EA")), ([System.Windows.Media.ColorConverter]::ConvertFromString("#BFE3FF")), 90
$dc.DrawRectangle($sky, $null, (Rect 0 0 $W $H))
$white = Brush "#F4FAFF"
foreach ($c in @(@(40, 40, 7, 2), @(72, 24, 3, 1), @(400, 36, 8, 2), @(432, 20, 4, 1), @(250, 70, 5, 1))) {
  $dc.DrawRectangle($white, $null, (Rect $c[0] $c[1] ($c[2] * 16) ($c[3] * 16)))
}

# Ground: grass on dirt, 16 px pixels
$rnd = New-Object System.Random 7
for ($x = 0; $x -lt $W; $x += 16) {
  for ($y = 296; $y -lt $H; $y += 16) {
    if ($y -lt 312) { $cols = "#5DA130", "#6DB33F", "#4E8C27" } else { $cols = "#7A5230", "#8B5E3C", "#6B4526", "#96693F" }
    $dc.DrawRectangle((Brush $cols[$rnd.Next($cols.Length)]), $null, (Rect $x $y 16 16))
  }
}

# Isometric grass block, each face an 8x8 pixel texture
function Face($p0, $u, $v, $pick) {
  for ($i = 0; $i -lt 8; $i++) { for ($j = 0; $j -lt 8; $j++) {
    $a = Pt ($p0.X + $u.X * $i / 8 + $v.X * $j / 8) ($p0.Y + $u.Y * $i / 8 + $v.Y * $j / 8)
    $geo = New-Object System.Windows.Media.StreamGeometry
    $ctx = $geo.Open()
    $ctx.BeginFigure($a, $true, $true)
    $ctx.LineTo((Pt ($a.X + $u.X / 8) ($a.Y + $u.Y / 8)), $true, $false)
    $ctx.LineTo((Pt ($a.X + $u.X / 8 + $v.X / 8) ($a.Y + $u.Y / 8 + $v.Y / 8)), $true, $false)
    $ctx.LineTo((Pt ($a.X + $v.X / 8) ($a.Y + $v.Y / 8)), $true, $false)
    $ctx.Close()
    $col = & $pick $j
    $dc.DrawGeometry((Brush $col), (New-Object System.Windows.Media.Pen((Brush $col), 0.8)), $geo)
  } }
}
$cx = 490; $cy = 150; $s = 92
Face (Pt $cx ($cy - $s / 2)) (Pt $s ($s / 2)) (Pt (-$s) ($s / 2)) { param($j) @("#6DB33F", "#5DA130", "#7CC24A", "#62A835")[$rnd.Next(4)] }
Face (Pt ($cx - $s) $cy) (Pt $s ($s / 2)) (Pt 0 ($s * 1.1)) { param($j) if ($j -lt 2 -or ($j -eq 2 -and $rnd.Next(2) -eq 0)) { @("#4E8C27", "#5A9A2E")[$rnd.Next(2)] } else { @("#6B4526", "#7A5230", "#5E3D22", "#835A36")[$rnd.Next(4)] } }
Face (Pt $cx ($cy + $s / 2)) (Pt $s (-$s / 2)) (Pt 0 ($s * 1.1)) { param($j) if ($j -lt 2 -or ($j -eq 2 -and $rnd.Next(2) -eq 0)) { @("#3F7520", "#4A8426")[$rnd.Next(2)] } else { @("#573820", "#634128", "#4D311C", "#6B4830")[$rnd.Next(4)] } }

# A gift box on the grass (GiftDeck)
$gx = 352; $gy = 232
$dc.DrawRectangle((Brush "#7C5CFF"), $null, (Rect $gx $gy 64 64))
$dc.DrawRectangle((Brush "#6246E0"), $null, (Rect ($gx - 6) ($gy - 14) 76 18))
$dc.DrawRectangle((Brush "#FFD34D"), $null, (Rect ($gx + 26) ($gy - 14) 12 78))
$dc.DrawRectangle((Brush "#FFD34D"), $null, (Rect ($gx + 12) ($gy - 26) 14 12))
$dc.DrawRectangle((Brush "#FFD34D"), $null, (Rect ($gx + 38) ($gy - 26) 14 12))

# Title
$ci = [System.Globalization.CultureInfo]::InvariantCulture
$ltr = [System.Windows.FlowDirection]::LeftToRight
$bold = New-Object System.Windows.Media.Typeface((New-Object System.Windows.Media.FontFamily("Segoe UI Black")), [System.Windows.FontStyles]::Normal, [System.Windows.FontWeights]::Black, [System.Windows.FontStretches]::Normal)
$semi = New-Object System.Windows.Media.Typeface((New-Object System.Windows.Media.FontFamily("Segoe UI")), [System.Windows.FontStyles]::Normal, [System.Windows.FontWeights]::SemiBold, [System.Windows.FontStretches]::Normal)
$dc.DrawText((New-Object System.Windows.Media.FormattedText("MINECRAFT", $ci, $ltr, $bold, 56, (Brush "#1E3A5F"), 1.0)), (Pt 34 122))
$dc.DrawText((New-Object System.Windows.Media.FormattedText("MINECRAFT", $ci, $ltr, $bold, 56, (Brush "#FFFFFF"), 1.0)), (Pt 30 118))
$dc.DrawText((New-Object System.Windows.Media.FormattedText("Java Edition  -  your own server", $ci, $ltr, $semi, 21, (Brush "#1E3A5F"), 1.0)), (Pt 34 194))
$dc.Close()

$bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($W, $H, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
$bmp.Render($dv)
$enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
$enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
$fs = [System.IO.File]::Create((Join-Path (Get-Location) $Out)); $enc.Save($fs); $fs.Close()
"wrote $Out"
