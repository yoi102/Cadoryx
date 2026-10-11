using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.OffsetPrototype;
using OcctSharp;
using OffsetJoin=Cadoryx.Editor.SketchOffsetJoin;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

if(args.Length!=2)throw new ArgumentException("Usage: AnalyticalOffsetNativeProbe frozen-candidate output");
if(Process.GetProcessesByName("testhost").Length!=0)throw new InvalidOperationException("Do not overlap this native probe with a full test run.");
string frozen=Path.GetFullPath(args[0]),output=Path.GetFullPath(args[1]);
if(Directory.Exists(output))throw new InvalidOperationException("Evidence already exists.");
Directory.CreateDirectory(output);
string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
void Near(double expected,double actual,string message,double tolerance=1e-7)=>Require(double.IsFinite(actual)&&Math.Abs(expected-actual)<=tolerance,
    $"{message}: expected={expected:R}, actual={actual:R}");
void Progress(string name)=>File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {name}{Environment.NewLine}");
var application=Directory.GetFiles(AppContext.BaseDirectory,"Cadoryx.*.dll").Select(path=>new{name=Path.GetFileName(path),sha256=Hash(path)}).ToArray();
foreach(var row in application)Require(row.sha256==Hash(Path.Combine(frozen,row.name)),"Frozen application mismatch: "+row.name);
var sdk=Directory.GetFiles(AppContext.BaseDirectory,"OcctSharp*.dll").Select(path=>new{name=Path.GetFileName(path),sha256=Hash(path)}).ToArray();
foreach(var row in sdk)Require(row.sha256==Hash(Path.Combine(frozen,row.name)),"Frozen SDK mismatch: "+row.name);
Require(sdk.Length==13,"Expected 13 locked SDK managed assemblies.");
var fixtures=new List<Fixture>();
SketchDraft New()=>new(CadSketch.Create(DefinitionId.New(),"Native offset foundation",RigidTransform3d.Identity));
void Polygon(SketchDraft d,params Point2d[] positions)
{
    var points=positions.Select(p=>new SketchPoint(SketchEntityId.New(),p)).ToImmutableArray();
    var lines=Enumerable.Range(0,points.Length).Select(i=>new SketchLine(SketchEntityId.New(),points[i].Id,points[(i+1)%points.Length].Id)).ToImmutableArray();
    d.Change(s=>s with{Points=s.Points.AddRange(points),Lines=s.Lines.AddRange(lines)});
}
void Reverse(SketchDraft d)=>d.Change(s=>s with{Lines=s.Lines.Select(l=>l with{Start=l.End,End=l.Start}).ToImmutableArray(),
    Arcs=s.Arcs.Select(a=>a with{Start=a.End,End=a.Start}).ToImmutableArray()});
double Rectangle(double w,double h,double distance,OffsetJoin join)=>w*h+2*distance*(w+h)+
    (distance<0||join==OffsetJoin.Miter?4:join==OffsetJoin.Round?Math.PI:2)*distance*distance;
void Add(string name,SketchDraft source,SketchEntityId seed,double distance,OffsetJoin join,double area)
{
    var result=SketchLoopOffset.Create(source.Value,seed,distance,join);
    fixtures.Add(new(name,result.Sketch,result.Reference,area,1,join,distance));
}
foreach(var join in Enum.GetValues<OffsetJoin>())foreach(double distance in new[]{.25,1,-.25,-1})foreach(bool reverse in new[]{false,true})
{
    var d=New();d.AddRectangle(new(0,0),new(20,12));if(reverse)Reverse(d);
    Add($"rectangle-{join}-{distance}-{reverse}",d,d.Value.Lines[0].Id,distance,join,Rectangle(20,12,distance,join));
    d=New();Polygon(d,new(0,0),new(20,0),new(20,8),new(8,8),new(8,20),new(0,20));if(reverse)Reverse(d);
    double coefficient=join==OffsetJoin.Miter?4:join==OffsetJoin.Round?(distance>0?5*Math.PI/4-1:5-Math.PI/4):(distance>0?1.5:4.5);
    Add($"concave-{join}-{distance}-{reverse}",d,d.Value.Lines[0].Id,distance,join,256+80*distance+coefficient*distance*distance);
}
foreach(var join in Enum.GetValues<OffsetJoin>())foreach(double distance in new[]{1d,-1d})foreach(bool reverse in new[]{false,true})
{
    var d=New();d.AddRectangle(new(0,0),new(20,10));var top=d.Value.Lines[2];d.RemoveEntities(new[]{top.Id});
    d.AddArc(new(20,10),new(10,20),new(0,10),existingStart:top.Start,existingEnd:top.End);if(reverse)Reverse(d);
    double area=(20+2*distance)*(10+distance)+Math.PI*Math.Pow(10+distance,2)/2;
    if(distance>0)area-=(join==OffsetJoin.Round?2*(1-Math.PI/4):join==OffsetJoin.Bevel?1:0)*distance*distance;
    Add($"tangent-cap-{join}-{distance}-{reverse}",d,d.Value.Arcs[0].Id,distance,join,area);

    d=New();d.AddLine(new(-10,0),new(10,0));var bottom=d.Value.Lines[0];
    d.AddArc(new(10,0),new(Math.Sqrt(50),Math.Sqrt(50)),new(0,10),existingStart:bottom.End);
    var first=d.Value.Arcs[0];d.AddArc(new(0,10),new(-8,8),new(-10,0),existingStart:first.End,existingEnd:bottom.Start);if(reverse)Reverse(d);
    var arcResult=SketchLoopOffset.Create(d.Value,d.Value.Arcs[0].Id,distance,join);
    var arcProfile=SketchProfileBuilder.Mixed(arcResult.Sketch,arcResult.Reference.MixedCurveIds);
    // This mixed source has no simple independent scalar formula. Its authored analytical area
    // is compared to native integration; the closed-form rectangle/L/cap fixtures above are separate oracles.
    fixtures.Add(new($"line-arc-arc-{join}-{distance}-{reverse}",arcResult.Sketch,arcResult.Reference,
        Math.Abs(ProposedSketchLoopOffset.Area(arcProfile.BoundaryCurves)),1,join,distance));

    d=New();d.AddRectangle(new(0,0),new(100,80));var outer=d.Value.Lines[0].Id;
    d.AddCircle(new(50,40),20);var hole=d.Value.Circles[0].Id;d.AddRectangle(new(40,30),new(60,50));var island=d.Value.Lines[^4].Id;
    d.AddCircle(new(50,40),4);var inner=d.Value.Circles[^1].Id;if(reverse)Reverse(d);
    var region=SketchRegionOffset.Create(d.Value,new[]{outer,hole,island,inner},distance,join);
    area=Rectangle(100,80,distance,join)-Math.PI*Math.Pow(20-distance,2)+Rectangle(20,20,distance,join)-Math.PI*Math.Pow(4-distance,2);
    fixtures.Add(new($"nested-region-{join}-{distance}-{reverse}",region.Sketch,region.Roots.Single(),area,2,join,distance));
}
foreach(var join in Enum.GetValues<OffsetJoin>())foreach(double distance in new[]{1d,-1d})
{var d=New();d.AddCircle(new(2,3),5);Add($"circle-{join}-{distance}",d,d.Value.Circles[0].Id,distance,join,Math.PI*Math.Pow(5+distance,2));}
foreach(var join in new[]{OffsetJoin.Round,OffsetJoin.Bevel})
{
    var d=New();Polygon(d,new(0,0),new(100,0),new(100,.1));
    double length=double.Hypot(100,.1),perimeter=100+.1+length;
    double coefficient=join==OffsetJoin.Round?Math.PI:(1+100/length+.1/length)/2;
    Add("acute-"+join,d,d.Value.Lines[0].Id,1,join,5+perimeter+coefficient);
}
foreach(var join in Enum.GetValues<OffsetJoin>())foreach(double scale in new[]{.01,1,1000})foreach(bool reverse in new[]{false,true})
{
    double angle=.73,translation=1_000_000;
    Point2d Map(Point2d p)=>new(translation+scale*(p.X*Math.Cos(angle)-p.Y*Math.Sin(angle)),
        -translation+scale*(p.X*Math.Sin(angle)+p.Y*Math.Cos(angle)));
    var d=New();Polygon(d,new[]{new Point2d(0,0),new(20,0),new(20,12),new(0,12)}.Select(Map).ToArray());if(reverse)Reverse(d);
    Add($"translated-rotated-{join}-{scale}-{reverse}",d,d.Value.Lines[0].Id,scale,join,Rectangle(20,12,1,join)*scale*scale);
}

{
    const int count=128;const double radius=100;
    var d=New();Polygon(d,Enumerable.Range(0,count).Select(i=>new Point2d(radius*Math.Cos(Math.Tau*i/count),radius*Math.Sin(Math.Tau*i/count))).ToArray());
    double area=count*radius*radius*Math.Sin(Math.Tau/count)/2,perimeter=2*count*radius*Math.Sin(Math.PI/count);
    Add("round-exact-256-output-edges",d,d.Value.Lines[0].Id,1,OffsetJoin.Round,area+perimeter+Math.PI);
}
Require(fixtures.Count==111,"Native fixture coverage changed unexpectedly.");
var rows=new List<object>();var kernel=new OcctGeometryKernel();int exit=0;
foreach(var f in fixtures)
{
    Progress(f.Name);var assets=new MemoryAssetStore();
    try
    {
        var reference=new SketchProfileReference(f.Sketch.Id,f.Sketch.Revision,f.Region.PolygonLineIds,f.Region.CircleId)
            {MixedBoundaryIds=f.Region.MixedCurveIds,Regions=f.Region.Children};
        var snapshot=DocumentSnapshot.Create("Independent analytical offset") with{Sketches=ImmutableDictionary<SketchId,CadSketch>.Empty.Add(f.Sketch.Id,f.Sketch)};
        var recipe=reference.Resolve(snapshot,f.Sketch.PartId,new ExtrudeRecipe(new([]),3,f.Sketch.Plane));
        double volume,worldVolume;var origin=f.Sketch.Points[0].Position;
        using(var result=await kernel.EvaluateAsync(recipe,assets))
        using(var shape=OcctGeometryBridge.ReadShape(result.Geometry,assets))
        {
            Require(shape.IsValid&&shape.GetSelfInterferenceReport().IsClear,"Invalid/self-intersecting native solid");
            Require(shape.GetTopologySummary().UniqueCounts.SolidCount==f.Solids,"Wrong number of material solids");
            worldVolume=Math.Abs(shape.InspectProperties(InspectionPropertyKind.Volume).Mass);
            // Inspect the original world-coordinate production BRep directly.
            // Old .10 failures and the unsuccessful translation/reflection experiments
            // are retained separately. No transformed shape replaces this acceptance input.
            using var beforeInspection=RepairSnapshot.Create(shape);
            var beforeTopology=shape.GetTopologySummary();
            if(f.Name.StartsWith("translated-rotated-Round-")&&!f.Name.Contains("-1000-"))
                ShapeExchange.WriteBrep(shape,Path.Combine(output,f.Name+"-original.brep"));
            var inspection=shape.InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-10);
            Require(double.IsFinite(inspection.EstimatedRelativeError)&&inspection.EstimatedRelativeError>=0&&
                inspection.EstimatedRelativeError<=1e-10,"Original-world high-precision integration did not converge");
            volume=Math.Abs(inspection.Properties.Mass);
            using var afterInspection=RepairSnapshot.Create(shape);
            Require(beforeInspection.Fingerprint==afterInspection.Fingerprint&&beforeTopology==shape.GetTopologySummary(),
                "Inspection changed original BRep fingerprint/topology");
            Near(f.Area*3,volume,"Independent native volume",Math.Max(1e-8,f.Area*3*1e-8));
            Near(volume,worldVolume,"Original-world adaptive/default volume agreement",Math.Max(1e-8,volume*1e-8));
            Near(volume,result.Geometry.VolumeMm3,"Stored native volume",Math.Max(1e-8,volume*1e-8));
        }
        int curves=VerifyNativeBoundary(f.Region,f.Sketch,f.Join,Math.Abs(f.Distance));
        Require(assets.Count==0,"Native result retained assets after disposal");
        rows.Add(new{name=f.Name,outcome="PASS",expectedVolume=f.Area*3,actualVolume=volume,worldVolume,
            integrationOrigin=origin,integrationTransform="Original world-coordinate BRep; no translation or reflection; fingerprint and topology unchanged",relativeTolerance=1e-10,solids=f.Solids,nativeCurveChecks=curves});
    }
    catch(Exception error)
    {exit=1;rows.Add(new{name=f.Name,outcome="FAIL",error=error.ToString(),assets=assets.Count});}
}
var native=Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
    .Where(m=>string.Equals(Path.GetDirectoryName(m.FileName),Path.Combine(AppContext.BaseDirectory,"occt"),StringComparison.OrdinalIgnoreCase))
    .Select(m=>new{name=m.ModuleName,path=m.FileName,sha256=Hash(m.FileName)}).OrderBy(m=>m.name).ToArray();
if(native.Length!=62)exit=1;
foreach(var row in native)if(row.sha256!=Hash(Path.Combine(frozen,"occt",row.name)))exit=1;
File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{recordedAt=DateTimeOffset.UtcNow,
    status=exit==0?"PASS":"FAIL",scope="Same 111 geometry fixtures now call frozen production loop/region offset; unchanged 1e-10 adaptive inspection directly on original world-coordinate production BRep, independently compared to closed-form/stored/default volumes, with unchanged fingerprint/topology. Full B17 application/window/workflow acceptance remains separate.",
    applicationPayload=application,sdkPayload=sdk,nativePayload=native,rows},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"{(exit==0?"PASS":"FAIL")}: {rows.Count} native geometry cases, {native.Length} isolated native modules.");
return exit;

int VerifyNativeBoundary(SketchRegionReference region,CadSketch sketch,OffsetJoin join,double distance)
{
    var profile=region.CircleId is {} circle?SketchProfileBuilder.Circle(sketch,circle):
        !region.MixedCurveIds.IsEmpty?SketchProfileBuilder.Mixed(sketch,region.MixedCurveIds):SketchProfileBuilder.Polygon(sketch,region.PolygonLineIds);
    var curves=profile.Circle is {} c?SketchMixedProfile.Circle(c):profile.BoundaryCurves.IsEmpty?SketchMixedProfile.Polygon(profile.Points):profile.BoundaryCurves;
    var start=new Point2d[curves.Length];var end=new Point2d[curves.Length];var bridges=new bool[curves.Length];
    for(int i=0;i<curves.Length;i++)
    {
        var curve=curves[i];SketchCurve2d definition;
        if(curve.Middle is {} middle)
        {
            var arc=SketchArcGeometry.Through(curve.Start,middle,curve.End);
            definition=SketchCurve2d.CircularArc(new(arc.Center.X,arc.Center.Y),arc.Radius,arc.StartAngle,arc.SweepAngle);
            bridges[i]=join==OffsetJoin.Round&&Math.Abs(arc.Radius-distance)<=1e-7;
        }
        else definition=SketchCurve2d.Segment(new(curve.Start.X,curve.Start.Y),new(curve.End.X,curve.End.Y));
        using var edge=SketchModeling.CreateEdge(definition,SketchPlane.XY);Require(edge.IsValid,"Invalid independently created native curve");
        var interval=edge.GetEdgeCurveSnapshot();double direction=definition.Reversed?-1:1;
        foreach(double t in new[]{0d,.37,1d})
        {
            var sample=edge.EvaluateEdgeDerivatives(interval.FirstParameter+(definition.Reversed?1-t:t)*(interval.LastParameter-interval.FirstParameter));
            var expected=curve.Middle is {} mid?SketchArcGeometry.Through(curve.Start,mid,curve.End).At(t):
                new Point2d(curve.Start.X+t*(curve.End.X-curve.Start.X),curve.Start.Y+t*(curve.End.Y-curve.Start.Y));
            Near(expected.X,sample.Point.X,"Native directed point X");Near(expected.Y,sample.Point.Y,"Native directed point Y");Near(0,sample.Point.Z,"Native plane");
            double speed=double.Hypot(sample.FirstDerivative.X,sample.FirstDerivative.Y);Require(speed>0&&double.IsFinite(speed),"Singular native tangent");
            var tangent=new Point2d(direction*sample.FirstDerivative.X/speed,direction*sample.FirstDerivative.Y/speed);
            if(t==0)start[i]=tangent;if(t==1)end[i]=tangent;
        }
    }
    for(int i=0;i<curves.Length;i++)if(bridges[i])
    {
        var previous=end[(i+curves.Length-1)%curves.Length];var next=start[(i+1)%curves.Length];
        Near(0,double.Hypot(previous.X-start[i].X,previous.Y-start[i].Y),"Native G1 into round bridge");
        Near(0,double.Hypot(end[i].X-next.X,end[i].Y-next.Y),"Native G1 out of round bridge");
    }
    return curves.Length+region.Children.Sum(child=>VerifyNativeBoundary(child,sketch,join,distance));
}

sealed record Fixture(string Name,CadSketch Sketch,SketchRegionReference Region,double Area,int Solids,OffsetJoin Join,double Distance);
