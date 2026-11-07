[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Config,[switch]$Force)
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing,System.Core,System.Xml -Path (Join-Path $PSScriptRoot 'ImageCore.cs'),(Join-Path $PSScriptRoot 'SkyLayout.cs')
function Value($obj,[string]$name,$default){if($null -ne $obj -and $null -ne $obj.PSObject.Properties[$name]){return $obj.$name};return $default}
function Need($obj,[string]$name){$v=Value $obj $name $null;if($null -eq $v -or ($v -is [string] -and [string]::IsNullOrWhiteSpace($v))){throw "Missing config field: $name"};return $v}
function Number($obj,[string]$name,$default){$v=Value $obj $name $default;try{$d=[double]$v}catch{throw "Expected a number: $name"};if([double]::IsNaN($d) -or [double]::IsInfinity($d)){throw "Expected a finite number: $name"};return $d}
$configPath=[IO.Path]::GetFullPath($Config)
if(!(Test-Path -LiteralPath $configPath -PathType Leaf)){throw "Config file not found: $configPath"}
$base=[IO.Path]::GetDirectoryName($configPath)
function ConfigPath([string]$path){if([string]::IsNullOrWhiteSpace($path)){return $null};if([IO.Path]::IsPathRooted($path)){return [IO.Path]::GetFullPath($path)};return [IO.Path]::GetFullPath([IO.Path]::Combine($base,$path))}
$c=Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
$source=ConfigPath (Need $c 'source');$osm=ConfigPath (Need $c 'osm');$output=ConfigPath (Need $c 'output_dir');$prefix=[string](Need $c 'prefix')
if($prefix -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$' -or $prefix.EndsWith('.') -or $prefix -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)'){throw 'prefix must be a safe filename stem (1..80 ASCII letters, digits, dots, underscores or hyphens).'}
foreach($inputPath in @($source,$osm)){if(!(Test-Path -LiteralPath $inputPath -PathType Leaf)){throw "Input file not found: $inputPath"}}
if((Value $c 'coordinate_system' 'WGS84') -ne 'WGS84'){throw 'Only WGS84 geographic coordinates are supported. Convert GCJ-02/BD-09 before rendering.'}
$bounds=New-Object MapBounds;$bc=Need $c 'bounds';$bounds.West=Number $bc 'west' $null;$bounds.South=Number $bc 'south' $null;$bounds.East=Number $bc 'east' $null;$bounds.North=Number $bc 'north' $null
foreach($n in @('west','south','east','north')){[void](Need $bc $n)};$bounds.Validate()
$pin=Need $c 'pin';[void](Need $pin 'lon');[void](Need $pin 'lat');$pinLon=Number $pin 'lon' $null;$pinLat=Number $pin 'lat' $null;$bounds.CheckPin($pinLon,$pinLat)
$pinLines=@(Need $pin 'label');if($pinLines.Count -lt 1 -or $pinLines.Count -gt 4){throw 'pin.label must contain 1..4 text lines.'};foreach($line in $pinLines){if([string]::IsNullOrWhiteSpace([string]$line)){throw 'Pin label lines must not be blank.'}}
$title=[string](Need $c 'title');$subtitle=[string](Value $c 'subtitle' '');$pinNote=[string](Value $pin 'note' 'approx. area')
$layout=Value $c 'layout' $null;$mode=[string](Value $layout 'mode' 'auto');if($mode -notin @('auto','manual')){throw 'layout.mode must be auto or manual.'}
$allowed=ConfigPath (Value $layout 'allowed_mask' $null);$clear=ConfigPath (Value $layout 'clear_mask' $null)
foreach($mask in @($allowed,$clear)){if($mask -and !(Test-Path -LiteralPath $mask -PathType Leaf)){throw "Mask not found: $mask"}}
$names=@("$prefix.png","$prefix-map.png","$prefix-preview.png","$prefix-manifest.json");if($mode -eq 'auto'){$names+="$prefix-layout-analysis.png"}
$protected=@($source,$osm,$configPath,$allowed,$clear) | Where-Object {$_}
foreach($name in $names){$dest=[IO.Path]::Combine($output,$name);foreach($inputPath in $protected){if([string]::Equals($dest,$inputPath,[StringComparison]::OrdinalIgnoreCase)){throw 'Output would overwrite a source, config or mask file; change output_dir/prefix.'}};if((Test-Path -LiteralPath $dest) -and !$Force){throw "Output already exists; choose a new prefix or explicitly use -Force: $dest"}}
[void][IO.Directory]::CreateDirectory($output)
$run=[IO.Path]::Combine($output,'work',($prefix+'-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8)));[void][IO.Directory]::CreateDirectory($run)
$warnings=New-Object 'System.Collections.Generic.List[string]';$reports=New-Object 'System.Collections.Generic.List[MapFontReport]';$resources=New-Object 'System.Collections.Generic.List[System.IDisposable]'
function Keep($obj){$resources.Add($obj);return $obj}
function Rect([double]$x,[double]$y,[double]$width,[double]$height){return New-Object Drawing.Rectangle([int][math]::Floor($x),[int][math]::Floor($y),[int][math]::Ceiling($width),[int][math]::Ceiling($height))}
function Inside([Drawing.Rectangle]$r,[int]$w,[int]$h,[string]$role){if($r.X -lt 0 -or $r.Y -lt 0 -or $r.Right -gt $w -or $r.Bottom -gt $h){throw "$role would be clipped by the image boundary. Change layout or typography."}}
try{
 $sourceHashBefore=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash;$photo=Keep ([ImageCore]::LoadPhoto($source));$src=$photo.Bitmap;$w=$src.Width;$h=$src.Height
 if($w -lt 64 -or $h -lt 64){throw 'Source must be at least 64 pixels in each dimension.'}
 $bitDepth=[Drawing.Image]::GetPixelFormatSize($src.PixelFormat)
 # GDI+ returns 8-bit channels. Detect high-bit input headers before claiming fidelity.
 $header=[IO.File]::ReadAllBytes($source);$highBit=$false
 if($header.Length -ge 25 -and $header[0] -eq 137 -and $header[1] -eq 80 -and $header[24] -gt 8){$highBit=$true}
 if($highBit){throw 'This dependency-free renderer supports 8-bit channels only; the PNG input is higher bit depth. Use a color-managed 16-bit compositor instead.'}
 if([IO.Path]::GetExtension($source) -match '^\.(tif|tiff|heic|avif|jxl|exr|hdr)$'){throw 'This renderer accepts 8-bit JPEG/PNG/BMP/GIF. Use a color-managed compositor for TIFF/HDR/HEIF/AVIF/EXR.'}
 $upright=Join-Path $run 'source-upright.png';[ImageCore]::SavePng($src,$upright,$null)
 $scale=[math]::Min($w,$h)/1800.;$typo=Value $c 'typography' $null
 $serif=[string](Value $typo 'font_serif' 'Georgia');$sans=[string](Value $typo 'font_sans' 'Arial')
 $titleFont=Keep ([ImageCore]::Font($serif,[single]((Number $typo 'title_size' 46)*$scale),'title',$reports,[Drawing.FontStyle]::Regular))
 $subFont=Keep ([ImageCore]::Font($sans,[single]([math]::Max(8,(Number $typo 'subtitle_size' 16)*$scale)),'subtitle',$reports,[Drawing.FontStyle]::Regular))
 $pinFont=Keep ([ImageCore]::Font($serif,[single]((Number $typo 'pin_size' 22)*$scale),'pin',$reports,[Drawing.FontStyle]::Regular))
 $noteFont=Keep ([ImageCore]::Font($sans,[single]([math]::Max(8,(Number $typo 'note_size' 14)*$scale)),'note',$reports,[Drawing.FontStyle]::Regular))
 $labelFont=Keep ([ImageCore]::Font($serif,[single]((Number $typo 'label_size' 20)*$scale),'labels',$reports,[Drawing.FontStyle]::Italic))
 $attribFont=Keep ([ImageCore]::Font($sans,[single]([math]::Max(9,12*$scale)),'attribution',$reports,[Drawing.FontStyle]::Regular))
 $measure=Keep ([ImageCore]::GraphicsFor($src));$attribution=([string][char]0xA9)+' OpenStreetMap contributors'
 $titleWidth=0.;foreach($pair in @(@($title,$titleFont),@($subtitle,$subFont),@($attribution,$attribFont))){$titleWidth=[math]::Max($titleWidth,$measure.MeasureString([string]$pair[0],$pair[1]).Width)}
 $titleHeight=$titleFont.GetHeight($measure)+$subFont.GetHeight($measure)+$attribFont.GetHeight($measure)+10*$scale
 $labelWidth=0.;foreach($line in $pinLines){$labelWidth=[math]::Max($labelWidth,$measure.MeasureString([string]$line,$pinFont).Width)};if($pinNote){$labelWidth=[math]::Max($labelWidth,$measure.MeasureString($pinNote,$noteFont).Width)}
 $labelHeight=$pinFont.GetHeight($measure)*$pinLines.Count;if($pinNote){$labelHeight+=$noteFont.GetHeight($measure)+3*$scale}
 $options=New-Object MapLayoutOptions;$options.PinU=($pinLon-$bounds.West)/($bounds.East-$bounds.West);$options.PinV=($bounds.North-$pinLat)/($bounds.North-$bounds.South)
 $options.LabelWidth=($labelWidth+8*$scale)/$w;$options.LabelHeight=($labelHeight+6*$scale)/$h;$options.TitleWidth=($titleWidth+8*$scale)/$w;$options.TitleHeight=($titleHeight+6*$scale)/$h
 $options.MinArea=Number $layout 'min_area' .15;$options.MaxArea=Number $layout 'max_area' .25;$options.PreferredArea=($options.MinArea+$options.MaxArea)/2;$options.MaxBottom=Number $layout 'max_bottom' .80;$options.AllowedMaskPath=$allowed;$options.ClearMaskPath=$clear
 $rawWidth=2048;$rawHeight=[int][math]::Round($rawWidth/$bounds.Aspect);if($rawHeight -gt 8192){$rawWidth=[int][math]::Floor(8192*$bounds.Aspect);$rawHeight=8192};if($rawWidth -lt 32 -or $rawHeight -lt 32){throw 'Geographic bounds are too narrow for reliable layout scoring.'}
 $raw=Keep ([ImageCore]::NewBitmap($rawWidth,$rawHeight));$ink=[Drawing.ColorTranslator]::FromHtml([string](Value (Value $c 'render' $null) 'ink' '#F1E8DA'))
 $lineScale=Number (Value $c 'render' $null) 'line_scale' 1;if($lineScale -lt .25 -or $lineScale -gt 3){throw 'render.line_scale must be .25..3.'}
 $rawStat=[ImageCore]::DrawOsm($raw,$osm,$bounds,0,0,$rawWidth,$ink,($rawWidth/1800.*$lineScale))
 $rawPath=Join-Path $run 'raw-map.png';[ImageCore]::SavePng($raw,$rawPath,$null)
 $analysis=New-Object SkyLayout($upright,$rawPath,$options)
 if($mode -eq 'auto'){$analysis.Search();$analysis.SaveHeatmap($upright,(Join-Path $run "$prefix-layout-analysis.png"));$candidate=$analysis.Best;if($null -eq $candidate){throw ('No safe automatic layout. '+$analysis.LastError+' Provide an explicit allowed mask or reviewed manual layout. Analysis: '+(Join-Path $run "$prefix-layout-analysis.png"))}}
 else{foreach($n in @('left','top','width')){[void](Need $layout $n)};$candidate=$analysis.Evaluate((Number $layout 'left' 0),(Number $layout 'top' 0),(Number $layout 'width' 0));if([double]::IsNegativeInfinity($candidate.Score)){throw 'Manual candidate was rejected by heuristic map/pin/label suitability scoring; this is not semantic subject verification. Supply an allowed mask, change position or bounds.'};if($candidate.Area -lt $options.MinArea -or $candidate.Area -gt $options.MaxArea){throw 'Manual layout violates requested min_area/max_area. Explicitly revise those constraints rather than silently shrinking the map.'}}
 $warnings.Add($analysis.ConfidenceWarning);if($mode -eq 'manual'){$warnings.Add('Manual layout: explicitly inspect all subjects, cloud boundaries, point placement and text overlap; heuristic scoring is not semantic verification.')};if($candidate.TitleLabelOverlap){$warnings.Add('Title and pin label boxes overlap. Review typography/layout before delivery.')}
 $map=Keep ([ImageCore]::NewBitmap($w,$h));$stats=[ImageCore]::DrawOsm($map,$osm,$bounds,($candidate.Left*$w),($candidate.Top*$h),($candidate.Width*$w),$ink,($scale*$lineScale))
 $point=$bounds.Project($pinLon,$pinLat,($candidate.Left*$w),($candidate.Top*$h),($candidate.Width*$w));$analysis.BlendLayer($map,$candidate,($point.X/$w),($point.Y/$h));$analysis.ProtectLayer($map)
 $ann=Keep ([ImageCore]::NewBitmap($w,$h));$g=Keep ([ImageCore]::GraphicsFor($ann));$brush=Keep (New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(235,$ink)));$red=Keep (New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(255,242,58,44)))
 $boxes=New-Object 'System.Collections.Generic.List[object]';$x=$candidate.TitleX*$w;$y=$candidate.TitleY*$h;$r=Rect $x $y ($titleWidth+8*$scale) ($titleHeight+6*$scale);Inside $r $w $h 'Title';$boxes.Add(@{role='title';rect=$r})
 $g.DrawString($title,$titleFont,$brush,[single]$x,[single]$y);$y+=$titleFont.GetHeight($g)+3*$scale
 if($subtitle){$g.DrawString($subtitle,$subFont,$brush,[single]$x,[single]$y)};$y+=$subFont.GetHeight($g)+3*$scale
 $g.DrawString($attribution,$attribFont,$brush,[single]$x,[single]$y);$as=$g.MeasureString($attribution,$attribFont);$boxes.Add(@{role='attribution';rect=(Rect $x $y $as.Width $as.Height)})
 $radius=[math]::Max(3,6*$scale);$pinRect=Rect ($point.X-$radius) ($point.Y-$radius) (2*$radius+2) (2*$radius+2);Inside $pinRect $w $h 'Pin';$g.FillEllipse($red,[single]($point.X-$radius),[single]($point.Y-$radius),[single](2*$radius),[single](2*$radius));$boxes.Add(@{role='pin';rect=$pinRect})
 $x=$candidate.LabelX*$w;$y=$candidate.LabelY*$h;$r=Rect $x $y ($labelWidth+8*$scale) ($labelHeight+6*$scale);Inside $r $w $h 'Pin label';$boxes.Add(@{role='pin_label';rect=$r})
 foreach($line in $pinLines){$g.DrawString([string]$line,$pinFont,$brush,[single]$x,[single]$y);$y+=$pinFont.GetHeight($g)};if($pinNote){$g.DrawString($pinNote,$noteFont,$brush,[single]$x,[single]($y+3*$scale))}
 $aux=@(Value $c 'labels' @());foreach($label in $aux){$lon=Number $label 'lon' $null;$lat=Number $label 'lat' $null;[void](Need $label 'lon');[void](Need $label 'lat');$bounds.CheckPin($lon,$lat);$p=$bounds.Project($lon,$lat,($candidate.Left*$w),($candidate.Top*$h),($candidate.Width*$w));$text=[string](Need $label 'text');$x=$p.X+(Number $label 'dx' 0)*$scale;$y=$p.Y+(Number $label 'dy' 0)*$scale;$size=$g.MeasureString($text,$labelFont);$r=Rect $x $y ($size.Width+2*$scale) ($size.Height+2*$scale);Inside $r $w $h 'Geographic label';$boxes.Add(@{role=('label:'+ $text);rect=$r});$g.DrawString($text,$labelFont,$brush,[single]$x,[single]$y)}
 $g.Dispose();$before=Keep (New-Object Drawing.Bitmap($ann));$analysis.ProtectLayer($ann);$retention=@();$attributionVisible=$true
 foreach($box in $boxes){$fraction=[ImageCore]::AlphaRetention($before,$ann,$box.rect);$retention+=@{role=$box.role;alpha_retained=$fraction};if($box.role -eq 'attribution' -and $fraction -lt .85){$attributionVisible=$false;$warnings.Add('Attribution was partly suppressed; place readable attribution externally before public use.')};if($box.role -eq 'pin' -and $fraction -lt .85){throw 'Pin falls on protected or uncertain content and would disappear. Change layout or provide a reviewed allowed mask.'};if($fraction -lt .65){$warnings.Add(('Annotation partially suppressed; review '+$box.role+' retained='+$fraction.ToString('F3',[Globalization.CultureInfo]::InvariantCulture)))}}
 $layer=Keep ([ImageCore]::Composite($map,$ann));$full=Keep ([ImageCore]::Composite($src,$layer));$preview=Keep ([ImageCore]::Preview($full,[int](Number $c 'preview_width' 1200)))
 [ImageCore]::SavePng($layer,(Join-Path $run "$prefix-map.png"),$photo.IccProfile);[ImageCore]::SavePng($full,(Join-Path $run "$prefix.png"),$photo.IccProfile);[ImageCore]::SavePng($preview,(Join-Path $run "$prefix-preview.png"),$photo.IccProfile)
 $check=[ImageCore]::Verify($src,$layer,(Join-Path $run "$prefix.png"),$allowed)
 if($check.OutsideAlphaDifferences -ne 0 -or $check.CompositeDifferences -ne 0 -or $check.ForbiddenMaskPixels -ne 0){throw ('Full-frame verification failed: '+($check|ConvertTo-Json -Compress))}
 foreach($font in $reports){if($font.Fallback){$warnings.Add('Font fallback for '+$font.Role+': '+$font.Requested+' -> '+$font.Actual)}}
 if($stats.MissingNodeReferences -gt 0){$warnings.Add('OSM has missing node references. Segments were broken at every missing node; fetch complete way dependencies for more detail.')}
 $sourceHashAfter=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash;if($sourceHashBefore -ne $sourceHashAfter){throw 'Source content changed during rendering; outputs not published.'}
 $sources=@(Value $c 'source_urls' @());if($sources.Count -eq 0){$warnings.Add('No source_urls supplied. Add geographic provenance before publication.')}
 $iccStatus='Source has no embedded ICC profile; no profile was invented.';if($photo.IccProfile){$iccStatus='Original ICC profile bytes embedded in full, layer and preview PNG iCCP chunks; pixels decoded without implicit color conversion. The map layer uses the same numeric RGB space.'}
 $manifest=[ordered]@{schema_version='1.0';created_utc=[DateTime]::UtcNow.ToString('o');config=$configPath;source=$source;source_sha256_before=$sourceHashBefore;source_sha256_after=$sourceHashAfter;source_unchanged=$true;osm=$osm;osm_sha256=(Get-FileHash -LiteralPath $osm -Algorithm SHA256).Hash;source_urls=$sources;coordinate_system='WGS84';projection='Local equirectangular, longitude scaled by cos(mid-latitude); north up; no anisotropic stretch.';original=@{width=$photo.OriginalWidth;height=$photo.OriginalHeight;exif_orientation=$photo.Orientation;dpi_x=$photo.OriginalDpiX;dpi_y=$photo.OriginalDpiY};output=@{width=$w;height=$h;format='8-bit RGBA PNG';icc=$iccStatus};layout=@{mode=$mode;selected=$candidate;examined=$analysis.Examined;alternatives=$analysis.Alternatives;allowed_mask=$allowed;clear_mask=$clear};bounds=$bounds;pin=@{lon=$pinLon;lat=$pinLat;label=$pinLines;note=$pinNote;precise_photo_gps=$false};statistics=$stats;fonts=$reports;annotation_retention=$retention;verification=$check;attribution=@{text=$attribution;drawn_in_image=$attributionVisible;publication_note='Keep this attribution readable when cropping/resizing and accompany public use with the OSM copyright/license link https://www.openstreetmap.org/copyright'};warnings=$warnings;visual_review_required=$true;work_dir=$run;files=$names}
 $manifest|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $run "$prefix-manifest.json") -Encoding UTF8
 # Publish only fully verified artifacts. Recheck collisions in case another run finished meanwhile.
 foreach($name in $names){$dest=Join-Path $output $name;if((Test-Path -LiteralPath $dest) -and !$Force){throw "Output appeared during rendering; refusing overwrite: $dest"}}
 foreach($name in $names){Copy-Item -LiteralPath (Join-Path $run $name) -Destination (Join-Path $output $name) -Force:$Force}
 Write-Output ('Rendered: '+(Join-Path $output "$prefix.png"));Write-Output ('Manifest: '+(Join-Path $output "$prefix-manifest.json"));Write-Output ('Verification: '+$check.Pixels+' pixels; outside-alpha differences=0; full composite differences=0')
 foreach($warning in $warnings){Write-Warning $warning}
}catch{
 $failure=[ordered]@{status='failed';error=$_.Exception.Message;config=$configPath;work_dir=$run;created_utc=[DateTime]::UtcNow.ToString('o')};$failure|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $run 'failure.json') -Encoding UTF8
 throw
}finally{for($i=$resources.Count-1;$i -ge 0;$i--){try{$resources[$i].Dispose()}catch{}}}




