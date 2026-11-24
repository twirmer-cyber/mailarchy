<#
  Erzeugt src\MailVault.App\MailVault.ico (Programmsymbol für exe, Startmenü, Taskleiste) aus derselben Form wie
  site\favicon.svg: dunkelblaue Kachel, heller Briefumschlag, goldenes Schloss. Mehrere Größen, damit Windows nichts hochskaliert.
  Aufruf: pwsh -File build\make-icon.ps1
#>
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "src\MailVault.App\MailVault.ico"
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

$images = foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = "AntiAlias"; $g.PixelOffsetMode = "HighQuality"
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($size / 32.0, $size / 32.0)
    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml("#1D3A5C"))
    $g.FillPath($bg, (RoundRect 0 0 32 32 6))
    $light = [System.Drawing.ColorTranslator]::FromHtml("#F4F6F9")
    $gold = [System.Drawing.ColorTranslator]::FromHtml("#E0A526")
    # Umschlag
    $g.FillPath((New-Object System.Drawing.SolidBrush $light), (RoundRect 4 8 24 16.5 2))
    $flap = New-Object System.Drawing.Pen $bg.Color, 1.8; $flap.StartCap = "Round"; $flap.EndCap = "Round"; $flap.LineJoin = "Round"
    $g.DrawLines($flap, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF 5, 9.5), (New-Object System.Drawing.PointF 16, 17.5), (New-Object System.Drawing.PointF 27, 9.5)))
    # Schloss unten rechts: Bügel, dunkler Rand, Körper, Schlüsselloch
    $shackle = New-Object System.Drawing.Pen $gold, 1.9; $shackle.StartCap = "Round"; $shackle.EndCap = "Round"
    $g.DrawArc($shackle, 20.59, 15.01, 5.32, 5.852, 180, 180)
    $g.DrawLine($shackle, 20.59, 17.94, 20.59, 19.5); $g.DrawLine($shackle, 25.91, 17.94, 25.91, 19.5)
    $body = RoundRect 18.5 19 9.5 7.41 1.33
    $g.DrawPath((New-Object System.Drawing.Pen $bg.Color, 3.04), $body)
    $g.FillPath((New-Object System.Drawing.SolidBrush $gold), $body)
    $g.FillEllipse($bg, 22.395, 21.475, 1.71, 1.71)
    $g.FillRectangle($bg, 22.87, 22.33, 0.76, 2.07)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    [pscustomobject]@{ Size = $size; Bytes = $ms.ToArray() }
}

# ICO-Datei: Kopf, Verzeichnis, dann die PNG-Bilder
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($i in $images) {
    $dim = if ($i.Size -ge 256) { 0 } else { $i.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$i.Bytes.Length); $w.Write([uint32]$offset)
    $offset += $i.Bytes.Length
}
foreach ($i in $images) { $w.Write($i.Bytes) }
$w.Dispose()
Write-Host "Geschrieben: $out"
