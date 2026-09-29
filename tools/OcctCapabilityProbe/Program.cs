using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using OcctSharp;

// NuGet-only consumer. No Cadoryx or upstream source/project references and no custom P/Invoke.
string output=Path.GetFullPath(args.Length>0?args[0]:"capability-evidence");Directory.CreateDirectory(output);
var results=new List<object>();int failures=0;
void Probe(string name,Func<object> run)
{
    var timer=Stopwatch.StartNew();
    try{var evidence=run();results.Add(new{name,passed=true,elapsedMs=timer.ElapsedMilliseconds,evidence});Console.WriteLine("PASS "+name);}
    catch(Exception e){failures++;results.Add(new{name,passed=false,error=e.ToString()});Console.WriteLine("FAIL "+name+": "+e.Message);}
}
static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
static double Volume(Shape shape){Require(shape.IsValid,"Invalid shape.");return Math.Abs(shape.InspectProperties(InspectionPropertyKind.Volume).Mass);}
static void Near(double actual,double expected,double tolerance=1e-5)=>Require(Math.Abs(actual-expected)<=tolerance,$"Expected {expected}, got {actual}.");
static RepairSelection Select(RepairSnapshot snapshot,ShapeKind kind,Func<BoundingBox3d,bool> predicate)
{
    var matches=new List<RepairSelection>();
    foreach(var item in snapshot.Topology.Where(t=>t.Kind==kind))
    {using var shape=snapshot.CopySubshape(item.Selection);if(predicate(shape.GetBoundingBox()))matches.Add(item.Selection);}
    Require(matches.Count==1,"Fixture must identify exactly one subshape.");return matches[0];
}
static RepairSelection Vertical(RepairSnapshot s,double x,double y)=>Select(s,ShapeKind.Edge,b=>
    Math.Abs(b.Minimum.X-x)<1e-5&&Math.Abs(b.Maximum.X-x)<1e-5&&Math.Abs(b.Minimum.Y-y)<1e-5&&Math.Abs(b.Maximum.Y-y)<1e-5&&b.SizeZ>14.99);

Probe("section-face-common",()=>
{
    using var box=ShapeFactory.CreateBox(10,20,30);
    using var wire=ShapeFactory.CreatePolygonWire([new(-1,-1,15),new(11,-1,15),new(11,21,15),new(-1,21,15)],true);
    using var plane=ShapeFactory.CreatePlanarFace(wire);using var cap=box.Common(plane);
    Require(cap.IsValid,"Section cap is invalid.");var faces=cap.GetSubShapes(ShapeKind.Face);
    try{Near(faces.Sum(f=>f.InspectProperties(InspectionPropertyKind.Area).Mass),200);}finally{foreach(var face in faces)face.Dispose();}
    return new{areaMm2=200,preservesTrimmedFaces=true};
});
Probe("native-dimension-contract",()=>
{
    Require(typeof(OcctViewer).GetMethod(nameof(OcctViewer.DisplayLengthDimension),[typeof(GpPoint),typeof(GpPoint),typeof(ViewerPlaneEquation),typeof(ViewerDimensionStyle)]) is not null,"Length dimension API missing.");
    Require(typeof(OcctViewer).GetMethod(nameof(OcctViewer.DisplayAngleDimension),[typeof(GpPoint),typeof(GpPoint),typeof(GpPoint),typeof(ViewerDimensionStyle)]) is not null,"Angle dimension API missing.");
    return new{families=new[]{"length","angle"},nativePresentationCoveredBy="engineering-review desktop smoke"};
});
Probe("viewer-gradient-contract",()=>
{
    var method=typeof(OcctViewer).GetMethod(nameof(OcctViewer.SetBackgroundGradient),
        [typeof(ViewerColor),typeof(ViewerColor)]);
    Require(method is not null && method.ReturnType==typeof(void),"The locked Viewer package lacks the two-color gradient API.");
    return new{method=method!.Name,topToBottom=true};
});

Probe("viewer-view-cube-contract",()=>
{
    var visible=typeof(OcctViewer).GetMethod(nameof(OcctViewer.SetViewCubeVisible),[typeof(bool)]);
    var hit=typeof(OcctViewer).GetMethod(nameof(OcctViewer.HitViewCube),[typeof(int),typeof(int)]);
    var projection=typeof(OcctViewer).GetMethod(nameof(OcctViewer.SetCubeOrientation),[typeof(ViewerCubeOrientation)]);
    Require(visible?.ReturnType==typeof(void)&&hit?.ReturnType==typeof(ViewerCubeOrientation?)&&
        projection?.ReturnType==typeof(void),"The locked Viewer package lacks the view-cube visibility, hit, or orientation contract.");
    Require(Enum.GetValues<ViewerCubeOrientation>().Length==26,"The package does not expose all cube faces, edges, and corners.");
    return new{orientations=26,viewerOwned=true};
});

Probe("exact-picked-topology-index",()=>
{
    using var box=ShapeFactory.CreateBox(10,12,15);
    using var map=RepairSnapshot.Create(box);
    var edges=box.GetSubShapes(ShapeKind.Edge);
    try
    {
        var indices=edges.Select(e=>RepairSnapshot.FindTopologyIndex(box,e)).Distinct().ToArray();
        Require(indices.Length==12&&indices.All(i=>i>=0&&map.Topology[i].Kind==ShapeKind.Edge),
            "Picked edges do not map to the exact full topology slots.");
        using var foreign=ShapeFactory.CreateBox(10,12,15);
        var other=foreign.GetSubShapes(ShapeKind.Edge);
        try{Require(RepairSnapshot.FindTopologyIndex(box,other[0])==-1,"Foreign edge mapped into source.");}
        finally{foreach(var edge in other)edge.Dispose();}
        return new{edges=indices.Length,foreignRejected=true};
    }
    finally{foreach(var edge in edges)edge.Dispose();}
});
Probe("analytic-assembly-datum-contract",()=>
{
    using var cylinder=ShapeFactory.CreateCylinder(7,12);
    using var map=RepairSnapshot.Create(cylinder);
    var kinds=new HashSet<string>();
    foreach(var item in map.Topology.Where(t=>t.Kind is ShapeKind.Face or ShapeKind.Edge))
    {
        using var subshape=map.CopySubshape(item.Selection);
        if(item.Kind==ShapeKind.Face)
        {
            var surface=subshape.GetFaceSurfaceSnapshot();
            if(surface.SurfaceType!=SurfaceGeometryType.Cylinder)continue;
            var u=(surface.FirstUParameter+surface.LastUParameter)/2;
            var v=(surface.FirstVParameter+surface.LastVParameter)/2;
            var point=subshape.EvaluateFace(u,v);
            var derivatives=subshape.EvaluateFaceDerivatives(u,v);
            Require(double.IsFinite(point.Point.X)&&double.IsFinite(derivatives.VDerivative.Z),
                "Cylindrical evaluation is non-finite.");
            kinds.Add("cylinder");
        }
        else
        {
            var curve=subshape.GetEdgeCurveSnapshot();
            if(curve.CurveType!=CurveGeometryType.Circle)continue;
            var point=subshape.EvaluateEdge((curve.FirstParameter+curve.LastParameter)/2);
            Require(double.IsFinite(point.Point.X),"Circular edge evaluation is non-finite.");
            kinds.Add("circle");
        }
    }
    Require(kinds.SetEquals(["cylinder","circle"]),"The locked package does not expose both analytic datum families.");
    return new{families=kinds.Order().ToArray(),fingerprint=map.Fingerprint};
});

Probe("primitives-transform",()=>
{
    using var box=ShapeFactory.CreateBox(10,12,15);using var moved=box.Transformed(ShapeTransform.CreateTranslationAndRotationZ(30,40,50,90));
    using var cylinder=ShapeFactory.CreateCylinder(2,5);using var sphere=ShapeFactory.CreateSphere(3);
    Near(Volume(moved),1800);Near(Volume(cylinder),20*Math.PI);Near(Volume(sphere),36*Math.PI);
    Near(moved.GetBoundingBox().Minimum.X,18,1e-5);return new{boxVolume=Volume(moved),cylinderVolume=Volume(cylinder)};
});
Probe("profile-extrude-revolve-ghost",()=>
{
    using var extrusionWire=ShapeFactory.CreatePolygonWire(
        [new(0,0,0),new(4,0,0),new(4,3,0),new(0,3,0)],true);
    using var extrusionFace=ShapeFactory.CreatePlanarFace(extrusionWire);
    using var direction=GpVec.Create(0,0,5);
    using var extrusion=extrusionFace.Extrude(direction);
    using var rotationWire=ShapeFactory.CreatePolygonWire(
        [new(1,0,0),new(3,0,0),new(3,0,2),new(1,0,2)],true);
    using var rotationFace=ShapeFactory.CreatePlanarFace(rotationWire);
    using var axis=GpAx1.Create(0,0,0,0,0,1);
    using var revolution=rotationFace.Revolve(axis,Math.PI/2);
    Near(Volume(extrusion),60);Near(Volume(revolution),4*Math.PI);
    return new{extrusionVolume=Volume(extrusion),revolutionVolume=Volume(revolution)};
});
Probe("polygon-through-hole",()=>
{
    using var outerWire=ShapeFactory.CreatePolygonWire([new(0,0,0),new(20,0,0),new(20,20,0),new(0,20,0)],true);
    using var outerFace=ShapeFactory.CreatePlanarFace(outerWire);
    using var direction=GpVec.Create(0,0,10);
    using var outer=outerFace.Extrude(direction);
    using var holeWire=ShapeFactory.CreatePolygonWire([new(6,6,-.1),new(10,6,-.1),new(10,10,-.1),new(6,10,-.1)],true);
    using var holeFace=ShapeFactory.CreatePlanarFace(holeWire);
    using var holeDirection=GpVec.Create(0,0,10.2);
    using var cutter=holeFace.Extrude(holeDirection);
    using var result=outer.Cut(cutter);
    double volume=Volume(result);Near(volume,3840);
    return new{volume,outerVolume=Volume(outer),holeVolume=Volume(cutter)};
});
Probe("exact-circle-profile-extrude",()=>
{
    using var edge=ShapeFactory.CreateCircleEdge(new(2,3,0),new(0,0,1),5);
    using var wire=ShapeFactory.CreateWire([edge]);
    using var face=ShapeFactory.CreatePlanarFace(wire);
    using var direction=GpVec.Create(0,0,8);
    using var solid=face.Extrude(direction);
    Near(Volume(solid),Math.PI*200);
    Near(solid.GetBoundingBox().Minimum.X,-3,1e-5);
    return new{exactCircularEdge=true,volume=Volume(solid)};
});
Probe("exact-circular-through-hole-cut",()=>
{
    using var outerEdge=ShapeFactory.CreateCircleEdge(new(0,0,0),new(0,0,1),10);
    using var outerWire=ShapeFactory.CreateWire([outerEdge]);
    using var outerFace=ShapeFactory.CreatePlanarFace(outerWire);
    using var direction=GpVec.Create(0,0,6);
    using var blank=outerFace.Extrude(direction);
    using var holeEdge=ShapeFactory.CreateCircleEdge(new(0,0,-0.1),new(0,0,1),2);
    using var holeWire=ShapeFactory.CreateWire([holeEdge]);
    using var holeFace=ShapeFactory.CreatePlanarFace(holeWire);
    using var holeDirection=GpVec.Create(0,0,6.2);
    using var cutter=holeFace.Extrude(holeDirection);
    using var result=blank.Cut(cutter);
    Near(Volume(result),576*Math.PI,0.001);
    return new{exactCircularHole=true,volume=Volume(result)};
});
foreach(var operation in Enum.GetValues<TopologyBooleanOperation>())Probe("boolean-history-"+operation,()=>
{
    using var box=ShapeFactory.CreateBox(10,20,30);using var tool=ShapeFactory.CreateBox(2,22,32);
    using var placed=tool.Transformed(ShapeTransform.CreateTranslation(4,-1,-1));
    using var source=RepairSnapshot.Create(box);using var cutter=RepairSnapshot.Create(placed);
    using var result=BooleanHistoryModeling.Build(operation,[source,cutter]);
    double expected=operation switch{TopologyBooleanOperation.Cut=>4800,TopologyBooleanOperation.Fuse=>6208,_=>1200};
    source.Dispose();cutter.Dispose();double volume=Volume(result.RequireShape());Near(volume,expected);
    Require(result.History.Any(h=>h.Source?.ArgumentIndex==0)&&result.History.Any(h=>h.Source?.ArgumentIndex==1),"Missing operand provenance.");
    if(operation==TopologyBooleanOperation.Cut)Require(result.History.Where(h=>h.Kind==LocalFeatureHistoryKind.Modified&&h.Source?.Kind==ShapeKind.Face)
        .GroupBy(h=>h.Source).Any(g=>g.Select(h=>h.ResultTopologyIndex).Distinct().Count()>1),"Missing real split history.");
    return new{volume,relations=result.History.Count};
});
Probe("multi-edge-fillet",()=>
{
    using var box=ShapeFactory.CreateBox(10,12,15);using var source=RepairSnapshot.Create(box);
    using var result=ContourFilletRecipe.Create(source,[FilletContourProgram.Constant(Vertical(source,0,0),.5),FilletContourProgram.Constant(Vertical(source,10,12),.5)]).Build(source);
    double volume=Volume(result.RequireShape());Near(volume,1800-2*15*(1-Math.PI/4)*.25);
    Require(result.Contours.Count==2,"Both contours must execute.");return new{volume,contours=result.Contours.Count};
});
foreach(bool sampled in new[]{false,true})Probe(sampled?"sampled-radius-fillet":"law-radius-fillet",()=>
{
    using var box=ShapeFactory.CreateBox(10,12,15);using var source=RepairSnapshot.Create(box);var edge=Vertical(source,0,0);
    var program=sampled?FilletContourProgram.Sampled(edge,[new(0,.5),new(.5,1.2),new(1,2)]):
        FilletContourProgram.FromLaw(edge,ScalarLawDefinition.Linear(new(0,1),.5,2));
    var recipe=ContourFilletRecipe.Create(source,[program]);using var simulated=recipe.Simulate(source);using var result=recipe.Build(source);
    Require(simulated.SimulatedSections.Count>0&&simulated.SimulatedSections.Max(s=>s.Radius)-simulated.SimulatedSections.Min(s=>s.Radius)>.2,"Radius did not vary.");
    double volume=Volume(result.RequireShape());Require(volume>1650&&volume<1800,"Unexpected removed material.");return new{volume,sections=simulated.SimulatedSections.Count};
});
Probe("two-distance-chamfer",()=>
{
    using var box=ShapeFactory.CreateBox(10,12,15);using var source=RepairSnapshot.Create(box);
    var support=Select(source,ShapeKind.Face,b=>Math.Abs(b.Maximum.X)<1e-5);
    using var result=ContourChamferRecipe.Create(source,[new(Vertical(source,0,0),support,ChamferDimensions.TwoDistances,1,2)]).Build(source);
    double volume=Volume(result.RequireShape());Near(volume,1785);return new{volume};
});
Probe("consecutive-local-features",()=>
{
    using var box=ShapeFactory.CreateBox(10,12,15);using var source=RepairSnapshot.Create(box);
    using var first=ContourFilletRecipe.Create(source,[FilletContourProgram.Constant(Vertical(source,0,0),1)]).Build(source);
    using var next=RepairSnapshot.Create(first.RequireShape());
    using var second=ContourFilletRecipe.Create(next,[FilletContourProgram.Constant(Vertical(next,10,12),1)]).Build(next);
    first.Dispose();next.Dispose();double volume=Volume(second.RequireShape());Near(volume,1800-2*15*(1-Math.PI/4));
    return new{volume,nonBoxSource=true,relations=second.History.Count};
});
Probe("hole",()=>
{
    using var box=ShapeFactory.CreateBox(10,12,15);using var source=RepairSnapshot.Create(box);
    using var hole=LocalFeatures.Hole(source,new(LocalHoleMode.UntilEnd,new(5,6,-1),new(0,0,1),1,0,17));
    double volume=Volume(hole.RequireShape());Near(volume,1800-15*Math.PI);return new{volume};
});
Probe("curve-profile-with-hole",()=>
{
    var outer=SketchCurveChain2d.Create([SketchCurve2d.Circle(new(0,0),5)],requireClosed:true);
    var inner=SketchCurveChain2d.Create([SketchCurve2d.Circle(new(0,0),2)],requireClosed:true);
    var profile=SketchProfile2d.Create(outer,[inner]);using var solid=profile.Extrude(SketchPlane.XY,3);
    double volume=Volume(solid);Near(volume,63*Math.PI);return new{volume,holes=profile.Holes.Count};
});
Probe("three-point-arc-segment-face",()=>
{
    var arc=SketchCurve2d.CircularArc(new(5,0),5,Math.PI,-Math.PI);
    var chord=SketchCurve2d.Segment(new(10,0),new(0,0));
    var chain=SketchCurveChain2d.Create([arc,chord],requireClosed:true);
    var profile=SketchProfile2d.Create(chain);
    using var face=profile.CreateFace(SketchPlane.XY);
    using var direction=GpVec.Create(0,0,10);
    using var solid=face.Extrude(direction);
    double volume=Volume(solid);Near(volume,125*Math.PI,0.01);
    return new{volume,exactArc=true,closed=chain.IsClosed};
});
Probe("mixed-line-arc-closed-face",()=>
{
    var curves=new SketchCurve2d[]{
        SketchCurve2d.CircularArc(new(5,0),5,Math.PI,Math.PI),
        SketchCurve2d.Segment(new(10,0),new(10,10)),
        SketchCurve2d.Segment(new(10,10),new(0,10)),
        SketchCurve2d.Segment(new(0,10),new(0,0))};
    var chain=SketchCurveChain2d.Create(curves,requireClosed:true);
    using var face=SketchProfile2d.Create(chain).CreateFace(SketchPlane.XY);
    using var direction=GpVec.Create(0,0,10);
    using var solid=face.Extrude(direction);
    double volume=Volume(solid);Near(volume,1000+125*Math.PI,0.02);
    return new{volume,curves=chain.Curves.Count};
});
Probe("multi-arc-profile-with-curved-hole",()=>
{
    var outer=SketchCurveChain2d.Create([
        SketchCurve2d.CircularArc(new(5,0),5,Math.PI,Math.PI),
        SketchCurve2d.Segment(new(10,0),new(10,10)),
        SketchCurve2d.CircularArc(new(5,10),5,0,Math.PI),
        SketchCurve2d.Segment(new(0,10),new(0,0))],requireClosed:true);
    var hole=SketchCurveChain2d.Create([
        SketchCurve2d.CircularArc(new(5,5),1,Math.PI,Math.PI),
        SketchCurve2d.Segment(new(6,5),new(6,7)),
        SketchCurve2d.Segment(new(6,7),new(4,7)),
        SketchCurve2d.Segment(new(4,7),new(4,5))],requireClosed:true);
    using var solid=SketchProfile2d.Create(outer,[hole]).Extrude(SketchPlane.XY,2);
    double volume=Volume(solid);Near(volume,2*(100+25*Math.PI-4-Math.PI/2),0.03);
    return new{volume,outerArcs=2,curvedHoles=1};
});
Probe("quadratic-bezier-and-island-compound",()=>
{
    var bezier=SketchCurve2d.Bezier([new(0,0),new(5,5),new(10,0)]);
    var chord=SketchCurve2d.Segment(new(10,0),new(0,0));
    using var bezierSolid=SketchProfile2d.Create(SketchCurveChain2d.Create([bezier,chord],true))
        .Extrude(SketchPlane.XY,6);
    Near(Volume(bezierSolid),100,0.02);
    static SketchCurveChain2d Rect(double x0,double y0,double x1,double y1)=>SketchCurveChain2d.Create([
        SketchCurve2d.Segment(new(x0,y0),new(x1,y0)),SketchCurve2d.Segment(new(x1,y0),new(x1,y1)),
        SketchCurve2d.Segment(new(x1,y1),new(x0,y1)),SketchCurve2d.Segment(new(x0,y1),new(x0,y0))],true);
    using var ring=SketchProfile2d.Create(Rect(0,0,20,20),[Rect(6,6,10,10)]).Extrude(SketchPlane.XY,5);
    using var island=SketchProfile2d.Create(Rect(7,7,9,9)).Extrude(SketchPlane.XY,5);
    using var compound=ShapeFactory.CreateCompound([ring,island]);
    double volume=Volume(compound);Near(volume,1940,0.02);
    return new{bezierVolume=Volume(bezierSolid),compoundVolume=volume};
});
Probe("clamped-cubic-spline-region",()=>
{
    var spline=SketchCurve2d.BSpline([new(0,0),new(2,4),new(5,6),new(8,4),new(10,0)],
        [0,.5,1],[4,1,4],3);
    var chord=SketchCurve2d.Segment(new(10,0),new(0,0));
    using var solid=SketchProfile2d.Create(SketchCurveChain2d.Create([spline,chord],true))
        .Extrude(SketchPlane.XY,5);
    double volume=Volume(solid);Near(volume,168.5,.03);
    return new{volume,controls=5,degree=3};
});
Probe("bspline-projection-intersection",()=>
{
    var curve=SketchCurve2d.BSpline([new(0,0),new(2,3),new(4,0)],[0,1],[3,3],2);
    var middle=SketchModeling.Evaluate(curve,.5);Near(middle.Point.X,2);Near(middle.Point.Y,1.5);
    var intersections=SketchModeling.Intersect(curve,SketchCurve2d.Segment(new(2,-1),new(2,5)));
    Require(intersections.Count==1,"Expected a unique spline crossing.");Near(intersections[0].Point.Y,1.5);
    var projections=SketchModeling.Project(curve,new(2,2));Require(projections.Count>0,"Missing spline projection.");
    using var edge=SketchModeling.CreateEdge(curve,SketchPlane.XY);Require(edge.IsValid,"Invalid spline edge.");return new{intersections=intersections.Count,projections=projections.Count};
});
Probe("loft-sweep-sew",()=>
{
    using var lower=ShapeFactory.CreatePolygonWire([new(-1,-1,0),new(1,-1,0),new(1,1,0),new(-1,1,0)],true);
    using var upper=ShapeFactory.CreatePolygonWire([new(-2,-2,5),new(2,-2,5),new(2,2,5),new(-2,2,5)],true);
    using var loft=ShapeFactory.CreateLoft([lower,upper],makeSolid:true,ruled:true);Near(Volume(loft),140.0/3);
    using var spine=ShapeFactory.CreatePolygonWire([new(0,0,0),new(0,0,5)]);using var face=ShapeFactory.CreatePlanarFace(lower);
    using var pipe=ShapeFactory.CreatePipe(spine,face);Near(Volume(pipe),20);
    var faces=pipe.GetFaces();try{using var sewn=ShapeFactory.Sew(faces);Require(sewn.IsValid&&sewn.CountSubShapes(ShapeKind.Face)>=6,"Sewing lost faces.");}
    finally{foreach(var f in faces)f.Dispose();}return new{loftVolume=Volume(loft),sweepVolume=Volume(pipe)};
});
Probe("parametric-transform-selection",()=>
{
    using var document=ParametricDocument.Create();using var box=ShapeFactory.CreateBox(2,3,4);
    var definition=new ParametricFeatureDefinition(Guid.NewGuid(),"Source",ParametricFeatureKind.SourceShape,new Dictionary<string,ParametricParameter>(),[]);
    document.Add(definition,box);document.Recompute();using var before=document.GetResult(definition.Id);
    var selection=document.Select(before,ShapeKind.Face,0);document.TransformSource(definition.Id,ShapeTransform.CreateTranslation(10,0,0));document.Recompute();
    using var moved=document.Resolve(selection);Require(moved.Status==ParametricSelectionStatus.Resolved&&moved.Shape!.GetBoundingBox().Minimum.X>9.9,"Transform selection lost provenance.");
    using var history=document.GetHistory(definition.Id);Require(history.Evolutions.Any(e=>e.Kind==ParametricEvolutionKind.Modified),"Missing transform history.");
    return new{status=moved.Status.ToString(),evolutions=history.Evolutions.Count};
});
Probe("exchange-brep-step-iges-stl",()=>
{
    using var box=ShapeFactory.CreateBox(2,3,4);var files=new List<object>();
    foreach(string format in new[]{"brep","step","iges","stl"})
    {
        string path=Path.Combine(output,"exchange."+format);
        _=format switch{"brep"=>ShapeExchange.WriteBrep(box,path),"step"=>ShapeExchange.WriteStep(box,path),"iges"=>ShapeExchange.WriteIges(box,path),_=>ShapeExchange.WriteStl(box,path)};
        using var loaded=format switch{"brep"=>ShapeExchange.ReadBrep(path),"step"=>ShapeExchange.ReadStep(path),"iges"=>ShapeExchange.ReadIges(path),_=>ShapeExchange.ReadStl(path)};
        Require(loaded.IsValid&&loaded.CountSubShapes(ShapeKind.Face)>0,"Unreadable exported geometry.");
        // IGES/STL can return shells; compare bounds, and volume only for solid-preserving formats.
        Near(loaded.GetBoundingBox().SizeX,2,1e-4);if(format is "brep" or "step")Near(Volume(loaded),24);
        files.Add(new{format,bytes=new FileInfo(path).Length});
    }
    return files;
});
Probe("xde-assembly-color-step",()=>
{
    using var document=XdeDocument.Create();using var box=ShapeFactory.CreateBox(2,3,4);XdeLabel assembly;
    using(var transaction=document.BeginTransaction("Fixture"))
    {
        var definition=document.AddShape(box,"Part");definition.Color=new(.1,.4,.9);assembly=document.AddAssembly("Assembly");
        using var identity=TopLocLocation.Identity;document.AddComponent(assembly,definition,identity);
        using var transform=ShapeTransform.CreateTranslation(10,0,0).ToGpTrsf();using var placed=TopLocLocation.FromTransform(transform);
        document.AddComponent(assembly,definition,placed);transaction.Commit();
    }
    string path=Path.Combine(output,"assembly.step");document.WriteStep(path);
    var occurrences=assembly.GetOccurrences();try{Require(occurrences.Count==2,"Repeated instances were lost.");}finally{foreach(var occurrence in occurrences)occurrence.Dispose();}
    using var reopened=XdeDocument.ReadStep(path);
    // Parse a public XDE round trip rather than merely checking that the writer returned a path.
    var free=reopened.GetFreeShapes();Require(free.Length>0,"Empty STEP assembly.");
    var instances=free.SelectMany(l=>l.GetOccurrences()).ToArray();
    try
    {
        Require(instances.Length==2,"STEP lost repeated occurrences.");
        Require(instances.Select(i=>i.ReferredLabel.Entry).Distinct().Count()==1,"Shared definition was duplicated.");
        var positions=new List<double>();
        foreach(var instance in instances)
        {
            Require(instance.ReferredLabel.Name=="Part","Part name was lost.");
            var color=instance.ReferredLabel.Color;Require(color is { } c&&Math.Abs(c.Red-.1)<1e-5&&Math.Abs(c.Green-.4)<1e-5&&Math.Abs(c.Blue-.9)<1e-5,"Part color was lost.");
            using var located=instance.GetLocatedShape();Near(Volume(located),24);positions.Add(located.GetBoundingBox().Minimum.X);
        }
        positions.Sort();Near(positions[0],0);Near(positions[1],10);
    }
    finally{foreach(var instance in instances)instance.Dispose();}
    return new{occurrences=instances.Length,sharedDefinition=true,colorPreserved=true,placementPreserved=true,bytes=new FileInfo(path).Length};
});
Probe("exact-review-properties-and-distance",()=>
{
    using var box=ShapeFactory.CreateBox(10,20,30);
    using var transform=GpTrsf.Create(25,0,0,0,0,1,0);using var placed=box.Transformed(transform);
    double area=0;var faces=box.GetFaces();
    try{area=faces.Sum(face=>face.InspectProperties(InspectionPropertyKind.Area).Mass);}finally{foreach(var face in faces)face.Dispose();}
    Near(area,2200);
    var volume=placed.InspectProperties(InspectionPropertyKind.Volume);Near(volume.Mass,6000);Near(volume.CenterOfMass.X,30);
    var distance=box.DistanceTo(placed);Near(distance.Distance,15);
    return new{area,volume=volume.Mass,distance=distance.Distance,solutions=distance.SolutionCount};
});
Probe("world-plane-section-curves",()=>
{
    using var box=ShapeFactory.CreateBox(10,20,30);
    using var wire=ShapeFactory.CreatePolygonWire([new(-1,-1,15),new(11,-1,15),new(11,21,15),new(-1,21,15)],true);
    using var plane=ShapeFactory.CreatePlanarFace(wire);using var section=box.Section(plane);
    var edges=section.GetSubShapes(ShapeKind.Edge);
    try{Require(edges.Length==4,"Missing section edges.");double length=edges.Sum(e=>e.InspectProperties(InspectionPropertyKind.Length).Mass);Near(length,60);return new{edges=edges.Length,length};}
    finally{foreach(var edge in edges)edge.Dispose();}
});
Probe("solid-contact-containment-interference",()=>
{
    using var box=ShapeFactory.CreateBox(10,10,10);using var small=ShapeFactory.CreateBox(2,2,2);
    using var shift=GpTrsf.Create(5,0,0,0,0,1,0);using var moved=box.Transformed(shift);
    using var pair=box.InspectPair(moved);Require(pair.Classification==ShapePairClassification.Interfering,"Expected volume interference.");Near(pair.OverlapVolume,500);
    using var inset=GpTrsf.Create(1,1,1,0,0,1,0);using var inner=small.Transformed(inset);
    using var contained=box.InspectPair(inner);Require(contained.Classification==ShapePairClassification.Contained,"Expected containment.");Near(contained.OverlapVolume,8);
    return new{overlap=pair.OverlapVolume,contained=contained.OverlapVolume};
});
string nativeDirectory=Path.Combine(AppContext.BaseDirectory,"occt");
var nativeNames=Directory.EnumerateFiles(nativeDirectory,"*.dll").Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
var loadedNative=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(m=>nativeNames.Contains(m.ModuleName)).ToArray();
Require(loadedNative.All(m=>string.Equals(Path.GetDirectoryName(m.FileName),nativeDirectory,StringComparison.OrdinalIgnoreCase)),"A native dependency was loaded outside the consumer output.");
var modules=loadedNative
    .Select(m=>new{name=m.ModuleName,path=m.FileName,sha256=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(m.FileName)))}).ToArray();
Require(modules.Any(m=>m.name.Equals("OcctSharp.Native.dll",StringComparison.OrdinalIgnoreCase)),"Expected native bridge was not loaded from the consumer output.");
var packageAssemblies=AppDomain.CurrentDomain.GetAssemblies().Where(a=>a.GetName().Name?.StartsWith("OcctSharp",StringComparison.Ordinal)==true)
    .Select(a=>new{name=a.GetName().Name,version=a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,path=a.Location}).OrderBy(a=>a.name).ToArray();
File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{passed=failures==0,total=results.Count,failures,runtime=OcctRuntime.Info,packageAssemblies,nativeModules=modules,results},new JsonSerializerOptions{WriteIndented=true}));
Environment.ExitCode=failures==0?0:1;
