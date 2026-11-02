# Run with PowerShell 7 and the .NET 10 SDK on Windows.
# Source artwork and generated ICO/PNG files are maintained app assets.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$generator = Join-Path $root 'debug/icon-generator'
New-Item -ItemType Directory -Force $generator | Out-Null
$source = @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
public static class ControllerArtwork {
 public static Bitmap Render(int size) {
  using var large = new Bitmap(size*4,size*4);
  using(var g=Graphics.FromImage(large)) {
   g.SmoothingMode=SmoothingMode.AntiAlias; g.ScaleTransform(size*4/160f,size*4/160f);
   using var navy=new SolidBrush(Color.FromArgb(9,25,54));
   using var tile=new GraphicsPath(); tile.AddArc(2,2,44,44,180,90);tile.AddArc(114,2,44,44,270,90);tile.AddArc(114,114,44,44,0,90);tile.AddArc(2,114,44,44,90,90);tile.CloseFigure();g.FillPath(navy,tile);
   using var signal=new Pen(Color.FromArgb(239,248,255),6){StartCap=LineCap.Round,EndCap=LineCap.Round};
   using var white=new SolidBrush(Color.FromArgb(239,248,255));
   g.DrawArc(signal,47,25,66,56,218,104);g.DrawArc(signal,62,41,36,28,218,104);g.FillEllipse(white,76,54,8,8);
   using var green=new SolidBrush(Color.FromArgb(132,221,75));using var body=new GraphicsPath();
   body.AddBezier(51,68,35,68,29,78,26,94);body.AddLine(26,94,21,116);body.AddBezier(21,116,18,132,28,140,40,133);body.AddLine(40,133,58,116);body.AddBezier(58,116,63,112,68,110,74,110);body.AddLine(74,110,86,110);body.AddBezier(86,110,92,110,97,112,102,116);body.AddLine(102,116,120,133);body.AddBezier(120,133,132,140,142,132,139,116);body.AddLine(139,116,134,94);body.AddBezier(134,94,131,78,125,68,109,68);body.CloseFigure();g.FillPath(green,body);
   g.FillRectangle(navy,44,79,9,25);g.FillRectangle(navy,36,87,25,9);
   foreach(var p in new[]{new Point(111,77),new Point(101,87),new Point(121,87),new Point(111,97)})g.FillEllipse(navy,p.X,p.Y,9,9);
   g.FillEllipse(navy,63,94,14,14);g.FillEllipse(navy,83,94,14,14);
  }
  var result=new Bitmap(size,size);
  using(var g=Graphics.FromImage(result)){g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.DrawImage(large,0,0,size,size);}return result;
 }
 public static void Write(string folder) {
  using(var png=Render(512))png.Save(Path.Combine(folder,"controller.png"),ImageFormat.Png);
  var sizes=new[]{16,20,24,32,40,48,64,128,256};var images=new byte[sizes.Length][];
  for(int i=0;i<sizes.Length;i++){using var bmp=Render(sizes[i]);using var ms=new MemoryStream();bmp.Save(ms,ImageFormat.Png);images[i]=ms.ToArray();}
  using var file=File.Create(Path.Combine(folder,"controller.ico"));using var w=new BinaryWriter(file);
  w.Write((ushort)0);w.Write((ushort)1);w.Write((ushort)sizes.Length);int offset=6+16*sizes.Length;
  for(int i=0;i<sizes.Length;i++){w.Write((byte)(sizes[i]==256?0:sizes[i]));w.Write((byte)(sizes[i]==256?0:sizes[i]));w.Write((byte)0);w.Write((byte)0);w.Write((ushort)1);w.Write((ushort)32);w.Write(images[i].Length);w.Write(offset);offset+=images[i].Length;}
  foreach(var data in images)w.Write(data);
 }
}
public static class Program { public static void Main(string[] args) => ControllerArtwork.Write(args[0]); }
'@
[IO.File]::WriteAllText((Join-Path $generator 'Program.cs'), $source)
[IO.File]::WriteAllText((Join-Path $generator 'IconGenerator.csproj'), '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWindowsForms>true</UseWindowsForms><OutputType>Exe</OutputType></PropertyGroup></Project>')
dotnet run --project (Join-Path $generator 'IconGenerator.csproj') -c Release -- (Join-Path $root 'assets')
if ($LASTEXITCODE -ne 0) { throw 'Icon generation failed' }

