using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Rendering;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Controls;
using Cadoryx.wpf.Views;
using Microsoft.Extensions.DependencyInjection;
using static Cadoryx.wpf.Diagnostics.SketchEditorSmokeRunner;

namespace Cadoryx.wpf.Diagnostics;

// This opt-in diagnostic exercises the production editors. It does not replace
// desktop hit testing, physical input or the independent native test matrix.
internal static class SketchExpressionCurvedConsumerSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output,string family)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));using var listener=new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        var main=(MainWindowViewModel)window.DataContext;int culture=main.CurrentCultureLCID;
        var solver=services.GetRequiredService<ISketchConstraintSolver>();var storage=services.GetRequiredService<IDocumentStorage>();
        var assets=services.GetRequiredService<IAssetStore>();var records=new List<object>();
        try
        {
            Check(family is "tube" or "circle" or "bezier" or "spline","Explicit diagnostic family required");
            await Idle();
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})foreach(string mode in family=="tube"?new[]{"bezier-tube","spline-tube"}:new[]{"sweep","loft"})
            {
                main.ChangeCultureCommand.Execute(CultureInfo.GetCultureInfo(language).LCID.ToString(CultureInfo.InvariantCulture));await Idle();
                if(main.Documents.Count==0){main.NewCommand.Execute(null);await Idle();}
                var document=main.ActiveDocument!;var session=document.Session;
                string prefix=family+"-"+mode+"-"+language;Progress(output,prefix+" authors");
                await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(.02,.02,.02,RigidTransform3d.Identity),"Support"));
                var part=session.Snapshot.Features.Values.Single().PartId;
                bool reverse=language=="ja-JP",tilted=language=="zh-CN";
                var world=tilted?new RigidTransform3d(new(11,-7,3),Quaterniond.FromEulerDegrees(23,41,17)):RigidTransform3d.Identity;
                Author first,last;Author? path=null;double expected,editedExpected,drivenOffset;
                if(family=="tube")
                {
                    path=Polynomial(part,mode=="spline-tube",1,world,.03,reverse);
                    var p=Curve(path.Draft.Value,path.Expression).Geometry.Evaluate(0);
                    var tangent=new Vector3d(p.First.X,p.First.Y,0).Normalized();
                    var rotation=Quaterniond.FromAxisAngle(Vector3d.UnitZ.Cross(tangent),Math.Acos(Vector3d.UnitZ.Dot(tangent)));
                    first=Circle(part,.03,world*new RigidTransform3d(new(p.Point.X,p.Point.Y,0),rotation),.01,false);
                    last=first;drivenOffset=.015;
                    double length=Math.Sqrt(5)/2+Math.Asinh(2)/4-2*.03*Math.Atan(2);
                    expected=Math.PI*.04*.04*length;editedExpected=Math.PI*.045*.045*length;
                }
                else if(mode=="sweep")
                {
                    path=Circle(part,12,world,.1,reverse,true);var p=Curve(path.Draft.Value,path.Expression).Geometry.At(0);
                    var rotation=reverse?Quaterniond.FromAxisAngle(new(1,1,1),Math.Tau/3):Quaterniond.FromEulerDegrees(-90,0,0);
                    first=Section(part,family,1,world*new RigidTransform3d(new(p.X,p.Y,0),rotation),.03,reverse);
                    last=first;drivenOffset=.04;expected=Area(family,1,.03)*12.1*Math.PI/2;editedExpected=Area(family,1,.04)*12.1*Math.PI/2;
                }
                else
                {
                    const double scale=.8;first=Section(part,family,1,world,.03,reverse);
                    last=Section(part,family,scale,world*RigidTransform3d.Translate(tilted?1:0,tilted?.4:0,5),.03*scale,reverse);
                    drivenOffset=.04;expected=5*Area(family,1,.03)*(1+scale+scale*scale)/3;
                    editedExpected=LoftVolume(family,scale,.04,.03*scale);
                }
                await session.ExecuteAsync(new UpsertSketchCommand(first.Draft.Value,solver));
                if(last!=first)await session.ExecuteAsync(new UpsertSketchCommand(last.Draft.Value,solver));
                if(path is not null)await session.ExecuteAsync(new UpsertSketchCommand(path.Draft.Value,solver));
                var baseline=session.Snapshot;document.SelectedTargetPart=part;
                bool loft=mode=="loft";
                async Task Configure(GuidedEditorWindow editor)
                {
                    Check(editor.Editor.Profiles.All(p=>p.Section.Source is null||p.Section.Source.Revision==session.Snapshot.Sketches[p.Section.Source.SketchId].Revision),"Stale author candidate");
                    if(loft)
                    {
                        foreach(var author in new[]{first,last})
                        {
                            await ClickButton(editor.AddStationButton);
                            editor.AssignmentProfilePicker.SelectedItem=editor.Editor.Profiles.Single(p=>p.Section.Source?.SketchId==author.Draft.Value.Id&&p.Section.Source.MixedBoundaryIds.SequenceEqual(new[]{author.Expression}));
                            await ClickButton(editor.AssignProfileButton);
                        }
                        await ClickButton(editor.RuledCheckBox);Check(editor.Editor.Ruled,"Actual ruled toggle");
                    }
                    else
                    {
                        Check(editor.Editor.SelectedSweepProfile is null&&editor.Editor.SelectedPathSketch is null,"Explicit section and path choices required");
                        editor.SectionPicker.SelectedItem=editor.Editor.Profiles.Single(p=>p.Section.Source?.SketchId==first.Draft.Value.Id&&p.Section.Source.MixedBoundaryIds.SequenceEqual(new[]{first.Expression}));
                        editor.PathSketchPicker.SelectedItem=editor.Editor.PathSketches.Single(p=>p.Value==path!.Draft.Value.Id);
                        editor.AvailableEdgePicker.SelectedItem=editor.Editor.AvailableEdges.Single(p=>p.Entity==path!.Expression);await ClickButton(editor.AddEdgeButton);
                        Check(editor.Editor.PathEdges.Single().Entity==path!.Expression&&!editor.Editor.PathEdges.Single().Reverse,"Declared path identity/direction");
                    }
                    await Preview(editor.PreviewButton,()=>editor.Editor.PreviewCommand.ExecutionTask);
                    Check(editor.Editor.CanConfirm,editor.Editor.Status);PreviewVolume(document,expected);
                    Check(ReferenceEquals(baseline,session.Snapshot),"Guided preview published a partial result");
                }
                Func<Task> open=()=>loft?main.LoftCommand.ExecuteAsync(null):main.SweepCommand.ExecuteAsync(null);
                Progress(output,prefix+" preview/cancel");
                await Operate<GuidedEditorWindow>(open,async editor=>
                {
                    await Configure(editor);
                    if(loft)editor.AssignmentProfilePicker.BringIntoView();else editor.SectionPicker.BringIntoView();
                    await Capture(editor,Path.Combine(output,prefix+"-guided.png"));
                    if(!loft)
                    {
                        editor.AvailableEdgePicker.BringIntoView();await Capture(editor,Path.Combine(output,prefix+"-path.png"));
                        editor.PathEdgesList.UpdateLayout();var row=(ListBoxItem)editor.PathEdgesList.ItemContainerGenerator.ContainerFromItem(editor.Editor.PathEdges.Single());
                        var toggle=Descendants(row).OfType<CheckBox>().Single();await ClickButton(toggle);
                        Check(!editor.Editor.CanConfirm,"Direction change kept an old confirmable preview");
                        await Preview(editor.PreviewButton,()=>editor.Editor.PreviewCommand.ExecutionTask);Check(!editor.Editor.CanConfirm,"Wrong start pose must reject");
                        await ClickButton(toggle);await Preview(editor.PreviewButton,()=>editor.Editor.PreviewCommand.ExecutionTask);Check(editor.Editor.CanConfirm,editor.Editor.Status);
                    }
                    await ClickButton(editor.CancelButton);
                });
                Check(ReferenceEquals(baseline,session.Snapshot)&&document.PreviewScene is null,"Guided cancel altered baseline");
                await Operate<GuidedEditorWindow>(open,async editor=>{await Configure(editor);await Confirm(editor.ConfirmButton,()=>editor.Editor.ConfirmCommand.ExecutionTask);});
                var accepted=session.Snapshot;var feature=accepted.Features.Values.Single(f=>!baseline.Features.ContainsKey(f.Id));
                Verify(assets,feature.Result,expected);CheckSources(accepted,feature,first,last,path);CheckReport(feature);
                await session.UndoAsync();Check(ReferenceEquals(baseline,session.Snapshot),"Guided confirm requires one undo");
                await session.RedoAsync();Check(ReferenceEquals(accepted,session.Snapshot),"Guided redo changed result");
                var target=ParameterTarget.SketchCurve(first.Draft.Value.Id,first.Expression,"OffsetDistance",0);
                async Task Parameter(ParameterEditorWindow editor)
                {
                    editor.ParameterNameInput.Text="section_offset";editor.ExpressionInput.Text=drivenOffset.ToString("R",CultureInfo.InvariantCulture)+" mm";
                    await ClickButton(editor.ApplyParameterButton);
                    editor.DrivingPicker.SelectedItem=editor.Editor.Targets.Single(t=>t.Target==target);await ClickButton(editor.BindDimensionButton);
                    Check(editor.Editor.Bindings.Count==1,"Actual parameter target binding");
                    await Preview(editor.PreviewButton,()=>editor.Editor.PreviewCommand.ExecutionTask);Check(editor.Editor.CanConfirm,editor.Editor.Status);
                    PreviewVolume(document,editedExpected);Check(ReferenceEquals(accepted,session.Snapshot),"C1 preview mutated accepted document");
                }
                Progress(output,prefix+" parameter/cancel/confirm");
                await Operate<ParameterEditorWindow>(()=>main.EditParametersCommand.ExecuteAsync(null),async editor=>
                {
                    await Parameter(editor);await Capture(editor,Path.Combine(output,prefix+"-parameter.png"));
                    editor.ExpressionInput.Text="0.05 mm";await Idle();Check(!editor.Editor.CanConfirm,"Pending text kept old preview confirmable");
                    editor.Close();
                });
                Check(ReferenceEquals(accepted,session.Snapshot)&&document.PreviewScene is null,"C1 close/cancel mutated accepted document");
                await Operate<ParameterEditorWindow>(()=>main.EditParametersCommand.ExecuteAsync(null),async editor=>{await Parameter(editor);await Confirm(editor.ConfirmButton,()=>editor.Editor.ConfirmCommand.ExecutionTask);});
                var edited=session.Snapshot;var updated=edited.Features[feature.Id];Verify(assets,updated.Result,editedExpected);CheckSources(edited,updated,first,last,path);CheckReport(updated);
                foreach(var sketch in accepted.Sketches.Values)foreach(var curve in sketch.ExpressionCurves)
                    Check(curve.Endpoints==edited.Sketches[sketch.Id].ExpressionCurves.Single(c=>c.Id==curve.Id).Endpoints,"C1 replaced generated endpoint identity");
                await session.UndoAsync();Check(ReferenceEquals(accepted,session.Snapshot),"C1 requires one undo");await session.RedoAsync();Check(ReferenceEquals(edited,session.Snapshot),"C1 redo changed result");
                string file=Path.Combine(output,prefix+".cadoryx");await session.SaveAsync(storage,file);
                Check(await main.CloseAllAsync(),"Close before actual production reopen");await main.OpenPathAsync(file);await Idle();
                var reopened=main.ActiveDocument??throw new InvalidOperationException("No active reopened document");var loaded=reopened.Session.Snapshot;loaded.Validate();
                Check(JsonSerializer.Serialize(edited.Parameters,CadJson.Options)==JsonSerializer.Serialize(loaded.Parameters,CadJson.Options),"Reopen parameter authors/targets");
                foreach(var sketch in edited.Sketches.Values)
                {
                    var actual=loaded.Sketches[sketch.Id];Check(SketchExpressionEntities.SameTable(sketch,actual),"Reopen complete expression author table");
                    Check(sketch.Points.SequenceEqual(actual.Points)&&sketch.Constraints.SequenceEqual(actual.Constraints),"Reopen point and constraint identities");
                }
                CheckReport(loaded.Features[feature.Id]);
                Check(JsonSerializer.Serialize(updated.GuidedModelingReport,GuidedModelingReport.JsonOptions)==JsonSerializer.Serialize(loaded.Features[feature.Id].GuidedModelingReport,GuidedModelingReport.JsonOptions),"Reopen complete modeling observations");
                Check(updated.Result==loaded.Features[feature.Id].Result,"Reopen native result fingerprint");CheckSources(loaded,loaded.Features[feature.Id],first,last,path);Verify(assets,loaded.Features[feature.Id].Result,editedExpected);
                var viewport=Descendants(window).OfType<OcctViewportHost>().First(h=>h.Viewport is not null).Viewport!;
                viewport.SetProjection(CadProjection.Axonometric);viewport.FitAll();await Idle();viewport.SaveScreenshot(Path.Combine(output,prefix+"-native.png"));
                records.Add(new{family,mode,language,reverse,tilted,expectedVolume=expected,editedExpectedVolume=editedExpected,actualVolume=loaded.Features[feature.Id].Result.VolumeMm3,
                    featureId=feature.Id.ToString(),firstSketch=first.Draft.Value.Id.ToString(),firstExpression=first.Expression.ToString(),lastSketch=last.Draft.Value.Id.ToString(),lastExpression=last.Expression.ToString(),
                    pathSketch=path?.Draft.Value.Id.ToString(),pathExpression=path?.Expression.ToString(),file,
                    modelingObservationStages=loaded.Features[feature.Id].GuidedModelingReport!.Stages.Length,
                    modelingRecipeSha256=loaded.Features[feature.Id].GuidedModelingReport!.BuiltRecipeSha256,
                    modelingSdk=loaded.Features[feature.Id].GuidedModelingReport!.SdkVersion});
                Check(await main.CloseAllAsync(),"Close reopened document");Progress(output,prefix+" PASS");
            }
            await ((App)Application.Current).StopRecoveryAsync();await Idle();Check(((IAssetStoreStatistics)assets).Count==0,"Zero final assets");
            listener.Flush();bindings.Flush();Check(new FileInfo(Path.Combine(output,"bindings.log")).Length==0,"WPF binding errors");
            await File.WriteAllTextAsync(Path.Combine(output,"scenes.json"),JsonSerializer.Serialize(records,new JsonSerializerOptions{WriteIndented=true}));
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+"Six current SDK22 curved consumers in en-US/zh-CN/ja-JP; explicit production section/path/station choices, wrong directed-start refusal, independent native volume/validity/self-interference/single solid, preview/confirm/cancel, C1 target Apply/Bind/Preview/Confirm with pending-text guard, source revisions and endpoint/channel identities, one undo/redo per command, actual save/close/production reopen, empty bindings and zero assets. Physical input, late-result races and exchange/history are separate.");
            window.CloseAfterSmoke();
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+ex);Application.Current.Shutdown(1);}
        finally{main.ChangeCultureCommand.Execute(culture.ToString(CultureInfo.InvariantCulture));PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    internal static async Task RunResultsAsync(MainWindow window,IServiceProvider services,string output,string evidenceRoot)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);evidenceRoot=Path.GetFullPath(evidenceRoot);
        using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));using var listener=new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        var main=(MainWindowViewModel)window.DataContext;int original=main.CurrentCultureLCID;var records=new List<object>();
        try
        {
            await Idle();
            var initial=main.ActiveDocument!;Check(initial.Session.Snapshot.Features.Count==0&&initial.Session.Snapshot.Sketches.Count==0,"Initial diagnostic document must be empty");
            await initial.Session.SaveAsync(services.GetRequiredService<IDocumentStorage>(),Path.Combine(output,"initial-empty.cadoryx"));
            Check(await main.CloseAllAsync(),"Close saved initial empty document");
            foreach(string family in new[]{"tube","circle","bezier","spline"})
            {
                string folder=Path.Combine(evidenceRoot,$"b18-curved-{family}-window-2");
                Check((await File.ReadAllTextAsync(Path.Combine(folder,"result.txt"))).StartsWith("PASS:",StringComparison.Ordinal),"Accepted workflow prerequisite");
                using var scenes=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder,"scenes.json")));
                Check(scenes.RootElement.GetArrayLength()==6,"Six accepted family scenes");
                foreach(var scene in scenes.RootElement.EnumerateArray())
                {
                    string language=scene.GetProperty("language").GetString()!;Check(language is "en-US" or "zh-CN" or "ja-JP","Declared language");
                    string mode=scene.GetProperty("mode").GetString()!;Check(mode is "sweep" or "loft" or "bezier-tube" or "spline-tube","Declared mode");
                    string prefix=family+"-"+mode+"-"+language,file=Path.Combine(folder,prefix+".cadoryx");
                    Check(Path.GetFullPath(scene.GetProperty("file").GetString()!)==Path.GetFullPath(file),"Declared owned archive path");
                    main.ChangeCultureCommand.Execute(CultureInfo.GetCultureInfo(language).LCID.ToString(CultureInfo.InvariantCulture));await Idle();
                    await main.OpenPathAsync(file);await Idle();var document=main.ActiveDocument!;var before=document.Session.Snapshot;
                    var feature=before.Features[new FeatureId(Guid.Parse(scene.GetProperty("featureId").GetString()!))];
                    Verify(document.Session.Assets,feature.Result,scene.GetProperty("editedExpectedVolume").GetDouble());
                    var item=document.Scene.Items.Single(i=>i.BodyId==feature.OutputBodyId);
                    var viewport=Descendants(window).OfType<OcctViewportHost>().First(h=>h.Viewport is not null).Viewport!;
                    viewport.SetProjection(CadProjection.Axonometric);await Idle();
                    document.Selection.Replace([new SelectionTarget(item.Path,item.BodyId,item.Geometry.Revision)]);document.Review.ActivePane=0;await Idle();
                    var target=viewport.CaptureFocusTarget(SceneEnvelope.Measure([item])!.Value);
                    var ribbon=Descendants(window).OfType<MainRibbonView>().Single();
                    Check(ribbon.FocusButton.Command==document.Review.FocusCommand,"Production Focus button binding");await ClickButton(ribbon.FocusButton);
                    await Task.Delay(400);await Idle();var camera=viewport.CaptureCamera();
                    Check(Math.Abs(camera.Scale-target.Scale)<=1e-7&&(camera.Eye-target.Eye).Length<=1e-7,"Focus animation reached selected result");
                    document.Selection.Replace([]);await Idle();viewport.SaveScreenshot(Path.Combine(output,prefix+"-focused.png"));
                    Check(ReferenceEquals(before,document.Session.Snapshot),"Focus changed document authors");
                    records.Add(new{family,mode,language,feature=feature.Id.ToString(),body=feature.OutputBodyId.ToString(),geometry=feature.Result.AssetId.ToString(),file,scale=camera.Scale});
                    Check(await main.CloseAllAsync(),"Close focused result");Progress(output,prefix+" PASS");
                }
            }
            await ((App)Application.Current).StopRecoveryAsync();await Idle();Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Zero focused-result assets");
            listener.Flush();bindings.Flush();Check(new FileInfo(Path.Combine(output,"bindings.log")).Length==0,"Focused-result WPF bindings");
            await File.WriteAllTextAsync(Path.Combine(output,"scenes.json"),JsonSerializer.Serialize(records,new JsonSerializerOptions{WriteIndented=true}));
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+"24 accepted archives reopened through production OpenPath; explicit feature-output body/revision selection and real Ribbon Focus Invoke, camera target/animation checked, cleared selection, full native result screenshots, source documents unchanged, empty bindings and zero assets. Original FitAll images retained; this supplements result visibility, not physical input or new authoring workflows.");window.CloseAfterSmoke();
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+ex);Application.Current.Shutdown(1);}
        finally{main.ChangeCultureCommand.Execute(original.ToString(CultureInfo.InvariantCulture));PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private sealed record Author(SketchDraft Draft,SketchEntityId Expression);
    private static Author Circle(DefinitionId part,double radius,RigidTransform3d pose,double offset,bool reverse,bool quarter=false)
    {
        var draft=new SketchDraft(CadSketch.Create(part,"Circle author",pose));draft.AddCircle(new(0,0),radius,construction:true);var id=draft.AddExactOffset(draft.Value.Circles[0].Id,offset);
        if(quarter)draft.EditExpression(id,Curve(draft.Value,id).Geometry.Trim(0,.25));if(reverse)draft.EditExpression(id,Curve(draft.Value,id).Geometry.Reverse());
        draft.SetConstruction([id],false);draft.Change(FixAuthors);return new(draft,id);
    }
    private static Author Polynomial(DefinitionId part,bool spline,double scale,RigidTransform3d pose,double offset,bool reverse)
    {
        var draft=new SketchDraft(CadSketch.Create(part,"Polynomial author",pose));
        if(spline)draft.AddSpline([new(-.5*scale,0),new(-scale/6,2*scale/3),new(scale/6,2*scale/3),new(.5*scale,0)],construction:true);
        else draft.AddBezier(new(-.5*scale,0),new(0,scale),new(.5*scale,0),construction:true);
        var id=draft.AddExactOffset(spline?draft.Value.Splines[0].Id:draft.Value.Beziers[0].Id,offset);
        if(reverse)draft.EditExpression(id,Curve(draft.Value,id).Geometry.Reverse());draft.SetConstruction([id],false);draft.Change(FixAuthors);return new(draft,id);
    }
    private static Author Section(DefinitionId part,string family,double scale,RigidTransform3d pose,double offset,bool reverse)=>family=="circle"?Circle(part,.5*scale,pose,offset,reverse):Polynomial(part,family=="spline",scale,pose,offset,reverse);
    private static SketchExpressionCurve Curve(CadSketch s,SketchEntityId id)=>s.ExpressionCurves.Single(c=>c.Id==id);
    private static CadSketch FixAuthors(CadSketch s)
    {
        var generated=s.ExpressionCurves.SelectMany(c=>new[]{c.Endpoints!.Start,c.Endpoints.End}).ToHashSet();
        return s with{Constraints=[..s.Constraints,..s.Points.Where(p=>!generated.Contains(p.Id)).Select(p=>(SketchConstraint)new FixPointConstraint(SketchConstraintId.New(),p.Id,p.Position))]};
    }
    private static double L=>3/(2*Math.Sqrt(5))+Math.Asinh(2)/4;
    private static double Q=>Math.Atan(2)-.4;
    private static double Area(string family,double scale,double d)=>family=="circle"?Math.PI*Math.Pow(.5*scale+d,2):scale*scale/3-scale*d*L+d*d*Q;
    private static double Pair(double a,double b,double c,double d)=>(2*a*c+a*d+b*c+2*b*d)/6;
    private static double LoftVolume(string family,double scale,double d0,double d1)=>family=="circle"?
        5*Math.PI*(Math.Pow(.5+d0,2)+(.5+d0)*(.5*scale+d1)+Math.Pow(.5*scale+d1,2))/3:
        5*(Pair(1,scale,1,scale)/3-L*Pair(1,scale,d0,d1)+Q*Pair(d0,d1,d0,d1));
    private static void CheckSources(DocumentSnapshot doc,FeatureDefinition feature,Author first,Author last,Author? path)
    {
        if(feature.Recipe is SweepRecipe sweep)
        {
            Check(sweep.Section.Source?.SketchId==first.Draft.Value.Id&&sweep.Section.Source.Revision==doc.Sketches[first.Draft.Value.Id].Revision&&sweep.Section.Source.MixedBoundaryIds.SequenceEqual(new[]{first.Expression}),"Sweep section source identity/revision");
            Check(sweep.Path.Source?.Sketch==path!.Draft.Value.Id&&sweep.Path.Source.Revision==doc.Sketches[path.Draft.Value.Id].Revision&&sweep.Path.Source.Edges.SequenceEqual(new[]{new SketchPathEdge(path.Expression)}),"Sweep path source identity/revision/direction");
        }
        else
        {
            var loft=(LoftRecipe)feature.Recipe;Check(loft.Ruled&&loft.Channels.Length==1,"Actual ruled one-channel loft");
            Check(!SketchProfileReference.SameProfile(loft.Channels[0].Profiles[0],loft.Channels[0].Profiles[1]),"Must exercise unequal native loft");
            foreach(var(source,author)in loft.Channels[0].Sources.Zip(new[]{first,last}))
                Check(source?.SketchId==author.Draft.Value.Id&&source.Revision==doc.Sketches[author.Draft.Value.Id].Revision&&source.MixedBoundaryIds.SequenceEqual(new[]{author.Expression}),"Loft source channel order/identity/revision");
        }
    }
    private static void CheckReport(FeatureDefinition feature)
    {
        var report=feature.GuidedModelingReport??throw new InvalidOperationException("Actual guided build has no modeling observations");
        report.ValidateFor(feature.Result);
        Check(report.BuiltRecipeSha256==GuidedModelingReport.RecipeHash(feature.Recipe),"As-built/current author recipe");
        Check(feature.Result.Format?.WriterVersion is {} writer&&report.SdkVersion.Contains(writer,StringComparison.Ordinal),"Actual SDK writer provenance");
        Check(feature.TopologyHistory is null,"Modeling observations must not invent topology history");
        var preparations=report.Stages.Where(s=>s.Operation==GuidedModelingOperation.CurvePreparation).ToArray();
        Check(preparations.Length>0&&preparations.All(s=>s.RequestedTolerance==1e-10&&s.MaximumSegments==64&&s.MaximumDegree==11&&
            s.NativeApproximationErrorEstimate is >=0 and <=1e-10),"Actual bounded native preparation observations");
        if(feature.Recipe is LoftRecipe)Check(report.Stages.Where(s=>s.Operation==GuidedModelingOperation.Loft).All(s=>s.NativeApproximationErrorEstimate is null),"No invented loft estimate");
    }
    private static void Verify(IAssetStore assets,GeometryAssetRef geometry,double expected)
    {
        using var shape=OcctGeometryBridge.ReadShape(geometry,assets);
        Check(geometry.Kind==BodyKind.Solid&&shape.GetTopologySummary().UniqueCounts.SolidCount==1,"Single native solid");
        Check(shape.GetValidationReport(true,true).IsValid&&shape.GetSelfInterferenceReport().IsClear,"Native validity/self-interference");
        Check(Math.Abs(geometry.VolumeMm3-expected)<=1e-7,$"Independent volume: expected={expected:R};actual={geometry.VolumeMm3:R}");
    }
    private static void PreviewVolume(CadDocumentViewModel doc,double expected)=>Check(doc.PreviewScene!.Items.Any(i=>Math.Abs(i.Geometry.VolumeMm3-expected)<=1e-7),"Independent preview volume");
    private static async Task Preview(Button button,Func<Task?> task){await ClickButton(button);await(task()??throw new InvalidOperationException("No preview task"));await Idle();}
    private static async Task Confirm(Button button,Func<Task?> task){await ClickButton(button);await(task()??throw new InvalidOperationException("No confirm task"));await Idle();}
    private static async Task Capture(FrameworkElement element,string path){await Idle();await Task.Delay(350);await Idle();Save(element,path);}
    private static void Progress(string output,string step)=>File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {step}{Environment.NewLine}");
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}}
    private static async Task Operate<T>(Func<Task> open,Func<T,Task> action)where T:Window
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _=Application.Current.Dispatcher.InvokeAsync(async()=>{T? editor=null;try{await Idle();editor=Application.Current.Windows.OfType<T>().Single();await action(editor);done.TrySetResult();}catch(Exception ex){done.TrySetException(ex);}finally{if(editor is {IsVisible:true})editor.Close();}},DispatcherPriority.ApplicationIdle);
        await open();await done.Task;
    }
}
