using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;

public sealed class MapPhoto : IDisposable {
 public Bitmap Bitmap; public int OriginalWidth,OriginalHeight,Orientation; public float OriginalDpiX,OriginalDpiY; public byte[] IccProfile;
 public void Dispose(){if(Bitmap!=null)Bitmap.Dispose();}
}
public sealed class MapBounds {
 public double West,South,East,North;
 public void Validate(){if(Double.IsNaN(West)||Double.IsNaN(East)||Double.IsNaN(South)||Double.IsNaN(North)||Double.IsInfinity(West)||Double.IsInfinity(East)||Double.IsInfinity(South)||Double.IsInfinity(North)||West < -180||East>180||South<=-85||North>=85||East<=West||North<=South)throw new ArgumentException("Invalid WGS84 bounds: west < east, south < north, longitude [-180,180], latitude (-85,85) required.");if(East-West>10||North-South>10)throw new ArgumentException("This renderer uses a local projection; each bounds span must be at most 10 degrees. Split regional/global maps or use a GIS projection.");}
 public void CheckPin(double lon,double lat){if(Double.IsNaN(lon)||Double.IsNaN(lat)||Double.IsInfinity(lon)||Double.IsInfinity(lat)||lon<=West||lon>=East||lat<=South||lat>=North)throw new ArgumentException("Pin must be strictly inside the WGS84 bounds; do not mix GCJ-02/BD-09 coordinates with OSM.");}
 public double Aspect {get{return (East-West)*Math.Cos((North+South)*Math.PI/360)/(North-South);}}
 public PointF Project(double lon,double lat,float left,float top,float width){return new PointF(left+(float)((lon-West)/(East-West)*width),top+(float)((North-lat)/(North-South)*width/Aspect));}
}
public sealed class MapStatistics {
 public int Nodes,Ways,Relations,RenderedWays,Segments,MissingNodeReferences,InterruptedWays;
 public Dictionary<string,int> Features=new Dictionary<string,int>();
}
public sealed class MapCheck {
 public long Pixels,OutsideAlphaPixels,OutsideAlphaDifferences,CompositeDifferences,ForbiddenMaskPixels; public int Width,Height; public string Method="Full-frame byte comparison against decoded upright source and recomputed source-over; zero-alpha pixels checked separately.";
}
public sealed class MapFontReport { public string Requested,Actual,Role; public bool Fallback; }
public static class ImageCore {
 public static MapPhoto LoadPhoto(string path){
  using(Image im=Image.FromFile(path,false)){
   if(im.PixelFormat==PixelFormat.Format48bppRgb||im.PixelFormat==PixelFormat.Format64bppArgb||im.PixelFormat==PixelFormat.Format64bppPArgb||im.PixelFormat==PixelFormat.Format16bppGrayScale)throw new NotSupportedException("Only 8-bit channel input is supported. Use a color-managed high-bit-depth compositor.");var r=new MapPhoto();r.OriginalWidth=im.Width;r.OriginalHeight=im.Height;r.OriginalDpiX=im.HorizontalResolution;r.OriginalDpiY=im.VerticalResolution;r.Orientation=1;
   try{var v=im.GetPropertyItem(0x112).Value;if(v.Length>=2)r.Orientation=v[0]+256*v[1];}catch(ArgumentException){}
   if(r.Orientation<1||r.Orientation>8)r.Orientation=1;
   try{r.IccProfile=(byte[])im.GetPropertyItem(0x8773).Value.Clone();}catch(ArgumentException){}
   int[] ops={0,0,4,2,6,5,1,7,3};im.RotateFlip((RotateFlipType)ops[r.Orientation]);
   r.Bitmap=NewBitmap(im.Width,im.Height);float dx=r.OriginalDpiX,dy=r.OriginalDpiY;if(r.Orientation>=5){float t=dx;dx=dy;dy=t;}r.Bitmap.SetResolution(SafeDpi(dx),SafeDpi(dy));
   using(Graphics g=Graphics.FromImage(r.Bitmap)){g.CompositingMode=CompositingMode.SourceCopy;g.DrawImage(im,new Rectangle(0,0,im.Width,im.Height),0,0,im.Width,im.Height,GraphicsUnit.Pixel);}return r;
  }
 }
 static float SafeDpi(float x){return !Single.IsNaN(x)&&!Single.IsInfinity(x)&&x>=1&&x<=9600?x:72;}
 public static Bitmap NewBitmap(int w,int h){if(w<1||h<1||(long)w*h>150000000)throw new ArgumentException("Image dimensions exceed the supported 150 megapixel limit.");return new Bitmap(w,h,PixelFormat.Format32bppArgb);}
 public static Graphics GraphicsFor(Bitmap b){var g=Graphics.FromImage(b);g.SmoothingMode=SmoothingMode.AntiAlias;g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;g.PageUnit=GraphicsUnit.Pixel;return g;}
 public static Font Font(string name,float px,string role,List<MapFontReport> reports,FontStyle style){if(px<3||px>2000||Single.IsNaN(px)||Single.IsInfinity(px))throw new ArgumentException("Invalid font size for "+role);Font f;try{f=new Font(name,px,style,GraphicsUnit.Pixel);}catch(ArgumentException){f=new Font(System.Drawing.FontFamily.GenericSansSerif,px,style,GraphicsUnit.Pixel);}reports.Add(new MapFontReport{Requested=name,Actual=f.FontFamily.Name,Role=role,Fallback=!String.Equals(name,f.FontFamily.Name,StringComparison.OrdinalIgnoreCase)});return f;}
 static byte[] Pixels(Bitmap b){var d=b.LockBits(new Rectangle(0,0,b.Width,b.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);try{if(d.Stride<0)throw new NotSupportedException("Negative image stride is not supported.");byte[] r=new byte[b.Width*b.Height*4];for(int y=0;y<b.Height;y++)Marshal.Copy(IntPtr.Add(d.Scan0,y*d.Stride),r,y*b.Width*4,b.Width*4);return r;}finally{b.UnlockBits(d);}}
 static void Put(Bitmap b,byte[] bytes){var d=b.LockBits(new Rectangle(0,0,b.Width,b.Height),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);try{for(int y=0;y<b.Height;y++)Marshal.Copy(bytes,y*b.Width*4,IntPtr.Add(d.Scan0,y*d.Stride),b.Width*4);}finally{b.UnlockBits(d);}}
 static void CompositeAt(byte[] src,byte[] layer,int i,byte[] output){int a=layer[i+3],b=src[i+3];if(a==0){for(int k=0;k<4;k++)output[i+k]=src[i+k];return;}int denominator=a*255+b*(255-a);output[i+3]=(byte)((denominator+127)/255);for(int k=0;k<3;k++)output[i+k]=denominator==0?(byte)0:(byte)((layer[i+k]*a*255+src[i+k]*b*(255-a)+denominator/2)/denominator);}
 public static Bitmap Composite(Bitmap src,Bitmap layer){if(src.Size!=layer.Size)throw new ArgumentException("Source and layer dimensions differ.");byte[] s=Pixels(src),l=Pixels(layer),o=new byte[s.Length];for(int i=0;i<s.Length;i+=4)CompositeAt(s,l,i,o);var b=NewBitmap(src.Width,src.Height);b.SetResolution(SafeDpi(src.HorizontalResolution),SafeDpi(src.VerticalResolution));Put(b,o);return b;}
 public static MapCheck Verify(Bitmap src,Bitmap layer,string savedPath,string allowedMask){using(var p=LoadPhoto(savedPath)){
  if(src.Size!=layer.Size||src.Size!=p.Bitmap.Size)throw new InvalidOperationException("Verification failed: image dimensions changed.");var r=new MapCheck{Pixels=(long)src.Width*src.Height,Width=src.Width,Height=src.Height};byte[] s=Pixels(src),l=Pixels(layer),o=Pixels(p.Bitmap),e=new byte[s.Length];byte[] mask=null;if(!String.IsNullOrEmpty(allowedMask)){using(var m=LoadPhoto(allowedMask)){if(m.Bitmap.Size!=src.Size)throw new ArgumentException("Allowed mask must match the upright source dimensions.");mask=Pixels(m.Bitmap);}}
  for(int i=0;i<s.Length;i+=4){CompositeAt(s,l,i,e);bool equal=true;for(int k=0;k<4;k++)if(e[i+k]!=o[i+k])equal=false;if(!equal)r.CompositeDifferences++;if(l[i+3]==0){r.OutsideAlphaPixels++;for(int k=0;k<4;k++)if(s[i+k]!=o[i+k]){r.OutsideAlphaDifferences++;break;}}if(mask!=null&&l[i+3]>0&&(mask[i]+mask[i+1]+mask[i+2])*mask[i+3]<3*255*16)r.ForbiddenMaskPixels++;}return r;
 }}
 public static double AlphaRetention(Bitmap before,Bitmap after,Rectangle area){if(before.Size!=after.Size)throw new ArgumentException("Layer size mismatch.");area.Intersect(new Rectangle(0,0,before.Width,before.Height));if(area.IsEmpty)return 0;byte[] a=Pixels(before),b=Pixels(after);long aa=0,bb=0;for(int y=area.Top;y<area.Bottom;y++)for(int x=area.Left;x<area.Right;x++){int i=(y*before.Width+x)*4+3;aa+=a[i];bb+=b[i];}return aa==0?0:bb/(double)aa;}
 public static Bitmap Preview(Bitmap src,int width){if(width<64||width>4096)throw new ArgumentException("preview_width must be 64..4096.");width=Math.Min(width,src.Width);int h=Math.Max(1,(int)Math.Round(src.Height*(double)width/src.Width));var p=NewBitmap(width,h);using(var g=GraphicsFor(p)){g.CompositingMode=CompositingMode.SourceCopy;g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.DrawImage(src,new Rectangle(0,0,width,h),0,0,src.Width,src.Height,GraphicsUnit.Pixel);}return p;}
 public static MapStatistics DrawOsm(Bitmap target,string path,MapBounds bounds,float left,float top,float width,Color ink,float scale){
  var xml=new XmlDocument();xml.XmlResolver=null;var settings=new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null};using(var reader=XmlReader.Create(path,settings))xml.Load(reader);
  var nodes=new Dictionary<string,double[]>();var water=new HashSet<string>();var stat=new MapStatistics();
  foreach(XmlElement n in xml.SelectNodes("/osm/node")){nodes[n.GetAttribute("id")]=new[]{Double.Parse(n.GetAttribute("lon"),CultureInfo.InvariantCulture),Double.Parse(n.GetAttribute("lat"),CultureInfo.InvariantCulture)};stat.Nodes++;}
  foreach(XmlElement rel in xml.SelectNodes("/osm/relation")){stat.Relations++;var tags=Tags(rel);if(Get(tags,"natural")=="water"||tags.ContainsKey("waterway"))foreach(XmlElement member in rel.SelectNodes("member"))if(member.GetAttribute("type")=="way")water.Add(member.GetAttribute("ref"));}
  using(var g=GraphicsFor(target)){g.SetClip(new RectangleF(left,top,width,(float)(width/bounds.Aspect)));foreach(XmlElement way in xml.SelectNodes("/osm/way")){stat.Ways++;var t=Tags(way);string kind="";int alpha=0;float pw=1;
   if(t.ContainsKey("railway")){kind="rail";alpha=135;pw=.8f;}if(t.ContainsKey("landuse")||t.ContainsKey("leisure")){kind="land";alpha=65;pw=.5f;}if(t.ContainsKey("building")){kind="building";alpha=85;pw=.45f;}if(t.ContainsKey("highway")){kind="road";alpha=190;pw=.85f;string h=Get(t,"highway");if(h=="motorway"||h=="trunk"||h=="primary"||h=="secondary"||h=="tertiary"){alpha=235;pw=1.45f;}if(h=="footway"||h=="path"||h=="steps"){alpha=130;pw=.55f;}}
   if(Get(t,"natural")=="water"||Get(t,"natural")=="coastline"||t.ContainsKey("waterway")||water.Contains(way.GetAttribute("id"))){kind="water";alpha=235;pw=1.6f;}if(kind=="")continue;
   var segments=new List<List<PointF>>();var current=new List<PointF>();bool missing=false;string first=null,last=null;foreach(XmlElement nd in way.SelectNodes("nd")){string id=nd.GetAttribute("ref");if(first==null)first=id;last=id;double[] xy;if(!nodes.TryGetValue(id,out xy)){stat.MissingNodeReferences++;missing=true;if(current.Count>1)segments.Add(current);current=new List<PointF>();continue;}current.Add(bounds.Project(xy[0],xy[1],left,top,width));}if(current.Count>1)segments.Add(current);if(missing)stat.InterruptedWays++;if(segments.Count==0)continue;
   using(var pen=new Pen(Color.FromArgb(alpha,ink),Math.Max(.1f,pw*scale))){if(kind=="rail")pen.DashPattern=new float[]{5,3};foreach(var seg in segments){if(kind=="water"&&!water.Contains(way.GetAttribute("id"))&&(Get(t,"natural")=="water"||t.ContainsKey("waterway"))&&!missing&&first==last&&segments.Count==1&&seg.Count>3){using(var brush=new SolidBrush(Color.FromArgb(22,ink)))g.FillPolygon(brush,seg.ToArray());}g.DrawLines(pen,seg.ToArray());stat.Segments+=seg.Count-1;}}stat.RenderedWays++;if(!stat.Features.ContainsKey(kind))stat.Features[kind]=0;stat.Features[kind]++;
  }}if(stat.RenderedWays==0)throw new ArgumentException("OSM contains no usable roads, buildings, railways, land or water geometry.");return stat;
 }
 static Dictionary<string,string> Tags(XmlElement e){var r=new Dictionary<string,string>();foreach(XmlElement t in e.SelectNodes("tag"))r[t.GetAttribute("k")]=t.GetAttribute("v");return r;}
 static string Get(Dictionary<string,string> d,string k){string s;return d.TryGetValue(k,out s)?s:"";}
 public static void SavePng(Bitmap bitmap,string path,byte[] icc){bitmap.Save(path,ImageFormat.Png);if(icc==null||icc.Length==0)return;byte[] png=File.ReadAllBytes(path);byte[] name=Encoding.ASCII.GetBytes("Original ICC\0\0");using(var payload=new MemoryStream()){payload.Write(name,0,name.Length);payload.WriteByte(0x78);payload.WriteByte(0x01);int at=0;while(at<icc.Length){int n=Math.Min(65535,icc.Length-at);bool last=at+n==icc.Length;payload.WriteByte(last?(byte)1:(byte)0);payload.WriteByte((byte)n);payload.WriteByte((byte)(n>>8));int inv=(~n)&65535;payload.WriteByte((byte)inv);payload.WriteByte((byte)(inv>>8));payload.Write(icc,at,n);at+=n;}uint a=1,b=0;foreach(byte v in icc){a=(a+v)%65521;b=(b+a)%65521;}WriteBE(payload,(b<<16)|a);byte[] data=payload.ToArray();using(var output=new MemoryStream()){output.Write(png,0,33);WriteBE(output,(uint)data.Length);byte[] type=Encoding.ASCII.GetBytes("iCCP");output.Write(type,0,4);output.Write(data,0,data.Length);byte[] crcData=new byte[4+data.Length];Buffer.BlockCopy(type,0,crcData,0,4);Buffer.BlockCopy(data,0,crcData,4,data.Length);WriteBE(output,Crc(crcData));output.Write(png,33,png.Length-33);File.WriteAllBytes(path,output.ToArray());}}}
 static void WriteBE(Stream s,uint v){s.WriteByte((byte)(v>>24));s.WriteByte((byte)(v>>16));s.WriteByte((byte)(v>>8));s.WriteByte((byte)v);}
 static uint Crc(byte[] data){uint c=0xffffffff;foreach(byte b in data){c^=b;for(int k=0;k<8;k++)c=(c&1)!=0?0xedb88320^(c>>1):c>>1;}return c^0xffffffff;}
}


