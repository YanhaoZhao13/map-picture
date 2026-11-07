$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing,System.Core,System.Xml -Path (Join-Path $PSScriptRoot 'ImageCore.cs')
Add-Type -ReferencedAssemblies System.Drawing,System.Core -Path (Join-Path $PSScriptRoot 'SkyLayout.cs')
$dir=Join-Path ([IO.Path]::GetTempPath()) ('map-picture-test-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($dir)
function Assert($condition,$message){if(!$condition){throw "FAIL: $message"};Write-Output "PASS: $message"}
$base=New-Object Drawing.Bitmap(80,60)
$base.SetResolution(300,240)
for($y=0;$y -lt 60;$y++){for($x=0;$x -lt 80;$x++){$base.SetPixel($x,$y,[Drawing.Color]::FromArgb(255,[int]($x*3),[int]($y*4),[int](($x+$y)%255)))}}
$baseFile=Join-Path $dir 'base.jpg';$base.Save($baseFile,[Drawing.Imaging.ImageFormat]::Jpeg);$base.Dispose()
$baseDecoded=[ImageCore]::LoadPhoto($baseFile)
$jpeg=[IO.File]::ReadAllBytes($baseFile)
for($orientation=1;$orientation -le 8;$orientation++){
  # EXIF APP1 with little-endian TIFF and one Orientation SHORT tag.
  [byte[]]$exif=69,120,105,102,0,0,73,73,42,0,8,0,0,0,1,0,18,1,3,0,1,0,0,0,$orientation,0,0,0,0,0,0,0
  $stream=New-Object IO.MemoryStream
  $stream.Write($jpeg,0,2);$stream.WriteByte(255);$stream.WriteByte(225);$stream.WriteByte(0);$stream.WriteByte(34);$stream.Write($exif,0,$exif.Length);$stream.Write($jpeg,2,$jpeg.Length-2)
  $path=Join-Path $dir "orientation-$orientation.jpg";[IO.File]::WriteAllBytes($path,$stream.ToArray());$stream.Dispose()
  $im=[ImageCore]::LoadPhoto($path);$diff=0
  for($y=0;$y -lt 60;$y++){for($x=0;$x -lt 80;$x++){
    switch($orientation){1{$xx=$x;$yy=$y}2{$xx=79-$x;$yy=$y}3{$xx=79-$x;$yy=59-$y}4{$xx=$x;$yy=59-$y}5{$xx=$y;$yy=$x}6{$xx=59-$y;$yy=$x}7{$xx=59-$y;$yy=79-$x}8{$xx=$y;$yy=79-$x}}
    if($baseDecoded.Bitmap.GetPixel($x,$y).ToArgb() -ne $im.Bitmap.GetPixel($xx,$yy).ToArgb()){$diff++}
  }}
  Assert ($diff -eq 0 -and $im.Orientation -eq $orientation) "EXIF $orientation all decoded pixels at correct upright coordinates";$im.Dispose()
}
$transparent=[ImageCore]::NewBitmap(80,60);$composite=[ImageCore]::Composite($baseDecoded.Bitmap,$transparent);$saved=Join-Path $dir 'unchanged.png';[ImageCore]::SavePng($composite,$saved,$null)
$audit=[ImageCore]::Verify($baseDecoded.Bitmap,$transparent,$saved,$null)
Assert ($audit.OutsideAlphaPixels -eq 4800 -and $audit.CompositeDifferences -eq 0) 'Transparent overlay preserves all pixels without DPI scaling'
$transparent.SetPixel(20,20,[Drawing.Color]::FromArgb(128,255,0,0));$half=[ImageCore]::Composite($baseDecoded.Bitmap,$transparent);$s=$baseDecoded.Bitmap.GetPixel(20,20);$out=$half.GetPixel(20,20)
Assert ($out.R -eq [int][math]::Floor((255*128+$s.R*127+127)/255) -and $out.G -eq [int][math]::Floor(($s.G*127+127)/255)) 'Alpha composite agrees with independent one-pixel integer calculation'
$half.Dispose();$composite.Dispose();$transparent.Dispose();$baseDecoded.Dispose()
$photo=New-Object Drawing.Bitmap(500,400);$g=[Drawing.Graphics]::FromImage($photo);$g.Clear([Drawing.Color]::FromArgb(70,145,220));$g.Dispose();$photo.Save((Join-Path $dir 'sky.png'));$photo.Dispose()
$raw=New-Object Drawing.Bitmap(400,200);$g=[Drawing.Graphics]::FromImage($raw);for($x=10;$x -lt 400;$x+=25){$g.DrawLine([Drawing.Pens]::White,$x,0,$x,200)};$g.Dispose();$raw.Save((Join-Path $dir 'raw.png'));$raw.Dispose()
$mask=New-Object Drawing.Bitmap(500,400);$g=[Drawing.Graphics]::FromImage($mask);$g.Clear([Drawing.Color]::White);$g.FillRectangle([Drawing.Brushes]::Black,251,0,249,400);$g.Dispose();$mask.Save((Join-Path $dir 'allowed.png'))
$opts=New-Object MapLayoutOptions;$opts.AllowedMaskPath=Join-Path $dir 'allowed.png';$a=New-Object SkyLayout((Join-Path $dir 'sky.png'),(Join-Path $dir 'raw.png'),$opts)
$layer=New-Object Drawing.Bitmap(500,400);$g=[Drawing.Graphics]::FromImage($layer);$g.Clear([Drawing.Color]::White);$g.Dispose();$a.ProtectLayer($layer)
$leaks=0;for($y=0;$y -lt 400;$y++){for($x=251;$x -lt 500;$x++){if($layer.GetPixel($x,$y).A -gt 0){$leaks++}}}
$layer.Dispose();$mask.Dispose();Assert ($leaks -eq 0) "Full-resolution black mask has zero leaked overlay pixels (actual $leaks)"
$xml=@'
<osm version="0.6">
<node id="1" lon="0.1" lat="0.5"/><node id="2" lon="0.35" lat="0.5"/>
<node id="3" lon="0.65" lat="0.5"/><node id="4" lon="0.9" lat="0.5"/>
<way id="10"><nd ref="1"/><nd ref="2"/><nd ref="999"/><nd ref="3"/><nd ref="4"/><tag k="highway" v="primary"/></way>
</osm>
'@
$osmPath=Join-Path $dir 'missing-node.osm';[IO.File]::WriteAllText($osmPath,$xml)
$bounds=New-Object MapBounds;$bounds.West=0;$bounds.East=1;$bounds.South=0;$bounds.North=1
$bitmap=[ImageCore]::NewBitmap(200,200);$stat=[ImageCore]::DrawOsm($bitmap,$osmPath,$bounds,0,0,200,[Drawing.Color]::White,1)
Assert ($stat.MissingNodeReferences -eq 1 -and $bitmap.GetPixel(100,100).A -eq 0 -and $bitmap.GetPixel(40,100).A -gt 0) 'Missing OSM node breaks the road instead of inventing a connecting line'
$bitmap.Dispose()
$xml=@'
<osm version="0.6">
<node id="1" lon="0.1" lat="0.1"/><node id="2" lon="0.9" lat="0.1"/><node id="3" lon="0.9" lat="0.9"/><node id="4" lon="0.1" lat="0.9"/>
<node id="5" lon="0.4" lat="0.4"/><node id="6" lon="0.6" lat="0.4"/><node id="7" lon="0.6" lat="0.6"/><node id="8" lon="0.4" lat="0.6"/>
<way id="10"><nd ref="1"/><nd ref="2"/><nd ref="3"/><nd ref="4"/><nd ref="1"/><tag k="natural" v="water"/></way>
<way id="11"><nd ref="5"/><nd ref="6"/><nd ref="7"/><nd ref="8"/><nd ref="5"/></way>
<relation id="20"><member type="way" ref="10" role="outer"/><member type="way" ref="11" role="inner"/><tag k="type" v="multipolygon"/><tag k="natural" v="water"/></relation>
</osm>
'@
$osmPath=Join-Path $dir 'lake-with-island.osm';[IO.File]::WriteAllText($osmPath,$xml)
$bitmap=[ImageCore]::NewBitmap(200,200);$stat=[ImageCore]::DrawOsm($bitmap,$osmPath,$bounds,0,0,200,[Drawing.Color]::White,1)
Assert ($bitmap.GetPixel(100,100).A -eq 0 -and $bitmap.GetPixel(20,100).A -gt 0) 'Multipolygon coast outlines preserve the island hole instead of filling it'
$bitmap.Dispose()
Write-Output "Temporary test fixtures: $dir"
