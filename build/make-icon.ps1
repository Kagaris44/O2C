<#
.SYNOPSIS
  Рисует иконку OpenToConnect (синий круг со значком питания) и сохраняет src/O2C.App/O2C.ico.
  Иконка хранится в репозитории; скрипт нужен, только если её захочется перерисовать.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $blue = [System.Drawing.Color]::FromArgb(255, 0x18, 0x5F, 0xA5)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $blue), 0, 0, $s - 1, $s - 1)

    $w = [Math]::Max(1.5, $s * 0.09)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $w
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $r = $s * 0.27; $c = $s / 2
    # Дуга с разрывом сверху и вертикальная черта — знак питания.
    $g.DrawArc($pen, $c - $r, $c - $r + $s * 0.03, 2 * $r, 2 * $r, -60, 300)
    $g.DrawLine($pen, $c, $s * 0.18, $c, $s * 0.48)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

# Формат ICO с PNG внутри (Windows Vista+).
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush()

$target = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\O2C.App\O2C.ico'
[IO.File]::WriteAllBytes($target, $out.ToArray())
Write-Host "Иконка: $target ($($out.Length) байт)"
