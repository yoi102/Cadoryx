using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using OcctSharp;

if(args.Length!=3)throw new ArgumentException("Usage: MixedContactProbe frozen-candidate window-evidence output");
string frozen=Path.GetFullPath(args[0]),evidence=Path.GetFullPath(args[1]),output=Path.GetFullPath(args[2]);Directory.CreateDirectory(output);
string Hash(string p)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)));
void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
void Progress(string message)=>File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
foreach(string p in Directory.GetFiles(AppContext.BaseDirectory,"Cadoryx.*.dll"))Require(Hash(p)==Hash(Path.Combine(frozen,Path.GetFileName(p))),"Frozen application mismatch: "+p);
var kernel=new OcctGeometryKernel(new IsolatedMetricThreadExecutor(Path.Combine(frozen,"Cadoryx.wpf.exe")),new IsolatedDrawingExecutor(Path.Combine(frozen,"Cadoryx.wpf.exe")));
var storage=new CadDocumentStorage(datumResolver:kernel);var rows=new List<object>();
foreach(var(language,caption) in new[]{("en-US",DrawingCaptionLanguage.English),("zh-CN",DrawingCaptionLanguage.Chinese),("ja-JP",DrawingCaptionLanguage.Japanese)})
foreach(string kind in new[]{"Circle","Arc","Ellipse","RationalSpline"})
{
    Progress(language+" "+kind+" reopen actual production window file");var assets=new MemoryAssetStore();
    using(var loaded=await storage.LoadAsync(Path.Combine(evidence,$"mixed-{kind}-{language}.cadoryx"),assets))
    await using(var session=new CadDocumentSession(loaded.Snapshot,assets,kernel,new InlineSessionDispatcher()))
    {
        var seed=session.Snapshot.Sketches.Values.Single();var c=seed.Constraints.OfType<FiniteParametricContactConstraint>().Single();
        Require(c.ParameterA==.41&&c.ParameterB==.5&&c.Continuity==FiniteContactContinuity.Curvature,"Production author lost G2/parameters.");
        Require(c.Branch==(kind is "Arc" or "RationalSpline"?FiniteContactTangentBranch.OppositeDirection:FiniteContactTangentBranch.SameDirection),"Production direction mismatch.");
        VerifyContact(seed,c);var feature=session.Snapshot.Features.Values.Single(f=>f.Name=="Mixed contact result");VerifyVolume(seed,c.B,feature.Result,assets);
        Guid? sheetId=null;
        if(kind=="Ellipse")
        {
            var sheet=DrawingSheetTemplate.Create("Mixed G2 continuity");sheet=sheet with{Template=sheet.Template! with{Language=caption}};sheetId=sheet.Id;
            await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
            await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Top",DrawingViewKind.Top,new(95,80),1));
            await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Front",DrawingViewKind.Front,new(190,80),1));
        }
        var parameter=new CadParameter(Guid.NewGuid(),"mixed_t",ParameterDimension.Scalar,"0.43");
        var book=new ParameterBook([parameter],[new(Guid.NewGuid(),parameter.Id,ParameterTarget.Sketch(seed.Id,c.Id,"ParameterA"))]);
        var before=session.Snapshot;await session.ExecuteAsync(new SetParametersCommand(book));var accepted=session.Snapshot;var sketch=accepted.Sketches[seed.Id];
        VerifyContact(sketch,sketch.Constraints.OfType<FiniteParametricContactConstraint>().Single());VerifyVolume(sketch,c.B,accepted.Features[feature.Id].Result,assets);
        Require(sketch.Points.Select(p=>p.Id).SequenceEqual(seed.Points.Select(p=>p.Id)),"Point identities merged.");
        Require(seed.Ellipses.SequenceEqual(sketch.Ellipses)&&seed.Freeforms.SequenceEqual(sketch.Freeforms),"Copied numeric curve moved.");
        if(sheetId is {} id)
        {
            Require(accepted.DrawingSheets[id].Views.All(v=>v.StaleReason is null&&v.Strokes.Length>0&&v.Sources.Any(s=>s.Revision==accepted.Features[feature.Id].Result.Revision)),"G2 body/drawing association stale.");
            TechnicalDrawingPdf.Write(accepted,Path.Combine(output,$"mixed-continuity-{language}.pdf"));
        }
        int count=assets.Count;long generation=session.Generation;bool refused=false;
        try{await session.ExecuteAsync(new SetParametersCommand(book with{Parameters=[parameter with{Expression="1"}]}));}
        catch(CadValidationException){refused=true;}
        Require(refused&&ReferenceEquals(accepted,session.Snapshot)&&assets.Count==count&&session.Generation==generation,"Invalid parameter was not atomic.");
        await session.UndoAsync();Require(ReferenceEquals(before,session.Snapshot),"Driver undo.");await session.RedoAsync();Require(ReferenceEquals(accepted,session.Snapshot),"Driver redo.");
        string file=Path.Combine(output,$"mixed-{kind}-{language}.cadoryx");await session.SaveAsync(storage,file);
        using(var reopened=await storage.LoadAsync(file,assets))VerifyContact(reopened.Snapshot.Sketches[seed.Id],reopened.Snapshot.Sketches[seed.Id].Constraints.OfType<FiniteParametricContactConstraint>().Single());
        rows.Add(new{language,kind,sketch=seed.Id.ToString(),curveA=c.A.ToString(),curveB=c.B.ToString(),continuity="G2",direction=c.Branch.ToString(),parameterA=.43,parameterB=.5,volume=accepted.Features[feature.Id].Result.VolumeMm3,sheetId});
    }
    Require(assets.Count==0,"Consumer retained assets.");Progress(language+" "+kind+" PASS; zero assets");
}
var application=Directory.GetFiles(AppContext.BaseDirectory,"Cadoryx.*.dll").Select(p=>new{path=p,sha256=Hash(p)}).ToArray();Require(application.Length==8,"Expected eight frozen application DLLs.");
var native=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(m=>string.Equals(Path.GetDirectoryName(m.FileName),Path.Combine(AppContext.BaseDirectory,"occt"),StringComparison.OrdinalIgnoreCase))
    .Select(m=>new{path=m.FileName,sha256=Hash(m.FileName)}).ToArray();Require(native.Length==62,"Expected 62 isolated native DLLs.");
foreach(var module in native)Require(module.sha256==Hash(Path.Combine(frozen,"occt",Path.GetFileName(module.path))),"Native frozen payload differs.");
File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{passed=true,frozen,applicationPayload=application,nativeModules=native,rows},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine("Frozen mixed G2 consumer PASS: twelve real window authors, independent native point/tangent/curvature and volume integrals, drivers/drawings/three vector PDFs, invalid parameter rejection, undo/redo/reopen and zero assets.");

static void VerifyContact(CadSketch s,FiniteParametricContactConstraint c)
{
    if(c.Continuity!=FiniteContactContinuity.Curvature)throw new InvalidOperationException("G2 downgraded.");
    var a=Native(s,c.A,c.ParameterA);var b=Native(s,c.B,c.ParameterB);double sign=c.Branch==FiniteContactTangentBranch.SameDirection?1:-1;
    if(double.Hypot(a.Point.X-b.Point.X,a.Point.Y-b.Point.Y)>1e-7||Math.Abs(a.Tangent.X*b.Tangent.Y-a.Tangent.Y*b.Tangent.X)>1e-9||
        sign*(a.Tangent.X*b.Tangent.X+a.Tangent.Y*b.Tangent.Y)<=0||Math.Abs(a.Curvature-sign*b.Curvature)>1e-7)
        throw new InvalidOperationException("Independent native G2 contact proof failed.");
}
static (Point2d Point,Point2d Tangent,double Curvature) Native(CadSketch s,SketchEntityId id,double t)
{
    var points=s.Points.ToDictionary(p=>p.Id,p=>p.Position);SketchPoint2d P(SketchEntityId p)=>new(points[p].X,points[p].Y);SketchCurve2d curve;
    if(s.Circles.SingleOrDefault(e=>e.Id==id) is {} circle)curve=SketchCurve2d.Circle(P(circle.Center),circle.Radius);
    else if(s.Arcs.SingleOrDefault(e=>e.Id==id) is {} arc)
    {
        var a=points[arc.Start];var m=points[arc.Middle];var e=points[arc.End];
        double ux=m.X-a.X,uy=m.Y-a.Y,vx=e.X-a.X,vy=e.Y-a.Y,cross=ux*vy-uy*vx;
        double dx=((ux*ux+uy*uy)*vy-(vx*vx+vy*vy)*uy)/(2*cross),dy=((vx*vx+vy*vy)*ux-(ux*ux+uy*uy)*vx)/(2*cross);
        var center=new SketchPoint2d(a.X+dx,a.Y+dy);double start=Math.Atan2(-dy,-dx),sweep=Math.Atan2(e.Y-center.Y,e.X-center.X)-start;
        if(cross>0&&sweep<=0)sweep+=Math.Tau;if(cross<0&&sweep>=0)sweep-=Math.Tau;
        curve=SketchCurve2d.CircularArc(center,double.Hypot(dx,dy),start,sweep);
    }
    else if(s.Ellipses.SingleOrDefault(e=>e.Id==id) is {} ellipse)
    {
        var p=ellipse.Geometry.Principal();curve=SketchCurve2d.EllipticArc(new(ellipse.Geometry.Center.X,ellipse.Geometry.Center.Y),p.MajorRadius,p.MinorRadius,p.StartAngle,p.SweepAngle,p.AxisAngle);
    }
    else if(s.Freeforms.SingleOrDefault(e=>e.Id==id) is {} freeform)
    {
        var g=freeform.Geometry;curve=g.Kind==SketchFreeformKind.Bezier?SketchCurve2d.Bezier(g.Poles.Select(p=>new SketchPoint2d(p.X,p.Y)).ToArray(),g.Weights):
            SketchCurve2d.BSpline(g.Poles.Select(p=>new SketchPoint2d(p.X,p.Y)).ToArray(),g.Knots,g.Multiplicities,g.Degree,g.Periodic,g.Weights);
        double width=curve.LastParameter-curve.FirstParameter;curve=curve.Trim((g.FirstParameter-curve.FirstParameter)/width,(g.LastParameter-curve.FirstParameter)/width);
        if(g.Reversed)curve=curve.Reverse();
    }
    else if(s.Beziers.SingleOrDefault(e=>e.Id==id) is {} bezier)curve=SketchCurve2d.Bezier([P(bezier.Start),P(bezier.Control),P(bezier.End)]);
    else throw new InvalidOperationException("Unexpected actual window curve.");
    using var edge=SketchModeling.CreateEdge(curve,SketchPlane.XY);if(!edge.IsValid)throw new InvalidOperationException("Native contact edge invalid.");
    var interval=edge.GetEdgeCurveSnapshot();var d=edge.EvaluateEdgeDerivatives(interval.FirstParameter+(curve.Reversed?1-t:t)*(interval.LastParameter-interval.FirstParameter));
    double speed=double.Hypot(d.FirstDerivative.X,d.FirstDerivative.Y),direction=curve.Reversed?-1:1;
    double curvature=direction*(d.FirstDerivative.X*d.SecondDerivative.Y-d.FirstDerivative.Y*d.SecondDerivative.X)/(speed*speed*speed);
    if(!double.IsFinite(curvature))throw new InvalidOperationException("Nonfinite native curvature.");
    return(new(d.Point.X,d.Point.Y),new(direction*d.FirstDerivative.X/speed,direction*d.FirstDerivative.Y/speed),curvature);
}
static void VerifyVolume(CadSketch s,SketchEntityId id,GeometryAssetRef geometry,IAssetStore assets)
{
    var b=s.Beziers.Single(e=>e.Id==id);var points=s.Points.ToDictionary(p=>p.Id,p=>p.Position);var a=points[b.Start];var e=points[b.End];var c=points[b.Control];
    double expected=2*Math.Abs((e.X-a.X)*(c.Y-a.Y)-(e.Y-a.Y)*(c.X-a.X))/3;
    using var shape=OcctGeometryBridge.ReadShape(geometry,assets);double native=Math.Abs(shape.InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-10).Properties.Mass);
    if(!shape.IsValid||shape.GetTopologySummary().UniqueCounts.SolidCount!=1||Math.Abs(native-expected)>1e-6||Math.Abs(geometry.VolumeMm3-expected)>1e-6)
        throw new InvalidOperationException("Native profile volume integral differs from independent quadratic area.");
}
