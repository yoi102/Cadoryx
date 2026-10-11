using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Views;
using Cadoryx.wpf.Views.Toolboxes;
using MahApps.Metro.Controls;
using Microsoft.Extensions.DependencyInjection;
using static Cadoryx.wpf.Diagnostics.SketchEditorSmokeRunner;

namespace Cadoryx.wpf.Diagnostics;

internal static class SketchExpressionConsumerSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));using var listener=new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        var main=(MainWindowViewModel)window.DataContext;int original=main.CurrentCultureLCID;
        try
        {
            await Idle();var document=main.ActiveDocument!;var session=document.Session;var solver=services.GetRequiredService<ISketchConstraintSolver>();var storage=services.GetRequiredService<IDocumentStorage>();
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(1,1,1,RigidTransform3d.Translate(40,0,0)),"Support"));var part=session.Snapshot.Features.Values.Single().PartId;
            var sections=new List<(CadSketch Sketch,SketchEntityId Curve)>();
            foreach(double z in new[]{0d,5d})
            {
                var d=new SketchDraft(CadSketch.Create(part,"Exact section "+z,RigidTransform3d.Translate(.1,0,z)));d.AddBezier(new(7.5,0),new(8,1),new(8.5,0),construction:true);
                var sourcePoints=d.Value.Points.ToArray();var id=d.AddExactOffset(d.Value.Beziers[0].Id,.03);d.SetConstruction([id],false);
                d.Change(s=>s with{Constraints=[..s.Constraints,..sourcePoints.Select(p=>(SketchConstraint)new FixPointConstraint(SketchConstraintId.New(),p.Id,p.Position))]});
                await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));sections.Add((session.Snapshot.Sketches[d.Value.Id],id));
            }
            var path=new SketchDraft(CadSketch.Create(part,"Reversed exact path",new(Vector3d.Zero,Quaterniond.FromEulerDegrees(90,0,0))));path.AddLine(new(0,10),new(0,0),construction:true);
            var line=path.Value.Lines[0];var pathId=path.AddExactOffset(line.Id,-.1);path.SetConstruction([pathId],false);
            path.Change(s=>s with{Constraints=[new FixPointConstraint(SketchConstraintId.New(),line.End,new(0,0)),new VerticalConstraint(SketchConstraintId.New(),line.Id),new LengthConstraint(SketchConstraintId.New(),line.Id,10)]});
            await session.ExecuteAsync(new UpsertSketchCommand(path.Value,solver));var baseline=session.Snapshot;var section=sections[0];
            double area=Area(),revolve=RevolveVolume();
            async Task Accepted(string mode,string language)
            {
                var accepted=session.Snapshot;var feature=accepted.Features.Values.Single(f=>!baseline.Features.ContainsKey(f.Id));
                Check(mode switch
                {
                    "extrude"=>feature.Recipe is ExtrudeRecipe e&&e.Profile.BoundaryCurves[0].Expression is not null&&feature.SketchSource?.MixedBoundaryIds.SequenceEqual(new[]{section.Curve})==true,
                    "revolve"=>feature.Recipe is RevolveRecipe r&&r.Profile.BoundaryCurves[0].Expression is not null&&feature.SketchSource?.MixedBoundaryIds.SequenceEqual(new[]{section.Curve})==true,
                    "sweep"=>feature.Recipe is SweepRecipe s&&s.Section.Source?.MixedBoundaryIds.SequenceEqual(new[]{section.Curve})==true&&s.Path.Source?.Edges.SequenceEqual(new[]{new SketchPathEdge(pathId,true)})==true,
                    "loft"=>feature.Recipe is LoftRecipe l&&l.Channels.Single().Sources.Select(s=>s!.SketchId).SequenceEqual(sections.Select(s=>s.Sketch.Id)),
                    _=>false
                },mode+" accepted expression/source intent");
                var file=Path.Combine(output,$"accepted-{mode}-{language}.cadoryx");await session.SaveAsync(storage,file);
                using(var loaded=await storage.LoadAsync(file,session.Assets)){loaded.Snapshot.Validate();Check(loaded.Snapshot.Features[feature.Id].Result==feature.Result,"Stored native result");}
                await session.UndoAsync();Check(ReferenceEquals(baseline,session.Snapshot),"Single undo");await session.RedoAsync();Check(ReferenceEquals(accepted,session.Snapshot),"Exact redo");await session.UndoAsync();
            }
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})
            {
                main.ChangeCultureCommand.Execute(CultureInfo.GetCultureInfo(language).LCID.ToString(CultureInfo.InvariantCulture));await Idle();document.SelectedTargetPart=part;
                Progress(output,language+" extrude");document.StartTool("Extrude");
                await Operate<ExtrusionEditorWindow>(()=>main.EditExtrusionsCommand.ExecuteAsync(null),async host=>
                {
                    host.ProfilePicker.SelectedItem=host.Editor.Profiles.Single(p=>p.Source?.SketchId==section.Sketch.Id&&p.Source.MixedBoundaryIds.SequenceEqual(new[]{section.Curve}));host.DistanceInput.Value=3;
                    await Preview(host.PreviewButton,()=>host.Editor.PreviewCommand.ExecutionTask);Check(host.Editor.CanConfirm,host.Editor.Status);Volume(document,3*area);host.ProfilePicker.BringIntoView();await Idle();Save(host,Path.Combine(output,$"extrude-{language}.png"));
                    await ClickButton(host.CancelButton);
                });Check(ReferenceEquals(baseline,session.Snapshot)&&document.PreviewScene is null,"Extrude cancel");
                await Operate<ExtrusionEditorWindow>(()=>main.EditExtrusionsCommand.ExecuteAsync(null),async host=>
                {
                    host.ProfilePicker.SelectedItem=host.Editor.Profiles.Single(p=>p.Source?.SketchId==section.Sketch.Id&&p.Source.MixedBoundaryIds.SequenceEqual(new[]{section.Curve}));host.DistanceInput.Value=3;
                    await Preview(host.PreviewButton,()=>host.Editor.PreviewCommand.ExecutionTask);Check(host.Editor.CanConfirm,host.Editor.Status);await Confirm(host.ConfirmButton,()=>host.Editor.ConfirmCommand.ExecutionTask);
                });await Accepted("extrude",language);
                Progress(output,language+" revolve");document.SelectedSketchId=section.Sketch.Id;document.StartSketchFeature("Revolve");
                var view=new ModelingToolboxView{DataContext=new{Document=document}};var model=new MetroWindow{Title=Cadoryx.Lang.Strings.Strings.PolygonRevolve,Width=760,Height=Math.Min(940,SystemParameters.WorkArea.Height-32),Content=view,Owner=window,WindowStartupLocation=WindowStartupLocation.CenterOwner};
                try
                {
                    model.Show();model.Activate();await Idle();view.SketchProfileInput.SelectedItem=document.SketchProfiles.Single(p=>p.Source.SketchId==section.Sketch.Id&&p.Source.MixedBoundaryIds.SequenceEqual(new[]{section.Curve}));view.RevolveAngleInput.Value=360;
                    await Preview(view.ModelingPreviewButton,()=>document.PreviewCommand.ExecutionTask);Check(document.HasPreview,document.ToolStatus);Volume(document,revolve);view.SketchProfileInput.BringIntoView();await Idle();Save(model,Path.Combine(output,$"revolve-{language}.png"));
                    view.ModelingConfirmButton.BringIntoView();await Idle();Save(model,Path.Combine(output,$"revolve-actions-{language}.png"));
                    await ClickButton(view.ModelingCancelButton);Check(ReferenceEquals(baseline,session.Snapshot)&&document.PreviewScene is null,"Revolve cancel");
                    await Preview(view.ModelingPreviewButton,()=>document.PreviewCommand.ExecutionTask);Check(document.HasPreview,document.ToolStatus);await Confirm(view.ModelingConfirmButton,()=>document.ConfirmCommand.ExecutionTask);await Accepted("revolve",language);
                }
                finally{document.CancelCommand.Execute(null);model.Close();}
                Progress(output,language+" sweep");
                async Task ConfigureSweep(GuidedEditorWindow host)
                {
                    Check(host.Editor.SelectedSweepProfile is null&&host.Editor.SelectedPathSketch is null,"Explicit author selection required");
                    host.SectionPicker.SelectedItem=host.Editor.Profiles.Single(p=>p.Label!=Cadoryx.Lang.Strings.Strings.GuidedCurrentSection&&p.Section.Source?.SketchId==section.Sketch.Id&&p.Section.Source.MixedBoundaryIds.SequenceEqual(new[]{section.Curve}));
                    host.PathSketchPicker.SelectedItem=host.Editor.PathSketches.Single(p=>p.Value==path.Value.Id);host.AvailableEdgePicker.SelectedItem=host.Editor.AvailableEdges.Single(p=>p.Entity==pathId);await ClickButton(host.AddEdgeButton);
                    await Preview(host.PreviewButton,()=>host.Editor.PreviewCommand.ExecutionTask);Check(!host.Editor.CanConfirm,"Unreversed path pose rejected");
                    host.PathEdgesList.UpdateLayout();var row=(ListBoxItem)host.PathEdgesList.ItemContainerGenerator.ContainerFromItem(host.Editor.PathEdges.Single());await ClickButton(Descendants(row).OfType<CheckBox>().Single());
                    Check(host.Editor.PathEdges.Single().Reverse,"Actual reverse toggle");await Preview(host.PreviewButton,()=>host.Editor.PreviewCommand.ExecutionTask);Check(host.Editor.CanConfirm,host.Editor.Status);Volume(document,10*area);
                }
                await Operate<GuidedEditorWindow>(()=>main.SweepCommand.ExecuteAsync(null),async host=>
                {
                    await ConfigureSweep(host);host.SectionPicker.BringIntoView();await Idle();Save(host,Path.Combine(output,$"sweep-section-{language}.png"));host.AvailableEdgePicker.BringIntoView();await Idle();Save(host,Path.Combine(output,$"sweep-path-{language}.png"));
                    host.OutputPicker.SelectedValue=GuidedOutput.Sheet;await Preview(host.PreviewButton,()=>host.Editor.PreviewCommand.ExecutionTask);Check(host.Editor.CanConfirm,host.Editor.Status);Check(document.PreviewScene!.Items.Any(i=>i.Geometry.Kind==BodyKind.Sheet),"Explicit sheet preview");await ClickButton(host.CancelButton);
                });Check(ReferenceEquals(baseline,session.Snapshot)&&document.PreviewScene is null,"Sweep cancel");
                await Operate<GuidedEditorWindow>(()=>main.SweepCommand.ExecuteAsync(null),async host=>{await ConfigureSweep(host);await Confirm(host.ConfirmButton,()=>host.Editor.ConfirmCommand.ExecutionTask);});await Accepted("sweep",language);
                Progress(output,language+" loft");
                async Task ConfigureLoft(GuidedEditorWindow host)
                {
                    foreach(var station in sections){await ClickButton(host.AddStationButton);host.AssignmentProfilePicker.SelectedItem=host.Editor.Profiles.Single(p=>p.Label!=Cadoryx.Lang.Strings.Strings.GuidedCurrentSection&&p.Section.Source?.SketchId==station.Sketch.Id&&p.Section.Source.MixedBoundaryIds.SequenceEqual(new[]{station.Curve}));await ClickButton(host.AssignProfileButton);}
                    await ClickButton(host.RuledCheckBox);await Preview(host.PreviewButton,()=>host.Editor.PreviewCommand.ExecutionTask);Check(host.Editor.CanConfirm,host.Editor.Status);Volume(document,5*area);
                }
                await Operate<GuidedEditorWindow>(()=>main.LoftCommand.ExecuteAsync(null),async host=>{await ConfigureLoft(host);host.AssignmentProfilePicker.BringIntoView();await Idle();Save(host,Path.Combine(output,$"loft-{language}.png"));await ClickButton(host.CancelButton);});
                Check(ReferenceEquals(baseline,session.Snapshot)&&document.PreviewScene is null,"Loft cancel");
                await Operate<GuidedEditorWindow>(()=>main.LoftCommand.ExecuteAsync(null),async host=>{await ConfigureLoft(host);await Confirm(host.ConfirmButton,()=>host.Editor.ConfirmCommand.ExecutionTask);});await Accepted("loft",language);
            }
            await session.SaveAsync(storage,Path.Combine(output,"baseline.cadoryx"));Check(await main.CloseAllAsync(),"Close documents");await ((App)Application.Current).StopRecoveryAsync();await Idle();
            Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Zero assets");listener.Flush();bindings.Flush();
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+"Expression consumers en-US/zh-CN/ja-JP: actual profile/path selections in extrusion, revolution, sweep and loft; directed Reverse Toggle, wrong-pose rejection, solid/sheet previews, independent volumes, confirm/cancel, undo/redo, save/reopen and zero assets. General curved offset sweep, desktop and physical input remain separately unverified.");window.CloseAfterSmoke();
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+ex);Application.Current.Shutdown(1);}
        finally{main.ChangeCultureCommand.Execute(original.ToString(CultureInfo.InvariantCulture));PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private static void Progress(string output,string step)=>File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {step}{Environment.NewLine}");
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    private static void Volume(CadDocumentViewModel document,double expected)=>Check(document.PreviewScene!.Items.Any(i=>Math.Abs(i.Geometry.VolumeMm3-expected)<1e-7),"Independent expression consumer volume "+expected+"; actual="+string.Join(",",document.PreviewScene.Items.Select(i=>i.Geometry.VolumeMm3.ToString("R",CultureInfo.InvariantCulture))));
    private static async Task Preview(Button button,Func<Task?> task){await ClickButton(button);await (task()??throw new InvalidOperationException("Preview did not execute"));await Idle();}
    private static async Task Confirm(Button button,Func<Task?> task){await ClickButton(button);await (task()??throw new InvalidOperationException("Confirm did not execute"));await Idle();}
    private static async Task Operate<T>(Func<Task> open,Func<T,Task> action)where T:Window
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _=Application.Current.Dispatcher.InvokeAsync(async()=>{T? editor=null;try{await Idle();editor=Application.Current.Windows.OfType<T>().Single();editor.Activate();await Idle();await action(editor);done.TrySetResult();}catch(Exception e){done.TrySetException(e);}finally{if(editor is {IsVisible:true})editor.Close();}},DispatcherPriority.ApplicationIdle);
        await open();await done.Task;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}}
    private static double Integral(Func<double,double> f)
    {const int n=8192;double sum=f(0)+f(1);for(int i=1;i<n;i++)sum+=(i%2==0?2:4)*f((double)i/n);return sum/(3*n);}
    private static (double X,double Y,double Dy) Offset(double u)
    {double v=2-4*u,s=Math.Sqrt(1+v*v);return(7.5+u+.03*v/s,2*u*(1-u)-.03/s,v-.12*v/(s*s*s));}
    private static double Area()=>Math.Abs(Integral(u=>{var(x,_,dy)=Offset(u);return x*dy;}));
    private static double RevolveVolume()=>Math.PI*Math.Abs(Integral(u=>{var(x,_,dy)=Offset(u);return x*x*dy;}));
}
