$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'assets'
New-Item -ItemType Directory -Force -Path $assetPath | Out-Null
function New-RoundPath([single]$X,[single]$Y,[single]$Width,[single]$Height,[single]$Radius) {
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = 2*$Radius
    $p.AddArc($X,$Y,$d,$d,180,90)
    $p.AddArc($X+$Width-$d,$Y,$d,$d,270,90)
    $p.AddArc($X+$Width-$d,$Y+$Height-$d,$d,$d,0,90)
    $p.AddArc($X,$Y+$Height-$d,$d,$d,90,90)
    $p.CloseFigure()
    return $p
}
$images = [System.Collections.Generic.List[byte[]]]::new()
$sizes = @(16,24,32,48,64,128,256)
foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size,$size,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.ScaleTransform($size/256.0,$size/256.0)
    $outer = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#111720'))
    $inner = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#1B232F'))
    $mint = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#3ACEA4'))
    $white = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#E1E9F2'))
    $gold = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#FFBC51'))
    $paths = @((New-RoundPath 8 8 240 240 48),(New-RoundPath 16 16 224 224 42),(New-RoundPath 136 44 48 28 8),(New-RoundPath 126 76 68 56 12))
    $g.FillPath($outer,$paths[0]); $g.FillPath($inner,$paths[1])
    $route = [System.Drawing.Pen]::new($mint,14)
    $route.StartCap = $route.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $route.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawLines($route,[System.Drawing.PointF[]]@([System.Drawing.PointF]::new(46,200),[System.Drawing.PointF]::new(102,200),[System.Drawing.PointF]::new(102,176),[System.Drawing.PointF]::new(160,176)))
    $secondary = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#40526A'),10)
    $secondary.StartCap = $secondary.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $secondary.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawLines($secondary,[System.Drawing.PointF[]]@([System.Drawing.PointF]::new(46,160),[System.Drawing.PointF]::new(46,136),[System.Drawing.PointF]::new(82,136)))
    $g.FillPath($mint,$paths[2]); $g.FillPath($white,$paths[3]); $g.FillRectangle($white,150,132,20,24)
    $vent = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#92A5BA'),5)
    $vent.StartCap = $vent.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($vent,144,96,176,96); $g.DrawLine($vent,144,111,176,111)
    $g.FillPolygon($gold,[System.Drawing.PointF[]]@([System.Drawing.PointF]::new(146,156),[System.Drawing.PointF]::new(174,156),[System.Drawing.PointF]::new(160,178)))
    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream,[System.Drawing.Imaging.ImageFormat]::Png)
    $images.Add($stream.ToArray())
    if ($size -eq 256) { $bitmap.Save((Join-Path $assetPath 'VirtualCNC.png'),[System.Drawing.Imaging.ImageFormat]::Png) }
    $stream.Dispose(); $g.Dispose(); $bitmap.Dispose()
    foreach ($resource in @($outer,$inner,$mint,$white,$gold,$route,$secondary,$vent) + $paths) { $resource.Dispose() }
}
$file = [System.IO.File]::Create((Join-Path $assetPath 'VirtualCNC.ico'))
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6+16*$sizes.Count
    for ($i=0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($bytes in $images) { $writer.Write($bytes) }
} finally { $writer.Dispose(); $file.Dispose() }
Write-Host 'Created VirtualCNC.ico (16..256 px) and preview PNG.'
