<#
  Erzeugt src\MailVault.App\MailVault.ico (Programmsymbol für exe, Startmenü, Taskleiste) aus derselben Form wie
  site\favicon.svg: dunkelblaue Kachel mit hellem Archivkasten. Mehrere Größen, damit Windows nichts hochskaliert.
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
    # kleine Größen: etwas kräftigere Linie, damit sie nicht verschwimmt
    $width = if ($size -le 20) { 2.6 } elseif ($size -le 32) { 2.2 } else { 2.0 }
    $pen = New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml("#F4F6F9")), $width
    $pen.StartCap = "Round"; $pen.EndCap = "Round"; $pen.LineJoin = "Round"
    $g.DrawPath($pen, (RoundRect 6 7 20 6 1))
    $box = New-Object System.Drawing.Drawing2D.GraphicsPath
    $box.AddLine(7.5, 13, 7.5, 23.5); $box.AddBezier(7.5, 23.5, 7.5, 24.33, 8.17, 25, 9, 25)
    $box.AddLine(9, 25, 23, 25); $box.AddBezier(23, 25, 23.83, 25, 24.5, 24.33, 24.5, 23.5); $box.AddLine(24.5, 23.5, 24.5, 13)
    $g.DrawPath($pen, $box)
    $g.DrawLine($pen, 13, 18, 19, 18)
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
