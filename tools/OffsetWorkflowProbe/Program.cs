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

if(args.Length!=3)throw new ArgumentException("Usage: OffsetWorkflowProbe frozen-candidate final-window-prefix output");
if(Process.GetProcessesByName("testhost").Length!=0)throw new InvalidOperationException("Native consumers and test runs must be serial.");
string frozen=Path.GetFullPath(args[0]),prefix=Path.GetFullPath(args[1]),output=Path.GetFullPath(args[2]);
if(Directory.Exists(output))throw new InvalidOperationException("Evidence already exists.");Directory.CreateDirectory(output);
string Hash(string p)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)));
void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
void Near(double expected,double actual,string message)=>Require(double.IsFinite(actual)&&Math.Abs(expected-actual)<=Math.Max(1e-8,Math.Abs(expected)*1e-8),$"{message}: {expected:R} != {actual:R}");
void Progress(string value)=>File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {value}{Environment.NewLine}");
var application=Directory.GetFiles(AppContext.BaseDirectory,"Cadoryx.*.dll").Select(p=>new{name=Path.GetFileName(p),sha256=Hash(p)}).ToArray();
Require(application.Length==8,"Expected eight frozen application assemblies.");
foreach(var r in application)Require(r.sha256==Hash(Path.Combine(frozen,r.name)),"Frozen application mismatch: "+r.name);
var sdk=Directory.GetFiles(AppContext.BaseDirectory,"OcctSharp*.dll").Select(p=>new{name=Path.GetFileName(p),sha256=Hash(p)}).ToArray();
Require(sdk.Length==13,"Expected 13 SDK assemblies.");foreach(var r in sdk)Require(r.sha256==Hash(Path.Combine(frozen,r.name)),"Frozen SDK mismatch.");
var kernel=new OcctGeometryKernel(new IsolatedMetricThreadExecutor(Path.Combine(frozen,"Cadoryx.wpf.exe")),new IsolatedDrawingExecutor(Path.Combine(frozen,"Cadoryx.wpf.exe")));
var storage=new CadDocumentStorage(datumResolver:kernel);var rows=new List<object>();int failures=0;
double Rectangle(double w,double h,SketchOffsetJoin join)=>w*h+2*(w+h)+(join==SketchOffsetJoin.Miter?4:join==SketchOffsetJoin.Round?Math.PI:2);
foreach(var join in Enum.GetValues<SketchOffsetJoin>())
foreach(var(language,caption) in new[]{("en-US",DrawingCaptionLanguage.English),("zh-CN",DrawingCaptionLanguage.Chinese),("ja-JP",DrawingCaptionLanguage.Japanese)})
foreach(string kind in new[]{"Region","Loop"})
{
    string name=$"{join}-{language}-{kind}",input=Path.Combine(prefix+"-"+join.ToString().ToLowerInvariant(),$"loop-accepted-{language}.cadoryx");
    Progress(name+" reopen actual production window");var assets=new MemoryAssetStore();object? acceptedRow=null;
    try
    {
        using(var loaded=await storage.LoadAsync(input,assets))
        await using(var session=new CadDocumentSession(loaded.Snapshot,assets,kernel,new InlineSessionDispatcher()))
        {
            var sketch=session.Snapshot.Sketches.Values.Single(s=>s.Name==(kind=="Region"?"B10 ":"B17 loop ")+language);
            Require(sketch.Arcs.Length==(join==SketchOffsetJoin.Round?4:0),"Window corner policy differs from the stored exact authors.");
            FeatureDefinition feature;
            if(kind=="Region")feature=session.Snapshot.Features.Values.Single(f=>f.SketchSource?.SketchId==sketch.Id);
            else
            {
                var root=SketchRegionOffset.SelectedRegions(sketch,[sketch.Lines[4].Id]).Single();
                var reference=new SketchProfileReference(sketch.Id,sketch.Revision,root.PolygonLineIds){MixedBoundaryIds=root.MixedCurveIds};
                await session.ExecuteAsync(new AddBodyCommand(reference.Resolve(session.Snapshot,sketch.PartId,new ExtrudeRecipe(new([]),3,sketch.Plane)),"Window loop body",sketch.PartId,sketchSource:reference));
                feature=session.Snapshot.Features.Values.Single(f=>f.SketchSource?.SketchId==sketch.Id);
            }
            double expected=3*(kind=="Loop"?Rectangle(20,12,join):Rectangle(50,40,join)-61*Math.PI);
            Verify(feature.Result,assets,expected,kind=="Region"?2:1);
            SketchEntityId? drivenCircle=null;SketchConstraintId? radiusConstraint=null;
            if(kind=="Region")
            {
                drivenCircle=feature.SketchSource!.Regions.Single().Children.Single().Children.Single().CircleId!.Value;
                var circle=sketch.Circles.Single(c=>c.Id==drivenCircle);var center=sketch.Points.Single(p=>p.Id==circle.Center);
                var radius=new RadiusConstraint(SketchConstraintId.New(),circle.Id,circle.Radius);radiusConstraint=radius.Id;
                var draft=new SketchDraft(sketch);draft.Change(s=>s with{Constraints=s.Constraints.Add(radius).Add(new FixPointConstraint(SketchConstraintId.New(),center.Id,center.Position))});
                await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,new ManagedSketchConstraintSolver()));
                var before=session.Snapshot;await session.ExecuteAsync(new SetParametersCommand(Drive(before,sketch.Id,radius.Id,1.5)));
                var after=session.Snapshot;sketch=after.Sketches[sketch.Id];feature=after.Features[feature.Id];
                expected=3*(Rectangle(50,40,join)-121*Math.PI+64*Math.PI-2.25*Math.PI);Verify(feature.Result,assets,expected,2);
                Require(sketch.Circles[2].Radius==3,"Result driver changed the original source circle.");
                await session.UndoAsync();Require(ReferenceEquals(before,session.Snapshot),"Driver undo changed snapshot identity.");await session.RedoAsync();Require(ReferenceEquals(after,session.Snapshot),"Driver redo changed snapshot identity.");
            }
            await session.ExecuteAsync(new EditDocumentCommand("Visible offset result",s=>s with{Bodies=s.Bodies.ToImmutableDictionary(p=>p.Key,p=>p.Value with{IsVisible=p.Key==feature.OutputBodyId})}));
            var sheet=DrawingSheetTemplate.Create($"{join} {kind} offset");sheet=sheet with{Template=sheet.Template! with{Language=caption}};
            await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
            await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Top",DrawingViewKind.Top,new(150,60),.6));
            await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Front",DrawingViewKind.Front,new(150,115),.6));
            var source=session.Snapshot;var slot=ComponentSlotId.New();var rootPath=new OccurrencePath(source.Id,[]);
            await session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(rootPath,sketch.PartId,slot,"Independent",RigidTransform3d.Translate(80,0,0)));
            var clone=AssemblyOccurrenceCommands.MakePartIndependent(rootPath.Append(slot));await session.ExecuteAsync(clone);
            var copied=session.Snapshot.Sketches.Values.Single(s=>s.PartId==clone.ResultPartId&&s.Name==sketch.Name);
            var copiedFeature=session.Snapshot.Features.Values.Single(f=>f.PartId==clone.ResultPartId&&f.Name==feature.Name&&f.SketchSource?.SketchId==copied.Id);
            Require(copied.Id!=sketch.Id&&copied.Arcs.SequenceEqual(sketch.Arcs)&&copied.Lines.SequenceEqual(sketch.Lines),"Independent graph did not preserve exact author-local IDs.");
            foreach(var view in session.Snapshot.DrawingSheets[sheet.Id].Views)
            {
                Require(view.Sources.SequenceEqual(source.DrawingSheets[sheet.Id].Views.Single(v=>v.Id==view.Id).Sources),"Inserting a copy silently enlarged drawing scope.");
                await session.ExecuteAsync(TechnicalDrawingCommands.ReselectView(sheet.Id,view.Id,null));
            }
            var beforeCopyDriver=session.Snapshot;
            if(radiusConstraint is {} constraint)
            {
                await session.ExecuteAsync(new SetParametersCommand(Drive(beforeCopyDriver,copied.Id,constraint,1)));
                Verify(session.Snapshot.Features[copiedFeature.Id].Result,assets,3*(Rectangle(50,40,join)-58*Math.PI),2);
                Require(ReferenceEquals(sketch,session.Snapshot.Sketches[sketch.Id]),"Copy driver modified original authors.");
                Require(session.Snapshot.Features[feature.Id].Result==feature.Result,"Copy driver rebuilt the original result.");
            }
            else Verify(copiedFeature.Result,assets,expected,1);
            var accepted=session.Snapshot;
            foreach(var view in accepted.DrawingSheets[sheet.Id].Views)
            {
                Require(view.StaleReason is null&&view.Strokes.Length>0&&view.Sources.Any(s=>s.Feature==feature.Id&&s.Revision==accepted.Features[feature.Id].Result.Revision)&&
                    view.Sources.Any(s=>s.Feature==copiedFeature.Id&&s.Revision==accepted.Features[copiedFeature.Id].Result.Revision),"Independent drawing sources are missing or stale.");
                var points=view.Strokes.SelectMany(s=>s.Points).ToArray();
                double halfWidth=(points.Max(p=>p.X)-points.Min(p=>p.X))*view.Scale/2,
                    halfHeight=(points.Max(p=>p.Y)-points.Min(p=>p.Y))*view.Scale/2;
                Require(view.CenterMm.X-halfWidth>=25&&view.CenterMm.X+halfWidth<=282&&
                    view.CenterMm.Y-halfHeight>=15&&view.CenterMm.Y+halfHeight+4<=130,
                    "Verification view or caption overlaps the frame, projection symbol or title block.");
            }
            string pdf=Path.Combine(output,name+".pdf"),file=Path.Combine(output,name+".cadoryx");TechnicalDrawingPdf.Write(accepted,pdf);await session.SaveAsync(storage,file);
            using(var reopened=await storage.LoadAsync(file,assets))
            {
                reopened.Snapshot.Validate();Require(reopened.Snapshot.Sketches[copied.Id].Arcs.SequenceEqual(copied.Arcs),"Reopen changed exact copy arcs.");
                Require(reopened.Snapshot.DrawingSheets[sheet.Id].Views.All(v=>v.StaleReason is null&&v.Strokes.Length>0),"Reopened drawing is stale.");
                Verify(reopened.Snapshot.Features[feature.Id].Result,assets,expected,kind=="Region"?2:1);
            }
            acceptedRow=new{name,outcome="PASS",input,inputSha256=Hash(input),join=join.ToString(),language,kind,sketchId=sketch.Id.ToString(),copiedSketchId=copied.Id.ToString(),
                featureId=feature.Id.ToString(),copiedFeatureId=copiedFeature.Id.ToString(),expectedVolume=expected,pdf,pdfSha256=Hash(pdf),file,fileSha256=Hash(file),relativeTolerance=1e-10};
        }
        Require(assets.Count==0,"Consumer retained assets.");rows.Add(acceptedRow!);Progress(name+" PASS; zero assets");
    }
    catch(Exception error){failures++;rows.Add(new{name,outcome="FAIL",error=error.ToString(),assets=assets.Count});Progress(name+" FAIL");}
}
var native=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(m=>string.Equals(Path.GetDirectoryName(m.FileName),Path.Combine(AppContext.BaseDirectory,"occt"),StringComparison.OrdinalIgnoreCase))
    .Select(m=>new{name=m.ModuleName,sha256=Hash(m.FileName)}).OrderBy(m=>m.name).ToArray();
Require(native.Length==62,"Expected 62 actually loaded native modules.");foreach(var r in native)Require(r.sha256==Hash(Path.Combine(frozen,"occt",r.name)),"Frozen native mismatch.");
Require(rows.Count==18,"Every declared workflow must produce exactly one result.");
File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{recordedAt=DateTimeOffset.UtcNow,status=failures==0?"PASS":"FAIL",failures,
    scope="Production window authors, unchanged 1e-10 native volume, result drivers, explicit drawing source reselection, independent author graph, PDF and native reopen. This does not discharge the translated native foundation failures.",
    applicationPayload=application,sdkPayload=sdk,nativePayload=native,rows},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"{(failures==0?"PASS":"FAIL")}: {18-failures}/18 actual window workflows; 18 vector PDFs expected; 8 application/13 SDK/62 native modules bound.");return failures==0?0:1;

void Verify(GeometryAssetRef geometry,IAssetStore assets,double expected,int solids)
{
    using var shape=OcctGeometryBridge.ReadShape(geometry,assets);Require(shape.IsValid&&shape.GetTopologySummary().UniqueCounts.SolidCount==solids,"Invalid native material topology.");
    Near(expected,Math.Abs(shape.InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-10).Properties.Mass),"Independent adaptive volume");Near(expected,geometry.VolumeMm3,"Stored native volume");
}
ParameterBook Drive(DocumentSnapshot document,SketchId sketch,SketchConstraintId constraint,double value)
{
    var target=ParameterTarget.Sketch(sketch,constraint);var p=new CadParameter(Guid.NewGuid(),"offset_driver_"+Guid.NewGuid().ToString("N"),ParameterDimension.Length,value.ToString("R",CultureInfo.InvariantCulture)+" mm");
    return new(document.Parameters.Parameters.Add(p),document.Parameters.Bindings.Where(b=>b.Target!=target).ToImmutableArray().Add(new(Guid.NewGuid(),p.Id,target)));
}
