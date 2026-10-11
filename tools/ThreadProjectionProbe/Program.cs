using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using OcctSharp;

if(args.Length!=3||args[2] is not ("identity" or "rotate" or "center"))throw new ArgumentException("Usage: ThreadProjectionProbe input.brep output identity|rotate|center");
string output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
void Progress(string value)=>File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {value}{Environment.NewLine}");
using var original=ShapeExchange.ReadBrep(Path.GetFullPath(args[0]));var projection=DrawingProjection.Top;
using var transform=args[2] switch
{
    "rotate"=>GpTrsf.Create(0,0,0,1,2,3,.317),
    "center"=>GpTrsf.Create(-10,-10,-12),
    _=>GpTrsf.Create(0,0,0)
};
GpXyz Map(GpXyz p,bool point)=>new(
    transform.Value(1,1)*p.X+transform.Value(1,2)*p.Y+transform.Value(1,3)*p.Z+(point?transform.Value(1,4):0),
    transform.Value(2,1)*p.X+transform.Value(2,2)*p.Y+transform.Value(2,3)*p.Z+(point?transform.Value(2,4):0),
    transform.Value(3,1)*p.X+transform.Value(3,2)*p.Y+transform.Value(3,3)*p.Z+(point?transform.Value(3,4):0));
using var shape=original.Transformed(transform);projection=projection with{Origin=Map(projection.Origin,true),ViewDirection=Map(projection.ViewDirection,false),UpDirection=Map(projection.UpDirection,false)};
Progress("begin Exact Top projection "+args[2]);var watch=Stopwatch.StartNew();double cpu=Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds;
using var view=TechnicalDrawing.CreateView(shape,projection,new(){Algorithm=DrawingAlgorithm.Exact,IsoparameterCount=0,SamplesPerCurve=32});
Progress("Exact projection returned");double elapsed=watch.Elapsed.TotalSeconds,processor=Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds-cpu;
var layers=view.Layers.Select(l=>new{l.Category,l.Visibility,lines=TechnicalDrawing.CopyPolylines(l.Shape,32).Select(p=>new{p.Closed,points=p.Points.Select(v=>new{v.X,v.Y,v.Z})})}).ToArray();
File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{passed=true,mode=args[2],inputSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0]))),
    runtime=OcctRuntime.Info,elapsedSeconds=elapsed,cpuSeconds=processor,layers},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"Exact Top {args[2]} PASS: {elapsed:F3}s elapsed / {processor:F3}s CPU.");
