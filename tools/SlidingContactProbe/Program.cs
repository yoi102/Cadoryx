using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using OcctSharp;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

if(args.Length!=3)throw new ArgumentException("Usage: SlidingContactProbe frozen-candidate window-prefix output");
string frozen=Path.GetFullPath(args[0]),evidence=Path.GetFullPath(args[1]),output=Path.GetFullPath(args[2]);Directory.CreateDirectory(output);
string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
void Progress(string message)=>File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
foreach(string path in Directory.GetFiles(AppContext.BaseDirectory,"Cadoryx.*.dll"))
    Require(Hash(path)==Hash(Path.Combine(frozen,Path.GetFileName(path))),"Frozen application mismatch: "+path);
foreach(string path in Directory.GetFiles(AppContext.BaseDirectory,"OcctSharp*.dll"))
    Require(Hash(path)==Hash(Path.Combine(frozen,Path.GetFileName(path))),"Frozen SDK mismatch: "+path);
var kernel=new OcctGeometryKernel(new IsolatedMetricThreadExecutor(Path.Combine(frozen,"Cadoryx.wpf.exe")),new IsolatedDrawingExecutor(Path.Combine(frozen,"Cadoryx.wpf.exe")));
var storage=new CadDocumentStorage(datumResolver:kernel);var rows=new List<object>();
foreach(var mode in new[]{FiniteContactParameterMode.SolveA,FiniteContactParameterMode.SolveB,FiniteContactParameterMode.SolveBoth})
foreach(var(language,caption) in new[]{("en-US",DrawingCaptionLanguage.English),("zh-CN",DrawingCaptionLanguage.Chinese),("ja-JP",DrawingCaptionLanguage.Japanese)})
foreach(string kind in new[]{"Circle","Arc","Ellipse","RationalSpline"})
{
    string gate=mode==FiniteContactParameterMode.SolveA?"b16-solve-a":mode==FiniteContactParameterMode.SolveB?"b16-solve-b":"b16-solve-both";
    Progress(mode+" "+language+" "+kind+" reopen actual window author");var assets=new MemoryAssetStore();
    using(var loaded=await storage.LoadAsync(Path.Combine(evidence+"-"+gate,$"sliding-{mode}-{kind}-{language}.cadoryx"),assets))
    await using(var session=new CadDocumentSession(loaded.Snapshot,assets,kernel,new InlineSessionDispatcher()))
    {
        var seed=session.Snapshot.Sketches.Values.Single();var c=seed.Constraints.OfType<FiniteParametricContactConstraint>().Single();
        Require(c.ParameterMode==mode&&c.Continuity==FiniteContactContinuity.Curvature,"Window author lost automatic G2 mode.");
        if(c.SolvesA)Require(c.SlidingA==new SlidingContactParameter(.39,.2,.6),"Window A author mismatch.");
        else Require(c.ParameterA==.37&&c.SlidingA is null,"Fixed A changed.");
        if(c.SolvesB)Require(c.SlidingB==new SlidingContactParameter(.53,.3,.7),"Window B author mismatch.");
        else Require(c.ParameterB==.5&&c.SlidingB is null,"Fixed B changed.");
        Require(c.Branch==(kind is "Arc" or "RationalSpline"?FiniteContactTangentBranch.OppositeDirection:FiniteContactTangentBranch.SameDirection),"Direction mismatch.");
        VerifyContact(seed,c);var feature=session.Snapshot.Features.Values.Single(f=>f.Name=="Sliding contact result");VerifyVolume(seed,c.B,feature.Result,assets);
        var readState=session.Snapshot;long readGeneration=session.Generation;
        var analysis=new ManagedSketchConstraintSolver().Analyze(seed);
        Require(analysis.Status==SketchAnalysisStatus.Evaluated&&analysis.IsSatisfied==true&&analysis.Rank is not null&&analysis.DegreesOfFreedom is not null,"Actual local analysis unavailable.");
        Require(ReferenceEquals(readState,session.Snapshot)&&session.Generation==readGeneration,"Local query changed history.");
        Guid? sheetId=null;
        if(kind=="Ellipse")
        {
            var sheet=DrawingSheetTemplate.Create($"Sliding {mode} contact");sheet=sheet with{Template=sheet.Template! with{Language=caption}};sheetId=sheet.Id;
            await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
            await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Top",DrawingViewKind.Top,new(95,80),1));
            await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Front",DrawingViewKind.Front,new(190,80),1));
        }
        var before=session.Snapshot;
        await session.ExecuteAsync(new SetParametersCommand(Drivers(seed,.42,.55)));
        var accepted=session.Snapshot;var sketch=accepted.Sketches[seed.Id];var actual=sketch.Constraints.OfType<FiniteParametricContactConstraint>().Single();
        VerifyContact(sketch,actual);VerifyVolume(sketch,c.B,accepted.Features[feature.Id].Result,assets);References(accepted,sketch);
        Require(seed.Ellipses.SequenceEqual(sketch.Ellipses)&&seed.Freeforms.SequenceEqual(sketch.Freeforms),"Copied numeric curves changed.");
        Require(accepted.Features[feature.Id].Result.Revision!=before.Features[feature.Id].Result.Revision,"Automatic driver did not rebuild body.");
        int count=assets.Count;long generation=session.Generation;bool refused=false;
        try{await session.ExecuteAsync(new SetParametersCommand(Drivers(sketch,1,1)));}
        catch(CadValidationException){refused=true;}
        Require(refused&&ReferenceEquals(accepted,session.Snapshot)&&assets.Count==count&&session.Generation==generation,"Invalid seed was not atomic.");
        var invalidActual=new CadParameter(Guid.NewGuid(),"invalid_actual",ParameterDimension.Scalar,"0.4");
        var invalidTarget=ParameterTarget.Sketch(sketch.Id,actual.Id,actual.SolvesA?"ParameterA":"ParameterB");refused=false;
        try{await session.ExecuteAsync(new SetParametersCommand(accepted.Parameters with
            {Parameters=accepted.Parameters.Parameters.Add(invalidActual),Bindings=accepted.Parameters.Bindings.Add(new(Guid.NewGuid(),invalidActual.Id,invalidTarget))}));}
        catch(CadValidationException){refused=true;}
        Require(refused&&ReferenceEquals(accepted,session.Snapshot)&&assets.Count==count&&session.Generation==generation,"Actual result accepted a design driver.");
        await session.UndoAsync();Require(ReferenceEquals(before,session.Snapshot),"Automatic driver undo.");await session.RedoAsync();Require(ReferenceEquals(accepted,session.Snapshot),"Automatic driver redo.");
        var root=new OccurrencePath(accepted.Id,[]);var slot=ComponentSlotId.New();
        await session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(root,sketch.PartId,slot,"Independent",RigidTransform3d.Translate(25,0,0)));
        var clone=AssemblyOccurrenceCommands.MakePartIndependent(root.Append(slot));await session.ExecuteAsync(clone);
        var copied=session.Snapshot.Sketches.Values.Single(s=>s.PartId==clone.ResultPartId);
        var copiedFeature=session.Snapshot.Features.Values.Single(f=>f.PartId==clone.ResultPartId&&f.Name=="Sliding contact result");
        Require(copied.Id!=sketch.Id&&copied.Constraints.SequenceEqual(sketch.Constraints),"Independence lost sliding authors.");
        if(sheetId is {} copySheet)
        {
            foreach(var view in session.Snapshot.DrawingSheets[copySheet].Views)
            {
                Require(view.Sources.SequenceEqual(accepted.DrawingSheets[copySheet].Views.Single(v=>v.Id==view.Id).Sources),
                    "Inserting an independent occurrence silently changed the existing drawing scope.");
                await session.ExecuteAsync(TechnicalDrawingCommands.ReselectView(copySheet,view.Id,null));
                var reselected=session.Snapshot.DrawingSheets[copySheet].Views.Single(v=>v.Id==view.Id);
                Require(reselected.StaleReason is null&&reselected.Strokes.Length>0&&
                    reselected.Sources.Any(s=>s.Feature==feature.Id)&&reselected.Sources.Any(s=>s.Feature==copiedFeature.Id),
                    "Explicit source reselection did not include the original and independent bodies.");
            }
        }
        var independentBefore=session.Snapshot;
        await session.ExecuteAsync(new SetParametersCommand(Drivers(copied,.44,.57,independentBefore.Parameters)));
        var independent=session.Snapshot;var copiedSolved=independent.Sketches[copied.Id];var copiedContact=copiedSolved.Constraints.OfType<FiniteParametricContactConstraint>().Single();
        Require(ReferenceEquals(sketch,independent.Sketches[sketch.Id])&&ReferenceEquals(accepted.Features[feature.Id],independent.Features[feature.Id]),"Independent driver changed original graph.");
        VerifyContact(copiedSolved,copiedContact);VerifyVolume(copiedSolved,copiedContact.B,independent.Features[copiedFeature.Id].Result,assets);References(independent,copiedSolved);
        Require(independent.Features[copiedFeature.Id].Result.Revision!=copiedFeature.Result.Revision,"Independent driver did not rebuild own body.");
        if(sheetId is {} id)
        {
            Require(independent.DrawingSheets[id].Views.All(v=>v.StaleReason is null&&v.Strokes.Length>0&&
                v.Sources.Any(s=>s.Feature==feature.Id&&s.Revision==independent.Features[feature.Id].Result.Revision)&&
                v.Sources.Any(s=>s.Feature==copiedFeature.Id&&s.Revision==independent.Features[copiedFeature.Id].Result.Revision)),"Independent automatic body/drawing association stale.");
            TechnicalDrawingPdf.Write(independent,Path.Combine(output,$"sliding-{mode}-{language}.pdf"));
        }
        await session.UndoAsync();Require(ReferenceEquals(independentBefore,session.Snapshot),"Independent driver undo.");await session.RedoAsync();Require(ReferenceEquals(independent,session.Snapshot),"Independent driver redo.");
        string file=Path.Combine(output,$"sliding-{mode}-{kind}-{language}.cadoryx");await session.SaveAsync(storage,file);
        using(var reopened=await storage.LoadAsync(file,assets))
        {
            reopened.Snapshot.Validate();var reopenedOriginal=reopened.Snapshot.Sketches[sketch.Id];var reopenedCopy=reopened.Snapshot.Sketches[copied.Id];
            Require(reopenedOriginal.Constraints.OfType<FiniteParametricContactConstraint>().Single()==actual&&reopenedCopy.Constraints.OfType<FiniteParametricContactConstraint>().Single()==copiedContact,"Reopen changed separate authors/actuals.");
            VerifyContact(reopenedOriginal,actual);VerifyContact(reopenedCopy,copiedContact);References(reopened.Snapshot,reopenedOriginal);References(reopened.Snapshot,reopenedCopy);
        }
        rows.Add(new{language,kind,mode=mode.ToString(),continuity="G2",direction=actual.Branch.ToString(),
            actualA=actual.ParameterA,actualB=actual.ParameterB,seedA=actual.SlidingA,seedB=actual.SlidingB,
            sketchId=sketch.Id.ToString(),copiedSketchId=copied.Id.ToString(),volume=independent.Features[feature.Id].Result.VolumeMm3,
            copiedVolume=independent.Features[copiedFeature.Id].Result.VolumeMm3,analysis.Rank,analysis.DegreesOfFreedom,sheetId});
    }
    Require(assets.Count==0,"Consumer retained assets.");Progress(mode+" "+language+" "+kind+" PASS; zero assets");
}
var application=Directory.GetFiles(AppContext.BaseDirectory,"Cadoryx.*.dll").Select(path=>new{path,sha256=Hash(path)}).ToArray();Require(application.Length==8,"Expected eight frozen application DLLs.");
var managed=Directory.GetFiles(AppContext.BaseDirectory,"OcctSharp*.dll").Select(path=>new{path,sha256=Hash(path)}).ToArray();Require(managed.Length==13,"Expected thirteen SDK DLLs.");
var native=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(m=>string.Equals(Path.GetDirectoryName(m.FileName),Path.Combine(AppContext.BaseDirectory,"occt"),StringComparison.OrdinalIgnoreCase))
    .Select(m=>new{path=m.FileName,sha256=Hash(m.FileName)}).ToArray();Require(native.Length==62,"Expected 62 isolated native DLLs.");
foreach(var module in native)Require(module.sha256==Hash(Path.Combine(frozen,"occt",Path.GetFileName(module.path))),"Native frozen payload differs.");
Require(rows.Count==36,"Incomplete automatic consumer cases.");
File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{passed=true,frozen,applicationPayload=application,managedPayload=managed,nativeModules=native,rows},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine("Frozen sliding G2 consumer PASS: 36 actual window authors, native contact/volume proofs, seed/actual drivers, independent body/drawing graph, nine vector PDFs, invalid/readonly rejection, undo/redo/reopen and zero assets.");

static ParameterBook Drivers(CadSketch sketch,double seedA,double seedB,ParameterBook? existing=null)
{
    var c=sketch.Constraints.OfType<FiniteParametricContactConstraint>().Single();
    var parameters=(existing?.Parameters??[]).ToBuilder();
    var bindings=(existing?.Bindings??[]).Where(b=>b.Target.Kind!=ParameterTargetKind.SketchConstraint||b.Target.Owner!=sketch.Id.Value).ToImmutableArray().ToBuilder();
    foreach(bool a in new[]{true,false})
    {
        if(!(a?c.SolvesA:c.SolvesB))continue;
        string side=a?"A":"B",prefix="slide_"+sketch.Id.Value.ToString("N")+"_"+side;
        var parameter=new CadParameter(Guid.NewGuid(),prefix,ParameterDimension.Scalar,(a?seedA:seedB).ToString("R",CultureInfo.InvariantCulture));
        parameters.Add(parameter);bindings.Add(new(Guid.NewGuid(),parameter.Id,ParameterTarget.Sketch(sketch.Id,c.Id,"Seed"+side)));
        parameters.Add(new(Guid.NewGuid(),prefix+"_actual",ParameterDimension.Scalar,"",ParameterTarget.Sketch(sketch.Id,c.Id,"Parameter"+side)));
    }
    return new(parameters.ToImmutable(),bindings.ToImmutable());
}
static void References(DocumentSnapshot snapshot,CadSketch sketch)
{
    var values=ParameterGraph.Evaluate(snapshot);var c=sketch.Constraints.OfType<FiniteParametricContactConstraint>().Single();
    foreach(var p in snapshot.Parameters.Parameters.Where(p=>p.Reference is {} target&&target.Owner==sketch.Id.Value))
        if(values[p.Id].Value!=(p.Reference!.Property=="ParameterA"?c.ParameterA:c.ParameterB))throw new InvalidOperationException("Read-only actual reference differs.");
}

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
