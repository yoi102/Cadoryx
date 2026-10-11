using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using OcctSharp;

if(args.Length>0&&args[0]=="--multicopy-history-capability")
{
    Environment.ExitCode=MulticopyHistoryCapabilityProbe.Run(args[1..]);return;
}

if(args.Length>0&&args[0]=="--planar-face-editing-probe")
{
    Environment.ExitCode=PlanarFaceEditingProbe.Run(args[1..]);return;
}

if(args.Length>0&&args[0]=="--contour-local-probe")
{
    Environment.ExitCode=ContourLocalProbe.Run(args[1..]);return;
}

if(args.Length>0&&args[0]=="--solid-offset-probe")
{
    Environment.ExitCode=SolidOffsetProbe.Run(args[1..]);return;
}

if(args.Length>0&&args[0]=="--iges-extrusion-probe")
{
    Environment.ExitCode=IgesExtrusionProbe.Run(args[1..]);return;
}

if(args.Length>0&&args[0]=="--planar-size-feature-probe")
{
    Environment.ExitCode=PlanarSizeFeatureProbe.Run(args[1..]);return;
}

if(args.Length>0&&args[0]=="--composed-hole-history-probe")
{
    Environment.ExitCode=ComposedHoleHistoryProbe.Run(args[1..]);return;
}

if(args.Length>0&&args[0]=="--manufacturing-hole-probe")
{
    Environment.ExitCode=ManufacturingHoleProbe.Run(args[1..]);return;
}

if(args.Length>0&&args[0]=="--manufacturing-reference-probe")
{
    Environment.ExitCode=ManufacturingReferenceProbe.Run(args[1..]);return;
}

if(args.Length>0&&args[0]=="--inspect-brep")
{
    Environment.ExitCode=ImportedInspectionProbe.Run(args[1..]);
    return;
}
if(args.Length>0&&args[0]=="--exchange-read-probe")
{
    Environment.ExitCode=ExchangeReadProbe.Run(args.Skip(1).ToArray());return;
}

// NuGet-only consumer. No Cadoryx/upstream project references or custom P/Invoke.
// The manufacturing probe also compiles the shared NuGet-only native inspector source.
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

Probe("imported-spline-pcurve-exact-inspection-spans",()=>
{
    string fixture=Path.Combine(AppContext.BaseDirectory,"Fixtures","minipc3-spline-pcurve-solid.brep");
    Require(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fixture)))==
        "475c57dc10c8603522c3523abb2bcf373f80aa6214d8b1d4c6cc116b03cb604c","Imported fixture changed.");
    using var shape=ShapeExchange.ReadBrep(fixture);Require(shape.IsValid,"Original imported fixture is invalid.");
    using var before=RepairSnapshot.Create(shape);var topology=shape.GetTopologySummary();
    using var independent=GPropProperties.FromShape(shape,GPropMode.Volume,onlyClosed:true);
    var measured=shape.InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-10);
    Near(measured.Properties.Mass,independent.Mass,1e-6*independent.Mass);
    Near(measured.Properties.CenterOfMass.X,independent.CenterOfMass.X,1e-6);
    Near(measured.Properties.CenterOfMass.Y,independent.CenterOfMass.Y,1e-6);
    Near(measured.Properties.CenterOfMass.Z,independent.CenterOfMass.Z,1e-6);
    Near(measured.Properties.Inertia.I11,independent.InertiaValue(1,1),1e-6*Math.Max(1,Math.Abs(independent.InertiaValue(1,1))));
    Near(measured.Properties.Inertia.I22,independent.InertiaValue(2,2),1e-6*Math.Max(1,Math.Abs(independent.InertiaValue(2,2))));
    Near(measured.Properties.Inertia.I33,independent.InertiaValue(3,3),1e-6*Math.Max(1,Math.Abs(independent.InertiaValue(3,3))));
    Require(measured.EstimatedRelativeError<=1e-10,"Imported volume did not meet its convergence estimate.");
    var faces=shape.GetFaces();
    try{foreach(var face in faces){using var reference=GPropProperties.FromShape(face,GPropMode.Surface);
        Near(face.InspectAdaptiveProperties(InspectionPropertyKind.Area,1e-10).Properties.Mass,
            reference.Mass,1e-6*Math.Max(1,reference.Mass));}}
    finally{foreach(var face in faces)face.Dispose();}
    using var after=RepairSnapshot.Create(shape);
    Require(before.Fingerprint==after.Fingerprint&&topology==shape.GetTopologySummary(),"Inspection changed original topology.");
    return new{volume=measured.Properties.Mass,estimatedError=measured.EstimatedRelativeError,
        originalFingerprintPreserved=true,independentMassCentroidInertia=true,faces=faces.Length};
});
Probe("imported-moment-cancellation-solid",()=>
{
    string fixture=Path.Combine(AppContext.BaseDirectory,"Fixtures","minipc3-moment-cancellation-solid.brep");
    Require(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fixture)))==
        "0ce6c5ed2ceec126a4cb73b69377179130fb51c8761ec75ebd17d7c0dfd74558","Moment fixture changed.");
    using var shape=ShapeExchange.ReadBrep(fixture);using var before=RepairSnapshot.Create(shape);
    var timer=Stopwatch.StartNew();var measured=shape.InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-10);
    timer.Stop();Require(timer.Elapsed<TimeSpan.FromSeconds(10),"Original small-solid properties took over ten seconds.");
    // Independent SDK adaptive Gauss (original fixture, 1e-12) and GK mass-only
    // reference; fixed-order default GProp is inaccurate on this fixture.
    Near(measured.Properties.Mass,.18952421132937064,1e-9);
    Near(measured.Properties.CenterOfMass.X,79.100677565953035,1e-8);
    Near(measured.Properties.CenterOfMass.Y,21.409822439018534,1e-8);
    Near(measured.Properties.CenterOfMass.Z,7.8984311373664502,1e-8);
    var m=measured.Properties.Inertia;
    Near(m.I11,.16666991267774733,1e-8);Near(m.I22,.0065840136701353913,1e-8);
    Near(m.I33,.16079661195522021,1e-8);Near(m.I12,0,1e-8);Near(m.I13,0,1e-8);
    Near(m.I23,-.0052768219160270746,1e-8);Near(m.I21,m.I12,1e-12);Near(m.I31,m.I13,1e-12);Near(m.I32,m.I23,1e-12);
    Require(measured.EstimatedRelativeError<=1e-10,"Moment convergence estimate exceeds tolerance.");
    using var after=RepairSnapshot.Create(shape);Require(before.Fingerprint==after.Fingerprint,"Moment inspection changed original topology.");
    return new{volume=measured.Properties.Mass,estimatedError=measured.EstimatedRelativeError,
        inspectionMs=timer.Elapsed.TotalMilliseconds,originalFingerprintPreserved=true,independentReference=true};
});
foreach(var sample in new[]{
    (Name:"many-faces",Hash:"08de662b2b91349e09e93c0aad998743b6100828169d5ad56160d7b5a50b7f58",Mass:5277.5345493580207,
        Center:new[]{.0057391598634751262,1.6494542465544584,-.0060364794645721611},
        Tensor:new[]{708134.92561548017,1406685.744015669,708126.14216468809,-26.93735518296257,-624.95065268648591,29.519719672163802}),
    (Name:"difficult-trim",Hash:"f58c54e9a1052685595dd0d19b0869f95ddabbac0c0d279f8e92279d3317c46e",Mass:223.84908567032869,
        Center:new[]{.0015797600917485111,.0010306625888025361,3.5072873825301185},
        Tensor:new[]{2707.7244964757961,2707.6208404158597,1106.8727003863924,.08812804478892744,-.1049137341652866,-1.8104408146209767})})
Probe("imported-original-"+sample.Name+"-solid",()=>
{
    string fixture=Path.Combine(AppContext.BaseDirectory,"Fixtures","minipc3-"+sample.Name+"-solid.brep");
    Require(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fixture)))==sample.Hash,"Original fixture drift.");
    using var shape=ShapeExchange.ReadBrep(fixture);using var before=RepairSnapshot.Create(shape);
    Require(shape.IsValid,"Original detailed fixture invalid.");
    var measured=shape.InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-9);
    Near(measured.Properties.Mass,sample.Mass,sample.Mass*1e-9);
    var c=measured.Properties.CenterOfMass;double[] center=[c.X,c.Y,c.Z];
    for(int i=0;i<3;i++)Near(center[i],sample.Center[i],1e-7);
    var m=measured.Properties.Inertia;double[] tensor=[m.I11,m.I22,m.I33,m.I12,m.I13,m.I23];
    for(int i=0;i<6;i++)Near(tensor[i],sample.Tensor[i],Math.Max(1e-6,Math.Abs(sample.Tensor[i])*1e-7));
    Near(m.I12,m.I21,1e-12);Near(m.I13,m.I31,1e-12);Near(m.I23,m.I32,1e-12);
    Require(measured.EstimatedRelativeError<=1e-9,"Detailed fixture error budget exceeded.");
    using var after=RepairSnapshot.Create(shape);Require(before.Fingerprint==after.Fingerprint,"Detailed original geometry changed.");
    return new{volume=measured.Properties.Mass,estimatedError=measured.EstimatedRelativeError,originalFingerprintPreserved=true,independentMassCenterInertia=true};
});
Probe("c0-rational-finite-spline-prism-both-directions",()=>
{
    using var basis=FreeformAuthoring.CreateCurve(FreeformCurveDefinition.BSpline(
        [new(8,0,0),new(10,4,0),new(12,7,0),new(16,0,0),new(12,-4,0),new(10,-2,0)],
        [5,5.7,7],[3,3,3],3,true,[1,.7,1.1,.8,1.2,1]));
    using var segment=FreeformAuthoring.SegmentCurve(basis,new(7.5,9));
    var definition=FreeformAuthoring.GetCurveDefinition(segment);var observations=new List<object>();
    var forward=SketchCurve2d.BSpline(definition.Poles.Select(p=>new SketchPoint2d(p.X,p.Y)).ToArray(),
        definition.Knots,definition.Multiplicities,definition.Degree,definition.Periodic,definition.Weights);
    foreach(var curve in new[]{forward,forward.Reverse()})
    {
        var chain=SketchCurveChain2d.Create([curve,SketchCurve2d.Segment(curve.Evaluate(1).Point,curve.Evaluate(0).Point)],true);
        double area=Math.Abs(chain.Measure().SignedArea);Require(area>0,"C0 finite region has no area.");
        using var solid=SketchProfile2d.Create(chain).Extrude(SketchPlane.XY,3);
        var measured=solid.InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-10);
        Near(measured.Properties.Mass,area*3,1e-7*area*3);
        observations.Add(new{area,volume=measured.Properties.Mass,estimatedError=measured.EstimatedRelativeError});
    }
    return observations;
});
Probe("thick-solid-selected-closing-face",()=>
{
    using var box=ShapeFactory.CreateBox(10,12,15);var faces=box.GetFaces();
    try
    {
        var closing=faces.Single(f=>{var b=f.GetBoundingBox();return Math.Abs(b.Minimum.Z-15)<1e-5&&Math.Abs(b.Maximum.Z-15)<1e-5;});
        Require(RepairSnapshot.FindTopologyIndex(box,closing)>=0,"Closing face is not a member of the exact original shape.");
        using var hollow=box.MakeThickSolid([closing],-1);Near(Volume(hollow),1800-8*10*14);Require(hollow.GetSelfInterferenceReport().IsClear,"Hollow box self-interferes.");
        Near(Volume(box),1800);using var clone=hollow.Clone();box.Dispose();closing.Dispose();Near(Volume(clone),680);
        return new{wallMm=1,volume=680,explicitClosingFace=true,independentOwner=true};
    }
    finally{foreach(var face in faces)face.Dispose();}
});
Probe("face-draft-explicit-neutral-plane-and-programs",()=>
{
    using var box=ShapeFactory.CreateBox(10,12,15);using var source=RepairSnapshot.Create(box);
    var left=Select(source,ShapeKind.Face,b=>Math.Abs(b.Maximum.X)<1e-5);var right=Select(source,ShapeKind.Face,b=>b.Minimum.X>9.99);
    var p=new FaceDraftProgram(left,.05,new(0,0,1),new(0,0,0),new(0,0,1),DraftPropagation.SelectedFaceOnly);
    var recipe=FaceDraftRecipe.Create(source,[p,p with{Face=right,Angle=.1}]);
    using var preflight=recipe.Preflight(source);Require(preflight.Diagnostics.Ready,preflight.Diagnostics.Message);
    using var result=recipe.Build(source);Require(result.Diagnostics.AlgorithmDone&&result.Diagnostics.ShapeIsValid,result.Diagnostics.Message);
    var drafted=result.RequireShape();Require(drafted.GetSelfInterferenceReport().IsClear,"Draft self-interferes.");
    Near(Volume(drafted),1800-12*15*15*(Math.Tan(.05)+Math.Tan(.1))/2);Near(Volume(box),1800);
    bool duplicateRejected=false;try{using var invalid=FaceDraftRecipe.Create(source,[p,p]).Build(source);}catch(ArgumentException){duplicateRejected=true;}Require(duplicateRejected,"Duplicate draft programs accepted.");
    return new{draftedVolume=Volume(drafted),explicitFaces=2,neutralPlane="XY at Z=0",anglesRadians=new[]{.05,.1},duplicateRejected};
});
Probe("exact-plane-mirror",()=>
{
    using var source=ShapeFactory.CreateBox(2,3,4);
    using var mirror=GpTrsf.CreatePlaneMirror(5,0,0,1,0,0);
    using var reflected=source.Transformed(mirror);Near(Volume(reflected),24);
    Near(reflected.InspectProperties(InspectionPropertyKind.Volume).CenterOfMass.X,9);
    using var twice=reflected.Transformed(mirror);Near(twice.GetBoundingBox().Minimum.X,0);
    using var clone=reflected.Clone();mirror.Dispose();source.Dispose();Near(Volume(clone),24);
    return new{volume=24,exactReflection=true,independentOwner=true};
});
Probe("exact-transformed-definition-face-styles",()=>
{
    using var destination=XdeDocument.Create();XdeLabel copied;
    using(var source=XdeDocument.Create())using(var box=ShapeFactory.CreateBox(4,3,2))
    {
        XdeLabel part;
        using(var edit=source.BeginTransaction("Source face metadata"))
        {
            part=source.AddShape(box,"Source");part.Color=new(0,0,1);part.SetLayer("Manufacturing");
            part.Material=new("Steel","Source",7.85e-6,"g/mm3","mass density");
            using var repair=new RepairDocumentSession(source,part);
            var top=Select(repair.Source,ShapeKind.Face,b=>Math.Abs(b.Minimum.Z-2)<1e-5&&Math.Abs(b.Maximum.Z-2)<1e-5);
            repair.GetOrCreateSubshapeLabel(top).Color=new(1,0,0,.65);edit.Commit();
        }
        using var mirror=GpTrsf.CreatePlaneMirror(0,0,0,0,0,1);
        using(var edit=destination.BeginTransaction("Reflected face metadata"))
        {copied=destination.CopyTransformedDefinition(part,mirror,"Reflected");edit.Commit();}
    }
    using var exact=copied.Shape;Near(Volume(exact),24);Require(copied.Layers.Contains("Manufacturing"),"Layer lost.");
    Require(copied.Material?.Name=="Steel","Material lost.");
    void CheckFace(XdeLabel label)
    {
        var styles=label.GetPresentationStyles();
        try
        {
            var red=styles.Single(s=>s.EffectiveColor is {} c&&c.Red>.99&&c.Green<.01);
            Require(red.Shape.FaceCount==1,"Face metadata became an overall override.");
            Near(red.Shape.InspectProperties(InspectionPropertyKind.Area).CenterOfMass.Z,-2);
        }
        finally{foreach(var style in styles)style.Dispose();}
    }
    CheckFace(copied);var path=Path.Combine(output,"transformed-face-colors.step");destination.WriteStep(path);
    using var reopened=XdeDocument.ReadStep(path);CheckFace(reopened.GetFreeShapes().Single());
    return new{exactVolume=24,reflectedRedFaceZ=-2,sourceDisposed=true,faceStyleRoundtrip=true};
});
Probe("styled-mirror-step-roundtrip",()=>
{
    string path=Path.Combine(output,"styled-mirror.step");
    using var source=ShapeFactory.CreateBox(2,3,4);using var mirror=GpTrsf.CreatePlaneMirror(5,0,0,1,0,0);
    using var reflected=source.Transformed(mirror);using var compound=ShapeFactory.CreateCompound([source,reflected]);
    using(var document=XdeDocument.Create())
    {
        using(var edit=document.BeginTransaction("Mirror"))
        {
            var root=document.AddAssembly("Assembly");var body=document.AddShape(compound,"Mirrored body");body.Color=new(.2,.4,.8);
            using var identity=TopLocLocation.Identity;document.AddComponent(root,body,identity);edit.Commit();
        }
        document.WriteStep(path);
    }
    using var exact=ShapeExchange.ReadStep(path);Near(Volume(exact),48);
    using var loaded=XdeDocument.ReadStep(path);
    double InstanceVolume(XdeLabel label)
    {if(label.IsAssembly)return label.GetComponents().Sum(c=>InstanceVolume(c.ReferredShape));using var shape=label.Shape;return Volume(shape);}
    Near(loaded.GetFreeShapes().Sum(InstanceVolume),48);
    Require(loaded.GetFreeShapes().Single().GetComponents().Count==1,"Style recovery inserted extra components.");
    var styles=loaded.GetFreeShapes().Single().GetComponents().Single().ReferredShape.GetPresentationStyles();
    try
    {
        Require(styles.Count>0&&styles.All(s=>s.EffectiveColor is {} c&&Math.Abs(c.Red-.2)<1e-6&&Math.Abs(c.Green-.4)<1e-6&&Math.Abs(c.Blue-.8)<1e-6),"Effective body color was lost.");
        Near(styles.Sum(s=>Volume(s.Shape)),48);
    }
    finally{foreach(var style in styles)style.Dispose();}
    return new{volume=48,preservesColor=true,noDuplicateComponents=true};
});

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

Probe("associated-datum-plane-cylinder-cone-owned-values",()=>
{
    var proofs=new List<object>();var expectedAxis=new ProbeVector(Math.Sin(Math.PI/3),0,Math.Cos(Math.PI/3));
    foreach(bool conical in new[]{false,true})
    {
        using var source=conical?ShapeFactory.CreateCone(5,2,12):ShapeFactory.CreateCylinder(5,12);
        using var placement=GpTrsf.Create(7,11,13,0,1,0,Math.PI/3);using var moved=source.Transformed(placement);using var map=RepairSnapshot.Create(moved);
        var wanted=conical?SurfaceGeometryType.Cone:SurfaceGeometryType.Cylinder;
        var analytic=map.Topology.Single(t=>{if(t.Kind!=ShapeKind.Face)return false;using var face=map.CopySubshape(t.Selection);return face.GetFaceSurfaceSnapshot().SurfaceType==wanted;});
        using var selected=map.CopySubshape(analytic.Selection);var surface=selected.GetFaceSurfaceSnapshot();
        ProbeVector Center(double v)
        {
            double span=surface.LastUParameter-surface.FirstUParameter;
            var p=ProbeVector.From(selected.EvaluateFace(surface.FirstUParameter+span*.2,v).Point);
            var q=ProbeVector.From(selected.EvaluateFace(surface.FirstUParameter+span*.5,v).Point);
            var r=ProbeVector.From(selected.EvaluateFace(surface.FirstUParameter+span*.8,v).Point);
            var a=q-p;var b=r-p;var cross=a.Cross(b);double square=cross.Dot(cross);Require(square>1e-12,"Degenerate analytic circle.");
            return p+(b.Cross(cross)*a.Dot(a)+cross.Cross(a)*b.Dot(b))*(1/(2*square));
        }
        double vspan=surface.LastVParameter-surface.FirstVParameter;var first=Center(surface.FirstVParameter+vspan*.25);var second=Center(surface.FirstVParameter+vspan*.75);
        var axis=(second-first).Normalized();Near(Math.Abs(axis.Dot(expectedAxis)),1,1e-9);Near((first-new ProbeVector(7,11,13)).Cross(expectedAxis).Length,0,1e-8);
        int planeCount=0;
        foreach(var item in map.Topology.Where(t=>t.Kind==ShapeKind.Face))
        {
            using var plane=map.CopySubshape(item.Selection);var geometry=plane.GetFaceSurfaceSnapshot();if(geometry.SurfaceType!=SurfaceGeometryType.Plane)continue;
            var at=plane.EvaluateFace((geometry.FirstUParameter+geometry.LastUParameter)/2,(geometry.FirstVParameter+geometry.LastVParameter)/2);
            Near(Math.Abs(ProbeVector.From(at.Normal).Normalized().Dot(expectedAxis)),1,1e-9);planeCount++;
        }
        Require(planeCount==2,"Two exact cap planes expected.");var fingerprint=map.Fingerprint;selected.Dispose();map.Dispose();moved.Dispose();source.Dispose();
        Near(Math.Abs(axis.Dot(expectedAxis)),1,1e-9);proofs.Add(new{family=wanted.ToString(),sourceFingerprint=fingerprint,exactFaceIndex=analytic.Selection.Index,planeCount,point=first,axis,independentCopiedValues=true});
    }
    return new{proofs};
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
Probe("standard-hole-controlled-modes",()=>
{
    using var box=ShapeFactory.CreateBox(10,12,15);using var source=RepairSnapshot.Create(box);
    var origin=new GpXyz(5,6,15);var direction=new GpXyz(0,0,-1);
    using var through=LocalFeatures.Hole(source,new(LocalHoleMode.ThroughAll,origin,direction,1));Near(Volume(through.RequireShape()),1800-15*Math.PI);
    using var directional=LocalFeatures.Hole(source,new(LocalHoleMode.BetweenBounds,origin,direction,1,First:0,Last:100));Near(Volume(directional.RequireShape()),1800-15*Math.PI);
    using var reversed=LocalFeatures.Hole(source,new(LocalHoleMode.BetweenBounds,origin,new(0,0,1),1,First:0,Last:100));Near(Volume(reversed.RequireShape()),1800-15*Math.PI);
    using var ray=ShapeFactory.CreateCylinder(1,100);using var rayLocation=GpTrsf.Create(5,6,15);using var positiveRay=ray.Transformed(rayLocation);using var rayRemoval=box.Common(positiveRay);
    Near(Volume(rayRemoval),0);Require(Math.Abs(1800-Volume(reversed.RequireShape())-Volume(rayRemoval))>1,"Directional guard failed to distinguish full-axis native cutting.");
    using var blind=LocalFeatures.Hole(source,new(LocalHoleMode.Blind,origin,direction,1,Last:6));Near(Volume(blind.RequireShape()),1800-6*Math.PI);
    using var invalid=LocalFeatures.Hole(source,new(LocalHoleMode.Blind,origin,direction,1,Last:100));Require(!invalid.Diagnostics.AlgorithmDone,"Overdeep blind hole was accepted as through.");
    using var counterbore=LocalFeatures.Hole(source,new(LocalHoleMode.Blind,origin,direction,2,Last:2));
    using var recessed=RepairSnapshot.Create(counterbore.RequireShape());using var bore=LocalFeatures.Hole(recessed,new(LocalHoleMode.ThroughAll,origin,direction,1));Near(Volume(bore.RequireShape()),1800-21*Math.PI);
    using var cone=ShapeFactory.CreateCone(1,3,2);using var placement=GpTrsf.Create(5,6,13);using var positioned=cone.Transformed(placement);using var head=box.Cut(positioned);
    using var sunk=RepairSnapshot.Create(head);using var shaft=LocalFeatures.Hole(sunk,new(LocalHoleMode.ThroughAll,origin,direction,1));Near(Volume(shaft.RequireShape()),1800-65*Math.PI/3);
    return new{throughVolume=1800-15*Math.PI,blindVolume=1800-6*Math.PI,counterboreVolume=1800-21*Math.PI,countersinkVolume=1800-65*Math.PI/3,overdeepBlindRejected=true};
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
Probe("exact-curve-and-holed-revolution",()=>
{
    var bezier=SketchCurve2d.Bezier([new(8,0),new(10,2),new(12,0)]);
    var spline=SketchCurve2d.BSpline([new(8,0),new(28d/3,2),new(32d/3,2),new(12,0)],[0,1],[4,4],3);
    var chord=SketchCurve2d.Segment(new(12,0),new(8,0));
    var rectangle=SketchCurveChain2d.Create([
        SketchCurve2d.Segment(new(4,-5),new(16,-5)),SketchCurve2d.Segment(new(16,-5),new(16,5)),
        SketchCurve2d.Segment(new(16,5),new(4,5)),SketchCurve2d.Segment(new(4,5),new(4,-5))],true);
    var hole=SketchCurveChain2d.Create([SketchCurve2d.Circle(new(10,0),1)],true);
    var profiles=new[]{SketchProfile2d.Create(SketchCurveChain2d.Create([bezier,chord],true)),
        SketchProfile2d.Create(SketchCurveChain2d.Create([spline,chord],true)),SketchProfile2d.Create(rectangle,[hole])};
    // Exact first area moments: integral x*y dx for these symmetric caps; rectangle minus circle.
    double[] moments=[80d/3,40,1200-10*Math.PI];
    using var axis=GpAx1.Create(0,0,0,0,0,1);
    for(int i=0;i<profiles.Length;i++)foreach(double angle in new[]{Math.PI,Math.Tau})
    {
        using var face=profiles[i].CreateFace(SketchPlane.XZ);using var result=face.Revolve(axis,angle);
        Require(result.GetValidationReport(geometryChecks:true,exact:true).IsValid,"Invalid curve/holed revolution.");
        Require(result.GetSelfInterferenceReport().IsClear,"Curve/holed revolution self-interferes.");
        Require(result.GetTopologySummary().UniqueCounts.SolidCount==1,"Expected exactly one solid.");
        Near(Volume(result),moments[i]*angle,.002);
    }
    return new{exactCurves=new[]{"quadratic-bezier","clamped-cubic-bspline"},holes=1,partialAndFullTurns=true};
});
Probe("nonuniform-arbitrary-cubic-exact-region-and-trim",()=>
{
    // Monotone Y makes this curve simple; its chord projections are deliberately nonmonotone.
    var poles=new SketchPoint2d[]{new(8,0),new(20,2),new(16,4),new(18,6),new(16,9)};
    var curve=SketchCurve2d.BSpline(poles,[0,.2,1],[4,1,4],3);
    var chord=SketchCurve2d.Segment(poles[^1],poles[0]);
    double integral=0;
    double[] nodes=[-.8611363115940526,-.3399810435848563,.3399810435848563,.8611363115940526];
    double[] weights=[.3478548451374538,.6521451548625461,.6521451548625461,.3478548451374538];
    foreach(var span in new[]{(0d,.2),(.2,1d)})for(int i=0;i<nodes.Length;i++)
    {
        double half=(span.Item2-span.Item1)/2,at=(span.Item1+span.Item2)/2+half*nodes[i];
        var evaluated=curve.Evaluate(at);
        integral+=half*weights[i]*(evaluated.Point.X*evaluated.FirstDerivative.Y-evaluated.Point.Y*evaluated.FirstDerivative.X);
    }
    double area=Math.Abs((integral+poles[^1].X*poles[0].Y-poles[^1].Y*poles[0].X)/2);
    using var face=SketchProfile2d.Create(SketchCurveChain2d.Create([curve,chord],true)).CreateFace(SketchPlane.XY);
    Require(face.GetValidationReport(true,true).IsValid&&face.GetSelfInterferenceReport().IsClear,"Nonuniform region invalid.");
    Near(Math.Abs(face.InspectProperties(InspectionPropertyKind.Area).Mass),area,1e-6);
    using var direction=GpVec.Create(0,0,3);using var solid=face.Extrude(direction);Near(Volume(solid),area*3,1e-5);
    var trimmed=curve.Trim(.2,.8);var reversed=trimmed.Reverse();
    foreach(double t in new[]{0d,.25,.5,.75,1})
    {
        var original=curve.Evaluate(.2+.6*t).Point;var actual=trimmed.Evaluate(t).Point;var backward=reversed.Evaluate(1-t).Point;
        Near(actual.X,original.X,1e-8);Near(actual.Y,original.Y,1e-8);Near(backward.X,original.X,1e-8);Near(backward.Y,original.Y,1e-8);
    }
    var hits=trimmed.Intersect(SketchCurve2d.Segment(new(0,4),new(30,4)));
    Require(hits.Count==1,"Expected a unique exact nonuniform crossing.");Near(hits[0].Point.Y,4);
    var onCurve=trimmed.Evaluate(hits[0].FirstNormalizedParameter).Point;Near(onCurve.X,hits[0].Point.X,1e-6);
    var projection=trimmed.Project(onCurve);double nearest=projection.Min(p=>p.Distance);
    Require(projection.Count(p=>p.Distance<=nearest+1e-7)==1,"Exact nearest on-curve projection must be unique.");Near(nearest,0,1e-7);
    using var edge=SketchModeling.CreateEdge(trimmed,SketchPlane.XY);Require(edge.IsValid,"Exact trimmed spline edge invalid.");
    return new{controls=5,nonuniformKnots=new[]{0,.2,1},nonmonotoneChord=true,area,trimmedInterval=new[]{.2,.8},exactIntersection=true};
});
Probe("copied-rational-native-edge-projection",()=>
{
    var definitions=new[]{
        FreeformCurveDefinition.Bezier([new(8,0,2),new(12,7,4),new(17,0,3)],[1,.6,1],new(.2,.85)),
        FreeformCurveDefinition.BSpline([new(8,0,2),new(10,4,3),new(12,7,5),new(14,6,4),new(16,3,2),new(18,0,3)],
            [0,.35,1],[5,1,5],4,weights:[1,.8,1.2,.6,1,1],parameterRange:new(.1,.9)),
        FreeformCurveDefinition.BSpline([new(8,0,2),new(12,4,3),new(16,0,4),new(12,-4,3)],
            [0,1,2,3,4],[1,1,1,1,1],3,periodic:true,weights:[1,.8,1,.6])};
    var evidence=new List<object>();
    foreach(var definition in definitions)
    {
        using var source=FreeformAuthoring.CreateCurve(definition);
        var copied=FreeformAuthoring.GetCurveDefinition(source);var interval=copied.ParameterRange!.Value;
        Require(copied.IsRational&&copied.Kind==definition.Kind&&copied.Degree==definition.Degree,"Copied rational family or degree was lost.");
        // Exact linear projection of homogeneous control data, without fitting a sampled curve.
        var poles=copied.Poles.Select(p=>new SketchPoint2d(p.X-2,(p.Y-3)*Math.Cos(.4)+(p.Z-20)*Math.Sin(.4))).ToArray();
        var projected=copied.Kind==FreeformGeometryKind.Bezier?SketchCurve2d.Bezier(poles,copied.Weights):
            SketchCurve2d.BSpline(poles,copied.Knots,copied.Multiplicities,copied.Degree,copied.Periodic,copied.Weights);
        double domainFirst=copied.Kind==FreeformGeometryKind.Bezier?0:copied.Knots[0];
        double domainLast=copied.Kind==FreeformGeometryKind.Bezier?1:copied.Knots[^1];
        projected=projected.Trim((interval.First-domainFirst)/(domainLast-domainFirst),(interval.Last-domainFirst)/(domainLast-domainFirst));
        foreach(double t in new[]{0d,.13,.37,.71,1})
        {
            double parameter=t==1?interval.Last:interval.First+(interval.Last-interval.First)*t;
            var original=source.EvaluateEdgeDerivatives(parameter);var actual=projected.Evaluate(t);
            Near(actual.Point.X,original.Point.X-2,1e-8);Near(actual.Point.Y,(original.Point.Y-3)*Math.Cos(.4)+(original.Point.Z-20)*Math.Sin(.4),1e-8);
            // Both public APIs report derivatives in their native parameter domains.
            double projectedSpan=projected.LastParameter-projected.FirstParameter,sourceSpan=interval.Last-interval.First;
            Near(actual.FirstDerivative.X*projectedSpan,original.FirstDerivative.X*sourceSpan,1e-7);
            Near(actual.FirstDerivative.Y*projectedSpan,(original.FirstDerivative.Y*Math.Cos(.4)+original.FirstDerivative.Z*Math.Sin(.4))*sourceSpan,1e-7);
        }
        using var owned=SketchModeling.CreateEdge(projected,SketchPlane.XY);source.Dispose();Require(owned.IsValid,"Copied projection depended on source lifetime.");
        evidence.Add(new{kind=copied.Kind.ToString(),copied.Degree,copied.Periodic,poles=poles.Length,weights=copied.Weights.Count,first=interval.First,last=interval.Last,exactControlProjection=true});
    }
    return evidence;
});
Probe("periodic-finite-interval-boundary",()=>
{
    var definitions=new[]{
        FreeformCurveDefinition.BSpline([new(8,0,0),new(12,4,0),new(16,0,0),new(12,-4,0)],
            [0,1,2,3,4],[1,1,1,1,1],3,periodic:true,weights:[1,.8,1,.6]),
        FreeformCurveDefinition.BSpline([new(8,0,0),new(10,4,0),new(12,7,0),new(16,0,0),new(12,-4,0),new(10,-2,0)],
            [5,5.7,7],[3,3,3],3,periodic:true,weights:[1,.7,1.1,.8,1.2,1]),
        FreeformCurveDefinition.BSpline([new(8,0,0),new(10,4,0),new(12,7,0),new(16,0,0),new(12,-4,0),new(10,-2,0)],
            [-3,-2.6,-1.7,-1],[2,2,2,2],3,periodic:true,weights:[1,.7,1.1,.8,1.2,1])};
    var evidence=new List<object>();
    foreach(var definition in definitions)
    {
    using var source=FreeformAuthoring.CreateCurve(definition);
    var full=SketchCurve2d.BSpline(definition.Poles.Select(p=>new SketchPoint2d(p.X,p.Y)).ToArray(),
        definition.Knots,definition.Multiplicities,definition.Degree,true,definition.Weights);
    double origin=full.FirstParameter,period=full.LastParameter-origin;
    foreach(var (a,b) in new[]{(.0625,.3125),(.875,1.125),(1.0625,1.3125),(-.1875,.0625),(.3,1.3)})
    {
        double first=origin+a*period,last=origin+b*period;
        object Attempt(Func<Shape> make)
        {
            try
            {
                using var edge=make();Require(edge.IsValid,"Periodic finite edge invalid.");var copied=FreeformAuthoring.GetCurveDefinition(edge);
                var interval=copied.ParameterRange!.Value;Near(interval.First,first,1e-12);Near(interval.Last,last,1e-12);
                var reconstructed=SketchCurve2d.BSpline(copied.Poles.Select(p=>new SketchPoint2d(p.X,p.Y)).ToArray(),copied.Knots,copied.Multiplicities,copied.Degree,copied.Periodic,copied.Weights);
                double domainFirst=reconstructed.FirstParameter,span=reconstructed.LastParameter-domainFirst;
                reconstructed=reconstructed.Trim((interval.First-domainFirst)/span,(interval.Last-domainFirst)/span);
                var points=new List<object>();
                foreach(double t in new[]{0d,.1,.5,.9,1})
                {
                    double u=interval.First+(interval.Last-interval.First)*t;var p=edge.EvaluateEdgeDerivatives(u);
                    double wrapped=(u-origin)%period;if(wrapped<0)wrapped+=period;wrapped+=origin;
                    var original=source.EvaluateEdgeDerivatives(wrapped);var projected=reconstructed.Evaluate(t);
                    Near(p.Point.X,original.Point.X,1e-8);Near(p.Point.Y,original.Point.Y,1e-8);
                    Near(p.FirstDerivative.X,original.FirstDerivative.X,1e-7);Near(p.FirstDerivative.Y,original.FirstDerivative.Y,1e-7);
                    Near(projected.Point.X,original.Point.X,1e-8);Near(projected.Point.Y,original.Point.Y,1e-8);
                    Near(projected.FirstDerivative.X,original.FirstDerivative.X,1e-7);Near(projected.FirstDerivative.Y,original.FirstDerivative.Y,1e-7);
                    points.Add(new{t,u,x=p.Point.X,y=p.Point.Y,dx=p.FirstDerivative.X,dy=p.FirstDerivative.Y});
                }
                return new{supported=true,copied.Periodic,copied.Degree,first=interval.First,last=interval.Last,knots=copied.Knots,points};
            }
            catch(Exception ex) when(ex is not InvalidOperationException){return new{supported=false,error=ex.GetType().Name,message=ex.Message};}
        }
        var nativeTrim=Attempt(()=>source.TrimEdge(first,last));
        var nativeSegment=Attempt(()=>FreeformAuthoring.SegmentCurve(source,new(first,last)));
        bool normalizedTrim;string? trimError=null;
        try{using var edge=full.Trim((first-origin)/period,(last-origin)/period).ToEdge(SketchPlane.XY);normalizedTrim=edge.IsValid;}
        catch(ArgumentException ex){normalizedTrim=false;trimError=ex.Message;}
        evidence.Add(new{origin,period,multiplicities=definition.Multiplicities,requestedFirst=first,requestedLast=last,nativeTrim,nativeSegment,normalizedTrim,trimError,
            boundaryAudit=true,applicationImplementation=false});
    }
    }
    return evidence;
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
Probe("copied-intersection-point-and-overlap-classification",()=>
{
    var curve=SketchCurve2d.BSpline([new(8,0),new(20,2),new(16,4),new(18,6),new(16,9)],[0,.2,1],[4,1,4],3);
    var same=SketchModeling.InspectIntersections(curve,curve);
    var partial=SketchModeling.InspectIntersections(curve,curve.Trim(.2,.8).Reverse());
    Require(same.HasOverlappingIntervals&&partial.HasOverlappingIntervals,"Identical/partial curves must retain overlap intervals.");
    var line=SketchCurve2d.Segment(new(0,4),new(30,4));
    var crossing=SketchModeling.InspectIntersections(curve,line);
    Require(!crossing.HasOverlappingIntervals&&crossing.PointCount==1&&crossing.Endpoints.Count==1,"An isolated crossing must remain distinct from overlap.");
    Near(crossing.Endpoints[0].Point.Y,4);
    return new{same=same.OverlapIntervalCount,partial=partial.OverlapIntervalCount,points=crossing.PointCount,copied=true};
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
Probe("brep-validity-is-not-global-interference",()=>
{
    using var a=ShapeFactory.CreateBox(2,2,2);using var t=GpTrsf.Create(1,0,0);using var b=a.Transformed(t);
    using var overlapping=ShapeFactory.CreateCompound([a,b]);
    Require(overlapping.IsValid&&overlapping.GetValidationReport(geometryChecks:true,exact:true).IsValid,
        "Counterexample changed: preserve actual local BRep validation semantics.");
    using var overlap=a.Common(b);Near(Volume(overlap),4);
    return new{localBRepValid=true,exactLocalBRepValid=true,overlapVolume=4,globalSelfInterferenceNotProvedByBRepCheck=true};
});
Probe("copied-native-self-interference",()=>
{
    using var a=ShapeFactory.CreateBox(2,2,2);using var before=RepairSnapshot.Create(a);
    Require(a.GetSelfInterferenceReport().IsClear,"Valid solid rejected by interference check.");
    using var t=GpTrsf.Create(1,0,0);using var b=a.Transformed(t);using var overlapping=ShapeFactory.CreateCompound([a,b]);
    var report=overlapping.GetSelfInterferenceReport();
    Require(!report.IsClear&&report.SelfInterferenceFaultCount>0,"Compound overlap was missed.");
    using var crossing=ShapeFactory.CreatePolygonWire([new(-2,0,0),new(2,0,0),new(0,-2,0),new(0,2,0)]);
    Require(!crossing.GetSelfInterferenceReport().IsClear,"Crossing wire was missed.");
    using var after=RepairSnapshot.Create(a);Require(before.Fingerprint==after.Fingerprint,"Source modified by check.");
    ShapeSelfInterferenceReport independent;var disposable=ShapeFactory.CreateBox(1,1,1);
    try{independent=disposable.GetSelfInterferenceReport();}finally{disposable.Dispose();}
    Require(independent.IsClear,"Copied report lost its lifetime.");
    return new{report.SelfInterferenceFaultCount,report.OtherFaultCount,copiedValues=true,inputUnchanged=true};
});
Probe("guided-sweep-and-loft-contract",()=>
{
    using var profileEdge=ShapeFactory.CreateCircleEdge(new(0,0,0),new(0,0,1),1);using var profile=ShapeFactory.CreateWire([profileEdge]);
    using var arc=ShapeFactory.CreateArcEdge(new(0,0,0),new(10-10/Math.Sqrt(2),0,10/Math.Sqrt(2)),new(10,0,10));using var spine=ShapeFactory.CreateWire([arc]);
    using var plan=GuidedSweepPlan.Create(spine,[new(profile)],new(){Frame=GuidedSweepFrame.CorrectedFrenet,SolidPolicy=SweepSolidPolicy.RequireSolid,
        Tolerance3d=1e-6,ToleranceBoundary=1e-6});using var sweep=plan.Build();var tube=sweep.RequireShape();Near(Volume(tube),5*Math.PI*Math.PI,1e-3);
    using var cornerSpine=ShapeFactory.CreatePolygonWire([new(0,0,0),new(0,0,10),new(10,0,10)]);
    using var cornerPlan=GuidedSweepPlan.Create(cornerSpine,[new(profile)],new(){Transition=GuidedSweepTransition.RightCorner,SolidPolicy=SweepSolidPolicy.RequireSolid,
        Tolerance3d=1e-6,ToleranceBoundary=1e-6});using var corner=cornerPlan.Build();Require(corner.RequireShape().IsValid,"Invalid corner sweep.");Near(Volume(corner.RequireShape()),20*Math.PI,1e-3);
    using var lower=ShapeFactory.CreatePolygonWire([new(-1,-1,0),new(1,-1,0),new(1,1,0),new(-1,1,0)],true);
    using var upper=ShapeFactory.CreatePolygonWire([new(-2,-2,5),new(2,-2,5),new(2,2,5),new(-2,2,5)],true);
    using var loft=GuidedLoft.Build([lower,upper],new(){Solid=true,Ruled=true});Near(Volume(loft.RequireShape()),140d/3);
    using var shell=GuidedLoft.Build([lower,upper],new(){Solid=false,Ruled=true});Require(shell.RequireShape().IsValid&&!shell.Diagnostics.IsSolid,"Loft shell must stay a shell.");
    return new{curvedSweepVolume=Volume(tube),cornerSweepVolume=Volume(corner.RequireShape()),ruledLoftVolume=Volume(loft.RequireShape()),shell=true};
});
Probe("prism-directions-and-explicit-planar-limit",()=>
{
    using var wire=ShapeFactory.CreatePolygonWire([new(0,0,0),new(2,0,0),new(2,2,0),new(0,2,0)],true);using var face=ShapeFactory.CreatePlanarFace(wire);
    using var positive=GpVec.Create(0,0,10);using var unplaced=face.Extrude(positive);using var move=GpTrsf.Create(0,0,-3);using var bidirectional=unplaced.Transformed(move);
    Near(Volume(bidirectional),40);Near(bidirectional.GetBoundingBox().Minimum.Z,-3,1e-5);Near(bidirectional.GetBoundingBox().Maximum.Z,7,1e-5);
    using var negative=GpVec.Create(0,0,-6);using var reversed=face.Extrude(negative);Near(Volume(reversed),24);Near(reversed.GetBoundingBox().Minimum.Z,-6,1e-5);
    using var basis=ShapeFactory.CreateBox(10,12,15);
    var faces=basis.GetFaces();
    try
    {
        var top=faces.Single(f=>Math.Abs(f.GetBoundingBox().Minimum.Z-15)<1e-5&&Math.Abs(f.GetBoundingBox().Maximum.Z-15)<1e-5);
        using var edge=ShapeFactory.CreateCircleEdge(new(5,6,15),new(0,0,1),1);using var profileWire=ShapeFactory.CreateWire([edge]);using var profile=ShapeFactory.CreatePlanarFace(profileWire);
        using var stopWire=ShapeFactory.CreatePolygonWire([new(0,0,20),new(10,0,20),new(10,12,20),new(0,12,20)],true);using var stop=ShapeFactory.CreatePlanarFace(stopWire);
        using var plan=LimitedFeaturePlan.Create(basis,profile,top,new(){Kind=LimitedFeatureKind.Prism,Limit=LocalFeatureLimit.Until,Direction=new(0,0,1)},until:stop);
        using var result=plan.Build();Near(Volume(result.RequireShape()),1800+5*Math.PI);
        return new{bidirectionalVolume=40,reversedVolume=24,untilPlaneVolume=1800+5*Math.PI,usesPlanarSupportSurface=true,finiteFaceDomainClipping=false};
    }
    finally{foreach(var f in faces)f.Dispose();}
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

internal readonly record struct ProbeVector(double X,double Y,double Z)
{
    public static ProbeVector From(GpPoint p)=>new(p.X,p.Y,p.Z);
    public double Dot(ProbeVector b)=>X*b.X+Y*b.Y+Z*b.Z;
    public double Length=>Math.Sqrt(Dot(this));
    public ProbeVector Cross(ProbeVector b)=>new(Y*b.Z-Z*b.Y,Z*b.X-X*b.Z,X*b.Y-Y*b.X);
    public ProbeVector Normalized()=>this*(1/Length);
    public static ProbeVector operator +(ProbeVector a,ProbeVector b)=>new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
    public static ProbeVector operator -(ProbeVector a,ProbeVector b)=>new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
    public static ProbeVector operator *(ProbeVector a,double s)=>new(a.X*s,a.Y*s,a.Z*s);
}
