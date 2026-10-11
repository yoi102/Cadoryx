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
using OcctSharp;

if(args.Length!=3)throw new ArgumentException("Usage: FiniteContactProbe frozen-candidate window-evidence output");
string frozen=Path.GetFullPath(args[0]),evidence=Path.GetFullPath(args[1]),output=Path.GetFullPath(args[2]);
Directory.CreateDirectory(output);var rows=new List<object>();
string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
void Progress(string message)=>File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
foreach(string path in Directory.GetFiles(AppContext.BaseDirectory,"Cadoryx.*.dll"))
    Require(Hash(path)==Hash(Path.Combine(frozen,Path.GetFileName(path))),"Consumer differs from frozen application: "+path);
var kernel=new OcctGeometryKernel(new IsolatedMetricThreadExecutor(Path.Combine(frozen,"Cadoryx.wpf.exe")),new IsolatedDrawingExecutor(Path.Combine(frozen,"Cadoryx.wpf.exe")));
var storage=new CadDocumentStorage(datumResolver:kernel);
foreach(var(language,caption) in new[]{("en-US",DrawingCaptionLanguage.English),("zh-CN",DrawingCaptionLanguage.Chinese),("ja-JP",DrawingCaptionLanguage.Japanese)})
for(int kind=0;kind<4;kind++)
{
    var assets=new MemoryAssetStore();Progress(language+" kind="+kind+" reopen actual window document");
    using(var loaded=await storage.LoadAsync(Path.Combine(evidence,$"finite-{kind}-{language}.cadoryx"),assets))
    await using(var session=new CadDocumentSession(loaded.Snapshot,assets,kernel,new InlineSessionDispatcher()))
    {
        var seed=session.Snapshot.Sketches.Values.Last(s=>s.Name=="Finite contact "+kind);
        // Snapshot dictionary order is not a timestamp: the saved file may include
        // sketches from earlier languages. Select the author with the newest window
        // identity by comparing to the immediately preceding file where necessary.
        if(language!="en-US")
        {
            string previous=language=="zh-CN"?"en-US":"zh-CN";
            using var earlier=await storage.LoadAsync(Path.Combine(evidence,$"finite-3-{previous}.cadoryx"),assets);
            seed=session.Snapshot.Sketches.Values.Single(s=>s.Name=="Finite contact "+kind&&!earlier.Snapshot.Sketches.ContainsKey(s.Id));
        }
        var contact=seed.Constraints.OfType<FiniteParametricContactConstraint>().Single();
        Require(contact.ParameterA==.46&&contact.ParameterB==.54,"Window author substituted parameters.");
        Require(contact.Branch==(kind is 1 or 3?FiniteContactTangentBranch.OppositeDirection:FiniteContactTangentBranch.SameDirection),"Window direction mismatch.");
        NativeOracle(seed,contact.A,contact.ParameterA);NativeOracle(seed,contact.B,contact.ParameterB);
        var parameter=new CadParameter(Guid.NewGuid(),"finite_t",ParameterDimension.Scalar,"0.43");
        var book=new ParameterBook([parameter],[new(Guid.NewGuid(),parameter.Id,ParameterTarget.Sketch(seed.Id,contact.Id,"ParameterA"))]);
        Guid? sheetId=null;FeatureId? bodyFeature=null;
        if(kind==1)
        {
            var source=SketchProfileReference.CreateBezierSegment(seed,contact.B);
            await session.ExecuteAsync(new AddBodyCommand(source.Resolve(session.Snapshot,seed.PartId,new ExtrudeRecipe(new([]),2,seed.Plane)),"Finite contact body",seed.PartId,sketchSource:source));
            bodyFeature=session.Snapshot.Features.Values.Single(f=>f.SketchSource?.SketchId==seed.Id).Id;
            var sheet=DrawingSheetTemplate.Create("Finite contact");sheet=sheet with{Template=sheet.Template! with{Language=caption}};sheetId=sheet.Id;
            await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
            await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Top",DrawingViewKind.Top,new(110,75),1));
        }
        var before=session.Snapshot;Progress(language+" kind="+kind+" scalar driver and native oracle");
        await session.ExecuteAsync(new SetParametersCommand(book));var accepted=session.Snapshot;var sketch=accepted.Sketches[seed.Id];
        SketchFiniteParametricContact.RequireSolvedRelations(sketch);NativeOracle(sketch,contact.A,.43);NativeOracle(sketch,contact.B,.54);
        Require(seed.Points.Select(p=>p.Id).SequenceEqual(sketch.Points.Select(p=>p.Id)),"Contact merged author point identities.");
        if(bodyFeature is {} id)
        {
            var feature=accepted.Features[id];var b=sketch.Beziers.Single(b=>b.Id==contact.B);
            Point2d P(SketchEntityId point)=>sketch.Points.Single(p=>p.Id==point).Position;
            var a=P(b.Start);var e=P(b.End);var c=P(b.Control);
            double volume=2*Math.Abs((e.X-a.X)*(c.Y-a.Y)-(e.Y-a.Y)*(c.X-a.X))/3;
            Require(Math.Abs(feature.Result.VolumeMm3-volume)<=1e-6,"Native volume differs from analytic quadratic area.");
            var view=accepted.DrawingSheets[sheetId!.Value].Views.Single();
            Require(!feature.IsStale&&feature.SketchSource!.Revision==sketch.Revision&&view.StaleReason is null&&view.Strokes.Length>0&&view.Sources.Any(s=>s.Revision==feature.Result.Revision),"Body/drawing failed atomic continuation.");
            TechnicalDrawingPdf.Write(accepted,Path.Combine(output,"finite-contact-"+language+".pdf"));
        }
        int count=assets.Count;long generation=session.Generation;bool refused=false;
        try{await session.ExecuteAsync(new SetParametersCommand(book with{Parameters=[parameter with{Expression="1"}]}));}
        catch(CadValidationException){refused=true;}
        Require(refused&&ReferenceEquals(accepted,session.Snapshot)&&count==assets.Count&&generation==session.Generation,"Invalid endpoint edit was not atomic.");
        await session.UndoAsync();Require(ReferenceEquals(before,session.Snapshot),"Driver undo changed identity.");
        await session.RedoAsync();Require(ReferenceEquals(accepted,session.Snapshot),"Driver redo changed identity.");
        string path=Path.Combine(output,$"finite-{kind}-{language}.cadoryx");await session.SaveAsync(storage,path);
        using(var reopened=await storage.LoadAsync(path,assets))SketchFiniteParametricContact.RequireSolvedRelations(reopened.Snapshot.Sketches[seed.Id]);
        rows.Add(new{language,kind,sketch=seed.Id.ToString(),curveA=contact.A.ToString(),curveB=contact.B.ToString(),parameterA=.43,parameterB=.54,direction=contact.Branch.ToString(),bodyFeature=bodyFeature?.ToString(),sheetId});
    }
    Require(assets.Count==0,"Consumer retained assets.");Progress(language+" kind="+kind+" PASS; zero assets");
}
var app=Directory.GetFiles(AppContext.BaseDirectory,"Cadoryx.*.dll").Select(p=>new{path=p,sha256=Hash(p)}).ToArray();
var native=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(m=>Path.GetDirectoryName(m.FileName)==Path.Combine(AppContext.BaseDirectory,"occt"))
    .Select(m=>new{path=m.FileName,sha256=Hash(m.FileName)}).ToArray();
foreach(var module in native)Require(module.sha256==Hash(Path.Combine(frozen,"occt",Path.GetFileName(module.path))),"Native payload differs from frozen candidate.");
File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{passed=true,frozen,applicationPayload=app,nativeModules=native,rows},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine("Frozen finite contact consumer PASS: twelve window authors, native exact point/tangent oracle, scalar drivers, analytic body volume, drawings, PDFs, undo/redo, reopen and zero assets.");

static void NativeOracle(CadSketch sketch,SketchEntityId id,double parameter)
{
    var points=sketch.Points.ToDictionary(p=>p.Id,p=>p.Position);SketchPoint2d P(SketchEntityId point)=>new(points[point].X,points[point].Y);SketchCurve2d curve;
    if(sketch.Lines.SingleOrDefault(l=>l.Id==id) is {} line)curve=SketchCurve2d.Segment(P(line.Start),P(line.End));
    else if(sketch.Beziers.SingleOrDefault(b=>b.Id==id) is {} bezier)curve=SketchCurve2d.Bezier([P(bezier.Start),P(bezier.Control),P(bezier.End)]);
    else
    {
        var spline=sketch.Splines.Single(s=>s.Id==id);var definition=CubicSplineKnotVector.Definition(spline.Controls.Select(p=>points[p]).ToImmutableArray(),spline.Knots);
        curve=SketchCurve2d.BSpline(definition.Controls.Select(p=>new SketchPoint2d(p.X,p.Y)).ToArray(),definition.Knots,definition.Multiplicities,3);
    }
    using var edge=SketchModeling.CreateEdge(curve,SketchPlane.XY);var interval=edge.GetEdgeCurveSnapshot();
    var native=edge.EvaluateEdgeDerivatives(interval.FirstParameter+parameter*(interval.LastParameter-interval.FirstParameter));
    var author=SketchFiniteParametricContact.Evaluate(sketch,id,parameter);double speed=double.Hypot(native.FirstDerivative.X,native.FirstDerivative.Y);
    if(!edge.IsValid||double.Hypot(author.Position.X-native.Point.X,author.Position.Y-native.Point.Y)>1e-7||
        double.Hypot(author.Tangent.X-native.FirstDerivative.X/speed,author.Tangent.Y-native.FirstDerivative.Y/speed)>1e-9)
        throw new InvalidOperationException("Exact author position/tangent differs from independent OCCT edge.");
}
