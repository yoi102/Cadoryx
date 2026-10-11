using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using OcctSharp;

if(args.Length!=1)throw new ArgumentException("Usage: SurfaceTerminationProbe output");
string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);var rows=new List<object>();
void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
void Progress(string value)=>File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {value}{Environment.NewLine}");
double Mass(Shape shape,InspectionPropertyKind kind)=>shape.InspectProperties(kind).Mass;
using var basis=ShapeFactory.CreateBox(10,10,5);var faces=basis.GetFaces();
try
{
    var support=faces.Single(face=>face.GetBoundingBox().Minimum.Z>4.999);
    // SketchPlane receives local X/Y axes; its normal is their cross product.
    var plane=new SketchPlane(new(5,5,12),new(1,0,0),new(0,1,0));
    var sphere=new SketchPlane(new(5,5,18),new(1,0,0),new(0,1,0));
    var cylinder=new SketchPlane(new(5,0,18),new(1,0,0),new(0,0,-1));
    foreach(string kind in new[]{"plane-full","plane-small","sphere-lower","cylinder-lower"})
    {
        Progress(kind);
        bool round=kind.StartsWith("sphere");
        using var profile=Profile(round);
        using var stop=kind switch
        {
            "sphere-lower"=>SurfaceModeling.CreateAnalyticFace(AnalyticSurfaceKind.Sphere,sphere,new(0,Math.Tau,-Math.PI/2,0),6),
            "cylinder-lower"=>SurfaceModeling.CreateAnalyticFace(AnalyticSurfaceKind.Cylinder,cylinder,new(0,Math.PI,0,10),6),
            "plane-small"=>SurfaceModeling.CreateAnalyticFace(AnalyticSurfaceKind.Plane,plane,new(-.5,.5,-.5,.5)),
            _=>SurfaceModeling.CreateAnalyticFace(AnalyticSurfaceKind.Plane,plane,new(-5,5,-5,5))
        };
        using var plan=LimitedFeaturePlan.Create(basis,profile,support,new(){Limit=LocalFeatureLimit.Until,Direction=new(0,0,1)},until:stop);
        using var result=plan.Build();Require(result.Diagnostics.AlgorithmDone&&result.Diagnostics.ShapeIsValid,result.Diagnostics.Message);
        var shape=result.RequireShape();double volume=Mass(shape,InspectionPropertyKind.Volume);
        double expected=kind switch
        {
            "sphere-lower"=>500+Math.PI*4*13-2*Math.PI/3*(216-Math.Pow(32,1.5)),
            "cylinder-lower"=>500+4*(52-(2*Math.Sqrt(32)+36*Math.Asin(1d/3))),
            _=>500+16*7
        };
        bool analyticVolumeMatches=Math.Abs(volume-expected)<=1e-5;
        // The undersized limiter is a rejection case: a successful native Until
        // may also have the wrong extent, so do not certify its volume.
        if(kind!="plane-small")Require(analyticVolumeMatches,$"{kind} independent analytic volume {expected:R} differs from {volume:R}.");
        double capArea=0,coveredArea=0;var topology=shape.GetFaces();
        try
        {
            var type=kind.StartsWith("sphere")?SurfaceGeometryType.Sphere:kind.StartsWith("cylinder")?SurfaceGeometryType.Cylinder:SurfaceGeometryType.Plane;
            foreach(var face in topology)
            {
                if(face.GetFaceSurfaceSnapshot().SurfaceType!=type||face.GetBoundingBox().Minimum.Z<=5+1e-6)continue;
                capArea+=Mass(face,InspectionPropertyKind.Area);using var common=face.Common(stop);
                var included=common.GetFaces();try{coveredArea+=included.Sum(f=>Mass(f,InspectionPropertyKind.Area));}finally{foreach(var f in included)f.Dispose();}
            }
        }
        finally{foreach(var face in topology)face.Dispose();}
        Require(capArea>1e-6,"No limiter cap found.");bool finiteDomainCovered=Math.Abs(capArea-coveredArea)<=Math.Max(1e-7,capArea*1e-9);
        Require(finiteDomainCovered==(kind!="plane-small"),"Actual finite trimmed limiter coverage differs from the contract.");
        ShapeExchange.WriteBrep(shape,Path.Combine(output,kind+".brep"));
        rows.Add(new{kind,volume,expected,analyticVolumeMatches,capArea,coveredArea,finiteDomainCovered,nativeDone=result.Diagnostics.AlgorithmDone,nativeValid=result.Diagnostics.ShapeIsValid,
            disclosure=kind=="plane-small"?"Negative prerequisite: native Until succeeded but the actual output fails finite limiter coverage and may have the wrong extent; application must reject it.":"Independent analytic volume and finite original limiter coverage both pass."});
    }
}
finally{foreach(var face in faces)face.Dispose();}
var modules=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(m=>Path.GetDirectoryName(m.FileName)==Path.Combine(AppContext.BaseDirectory,"occt"))
    .Select(m=>new{path=m.FileName,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(m.FileName)))}).ToArray();
File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{passed=true,scope="Four SDK prerequisite cases; application/UI/history/branch proofs not implemented.",rows,nativeModules=modules},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine("D5 SDK prerequisite PASS: independent planar/sphere/cylinder volumes and actual finite cap coverage; small planar domain remains explicitly unsafe without application rejection.");

static Shape Profile(bool round)
{
    if(round)
    {
        using var edge=ShapeFactory.CreateCircleEdge(new(5,5,5),new(0,0,1),2);
        using var wire=ShapeFactory.CreateWire([edge]);return ShapeFactory.CreatePlanarFace(wire);
    }
    using var polygon=ShapeFactory.CreatePolygonWire([new(3,3,5),new(7,3,5),new(7,7,5),new(3,7,5)],true);
    return ShapeFactory.CreatePlanarFace(polygon);
}
