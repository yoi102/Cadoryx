using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using Antelcat.I18N.WPF;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Sketching;
using Cadoryx.Rendering;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Views;
using Microsoft.Extensions.DependencyInjection;
using static Cadoryx.wpf.Diagnostics.SketchEditorSmokeRunner;

namespace Cadoryx.wpf.Diagnostics;

internal static class DatumEditorSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));using var listener=new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        var culture=CultureInfo.CurrentUICulture;var vm=(MainWindowViewModel)window.DataContext;var markupCulture=CultureInfo.GetCultureInfo(vm.CurrentCultureLCID);
        void Progress(string step)=>File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {step}{Environment.NewLine}");
        try
        {
            await Idle();var document=vm.ActiveDocument!;var storage=services.GetRequiredService<IDocumentStorage>();
            await document.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(20,10,5,RigidTransform3d.Identity),"Base"));var box=document.Session.Snapshot.Features.Values.Single();
            var draft=new SketchDraft(CadSketch.Create(box.PartId,"Bracket",RigidTransform3d.Identity));draft.AddRectangle(new(0,0),new(4,3));
            await document.Session.ExecuteAsync(new UpsertSketchCommand(draft.Value,new ManagedSketchConstraintSolver()));var sketch=document.Session.Snapshot.Sketches[draft.Value.Id];var source=SketchProfileReference.Create(sketch,sketch.Lines.Select(l=>l.Id));
            await document.Session.ExecuteAsync(new AddBodyCommand(source.Resolve(document.Session.Snapshot,box.PartId,new ExtrudeRecipe(new([]),2,sketch.Plane)),"Bracket",box.PartId,sketchSource:source));
            var extrusion=document.Session.Snapshot.Features.Values.Single(f=>f.Id!=box.Id);
            await document.Session.ExecuteAsync(new AddBodyCommand(new CylinderRecipe(3,6,RigidTransform3d.Translate(30,0,0)),"Cylinder datum source",box.PartId));
            var cylinder=document.Session.Snapshot.Features.Values.Single(f=>f.Recipe is CylinderRecipe);
            await document.Session.SaveAsync(storage,Path.Combine(output,"baseline.cadoryx"));var baseline=document.Session.Snapshot;
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;
                await Operate(()=>vm.EditDatumsCommand.ExecuteAsync(null),async editor=>
                {
                    Progress($"C2 {language}: semantic Box plane and explicit associations");editor.Activate();await Idle();
                    Progress($"window before input: loaded={editor.IsLoaded}, visible={editor.IsVisible}, size={editor.ActualWidth}x{editor.ActualHeight}, edit={editor.Editor.CanEdit}");
                    Check(editor.IsLoaded&&editor.IsVisible,"Datum window did not load: "+vm.StatusText);
                    editor.PlaneNameInput.Text="Top clearance";editor.Editor.Primary=editor.Editor.Sources.Single(s=>s.Source==DatumPlaneSource.Box(box.Id,BoxBoundary.ZMax));editor.ValueInput.Value=2;
                    Progress($"window after input: visible={editor.IsVisible}, edit={editor.Editor.CanEdit}, status={editor.Editor.Status}");
                    await ClickButton(editor.ApplyPlaneButton);Check(editor.Editor.Planes.Count==1,"Actual plane apply button");
                    await ClickButton(editor.BindSketchButton);await ClickButton(editor.BindWorkPlaneButton);Check(editor.Editor.Book.SketchBindings.Count==1&&editor.Editor.Book.WorkPlane is not null,"Actual sketch/workplane bind buttons");
                    await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Check(ReferenceEquals(baseline,document.Session.Snapshot),"Preview keeps baseline");
                    Check(document.PreviewScene!.Items.Any(i=>Math.Abs(i.Geometry.VolumeMm3-24)<1e-6&&Math.Abs(i.Geometry.Bounds.Min.Z-7)<1e-6),"Native bound extrusion translated to Z=7");
                    Check(editor.Editor.Planes.Single().Plane.Frame.Translation.Z==7,"Evaluated datum frame in grid");
                    editor.PrimarySource.BringIntoView();await Idle();Save(editor,Path.Combine(output,$"datums-{language}.png"));
                    editor.ValueInput.Value=3;await Idle();Check(!editor.Editor.CanConfirm,"Unapplied input invalidates preview");await Preview(editor);Check(!editor.Editor.CanConfirm,"Preview cannot consume unapplied values");
                    await ClickButton(editor.ApplyPlaneButton);await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);editor.Close();
                });
                Check(ReferenceEquals(baseline,document.Session.Snapshot),"Window close cancels associations and native preview");
                await Operate(()=>vm.EditDatumsCommand.ExecuteAsync(null),async editor=>
                {
                    Progress($"C2 extended {language}: point, axis and exact native plane controls");editor.Activate();await Idle();
                    Check(editor.Editor.NativeSource is null,"No implicit exact source");
                    editor.ConstructionTabs.SelectedIndex=2;editor.PointPanel.PointNameInput.Text="P";editor.PointPanel.PointXInput.Value=6;editor.Editor.PointY=2;editor.Editor.PointZ=3;
                    await ClickButton(editor.PointPanel.ApplyPointButton);Check(editor.Editor.Points.Count==1,"Actual point apply");
                    editor.Editor.NewPointCommand.Execute(null);editor.PointPanel.PointNameInput.Text="Q";editor.Editor.PointKind=DatumPointKind.Offset;editor.Editor.PointFirst=editor.Editor.PointReferences.Single(r=>r.Label=="P");editor.Editor.PointX=1;
                    await ClickButton(editor.PointPanel.ApplyPointButton);Check(editor.Editor.Points.Count==2,"Explicit offset point");
                    editor.ConstructionTabs.SelectedIndex=1;editor.AxisPanel.AxisNameInput.Text="Spin";editor.AxisPanel.AxisKindInput.SelectedValue=DatumAxisKind.TwoPoints;
                    editor.Editor.AxisFirst=editor.Editor.AxisFirstSources.Single(r=>r.Label=="P");editor.Editor.AxisSecond=editor.Editor.AxisSecondSources.Single(r=>r.Label=="Q");
                    await ClickButton(editor.AxisPanel.ApplyAxisButton);Check(editor.Editor.Axes.Count==1,"Actual two-point axis apply");
                    editor.ConstructionTabs.SelectedIndex=0;editor.PlaneNameInput.Text="Exact top";editor.NativeSourceInput.SelectedItem=editor.Editor.NativeSources.Single(s=>s.Feature==box.Id);await Idle();
                    await Pick(editor,CadProjection.Top,new(10,5,5));await ClickButton(editor.UsePickedPlaneButton);await ClickButton(editor.ApplyPlaneButton);
                    Check(editor.Editor.Planes.Single().Plane.Primary.Kind==DatumSourceKind.ExactFace,"Native exact plane recipe");await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);
                    Check(Math.Abs(editor.Editor.Planes.Single().Plane.Frame.Translation.Z-5)<1e-8,"Exact plane origin");
                    editor.ConstructionTabs.SelectedIndex=1;editor.Editor.NewAxisCommand.Execute(null);editor.AxisPanel.AxisNameInput.Text="Native spin";
                    editor.NativeSourceInput.SelectedItem=editor.Editor.NativeSources.Single(s=>s.Feature==cylinder.Id);editor.Editor.PickEdges=false;await Idle();
                    await Pick(editor,CadProjection.Front,new(30,-3,3));Check(editor.Editor.AxisKind==DatumAxisKind.Analytic,editor.Editor.Status);await ClickButton(editor.AxisPanel.ApplyAxisButton);
                    Check(editor.Editor.Axes.Single(r=>r.Name=="Native spin").Axis.Exact?.Kind==ExactDatumKind.Cylinder,"Explicit native cylinder axis");
                    editor.ConstructionTabs.SelectedIndex=2;editor.Editor.NewPointCommand.Execute(null);editor.PointPanel.PointNameInput.Text="Circle center";editor.Editor.PickEdges=true;await Idle();
                    await Pick(editor,CadProjection.Top,new(30,3,6));Check(editor.Editor.PointKind==DatumPointKind.CircleCenter,editor.Editor.Status);await ClickButton(editor.PointPanel.ApplyPointButton);await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);
                    Check((editor.Editor.Points.Single(r=>r.Name=="Circle center").Point.Position-new Vector3d(30,0,6)).Length<1e-7,"Explicit native circular edge center");
                    Save(editor,Path.Combine(output,$"extended-circle-{language}.png"));editor.ConstructionTabs.SelectedIndex=1;Save(editor,Path.Combine(output,$"extended-cylinder-axis-{language}.png"));
                    editor.ConstructionTabs.SelectedIndex=0;editor.NativeSourceInput.SelectedItem=editor.Editor.NativeSources.Single(s=>s.Feature==box.Id);editor.Editor.PickEdges=false;await Idle();
                    editor.Host.Viewport!.SetProjection(CadProjection.Axonometric);editor.Host.Viewport.FitAll();await Idle();
                    Save(editor,Path.Combine(output,$"extended-plane-{language}.png"));editor.Host.Viewport.SaveScreenshot(Path.Combine(output,$"extended-native-{language}.png"));
                    editor.ConstructionTabs.SelectedIndex=1;Save(editor,Path.Combine(output,$"extended-axis-{language}.png"));
                    editor.ConstructionTabs.SelectedIndex=2;Save(editor,Path.Combine(output,$"extended-point-{language}.png"));
                    var oldAxis=editor.Editor.Axes.Single(r=>r.Name=="Spin").Axis.Frame;var oldPoint=editor.Editor.Points.Single(r=>r.Name=="Q").Point.Position;
                    editor.Editor.SelectedAxis=editor.Editor.Axes.Single(r=>r.Name=="Spin");editor.Editor.DetachAxisCommand.Execute(null);editor.Editor.SelectedPoint=editor.Editor.Points.Single(r=>r.Name=="Q");editor.Editor.DetachPointCommand.Execute(null);
                    await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Check(DatumPlanes.SameFrame(oldAxis,editor.Editor.Axes.Single(r=>r.Name=="Spin").Axis.Frame)&&oldPoint==editor.Editor.Points.Single(r=>r.Name=="Q").Point.Position,"Detach retains exact evaluated values");
                    editor.Close();
                });
                Check(ReferenceEquals(baseline,document.Session.Snapshot),"Extended controls close-cancel candidate");
            }
            CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("en-US");I18NExtension.Culture=CultureInfo.CurrentUICulture;
            await Operate(()=>vm.EditDatumsCommand.ExecuteAsync(null),async editor=>
            {
                editor.PlaneNameInput.Text="Top clearance";editor.Editor.Primary=editor.Editor.Sources.Single(s=>s.Source==DatumPlaneSource.Box(box.Id,BoxBoundary.ZMax));editor.ValueInput.Value=2;
                await ClickButton(editor.ApplyPlaneButton);await ClickButton(editor.BindSketchButton);await ClickButton(editor.BindWorkPlaneButton);await Preview(editor);
                Check(editor.Editor.CanConfirm,editor.Editor.Status);await ClickButton(editor.ConfirmButton);if(editor.Editor.ConfirmCommand.ExecutionTask is {} task)await task;
            });
            var committed=document.Session.Snapshot;Check(committed.Sketches[sketch.Id].Plane.Translation.Z==7,"Committed bound sketch");
            await document.Session.UndoAsync();Check(ReferenceEquals(baseline,document.Session.Snapshot),"Single association undo");await document.Session.RedoAsync();Check(ReferenceEquals(committed,document.Session.Snapshot),"Exact association redo");
            var plane=committed.Datums.Planes.Values.Single();var height=new CadParameter(Guid.NewGuid(),"height",ParameterDimension.Length,"12 mm");var offset=new CadParameter(Guid.NewGuid(),"clearance",ParameterDimension.Length,"height / 4");
            await document.Session.ExecuteAsync(new SetParametersCommand(new([height,offset],[new(Guid.NewGuid(),height.Id,ParameterTarget.Feature(box.Id,"Z")),new(Guid.NewGuid(),offset.Id,ParameterTarget.Datum(plane.Id,"Offset"))])));
            Check(document.Session.Snapshot.Sketches[sketch.Id].Plane.Translation.Z==15,"Joint parameter/source/sketch frame");Check(((ExtrudeRecipe)document.Session.Snapshot.Features[extrusion.Id].Recipe).Placement.Translation.Z==15,"Joint native extrusion placement");
            await document.Session.SaveAsync(storage,Path.Combine(output,"accepted.cadoryx"));
            using(var loaded=await storage.LoadAsync(Path.Combine(output,"accepted.cadoryx"),document.Session.Assets)){loaded.Snapshot.Validate();Check(loaded.Snapshot.Datums.SketchBindings.Count==1,"Stored association roundtrip");}
            // The newly created sketch must inherit part-local datum coordinates even when the workplane uses an occurrence.
            await OperateSketch(()=>vm.NewSketchCommand.ExecuteAsync(null),async editor=>
            {Check(editor.Editor.Sketch.PartId==box.PartId&&editor.Editor.Sketch.Plane.Translation.Z==15&&!editor.Editor.CanChangePlane,"New sketch datum inheritance");editor.Close();await Idle();});
            await ReselectFrozenAxis(vm,document,cylinder,output);await RevolveControls(document,output);
            Progress("C2 rotation/reselection completed; saving before close");await document.Session.SaveAsync(storage,Path.Combine(output,"accepted-extended.cadoryx"));
            using(var loaded=await storage.LoadAsync(Path.Combine(output,"accepted-extended.cadoryx"),document.Session.Assets)){loaded.Snapshot.Validate();Check(loaded.Snapshot.Features.Values.Count(f=>f.Recipe is RevolveRecipe)==2,"Production extended rotations roundtrip");}
            Check(await vm.CloseAllAsync(),"Close documents");await ((App)Application.Current).StopRecoveryAsync();await Idle();
            Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Zero assets");listener.Flush();bindings.Flush();
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+"C2 production datum window en-US/zh-CN/ja-JP; actual plane/axis/point apply, explicit native API plane/cylinder face and circular edge selection, two-point axis, offset and circle-center point; detach preserves evaluated geometry, close cancels candidates; actual sketch/workplane binding, preview/confirm buttons; semantic Box source, evaluated local frames and native dependent extrusion; frozen native source and explicit same-ID axis reselection restore dependent revolution; production revolution-axis selection/detach/cancel/confirm buttons in all three languages, directed exact half-ring and one undo; unapplied-input guard; joint C1 parameter/source/sketch/native update, save/reopen, new sketch datum inheritance and zero assets. Layout screenshots and native HWND screenshots are separate; real desktop pointer NOT VERIFIED.");window.CloseAfterSmoke();
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+ex);Application.Current.Shutdown(1);}
        finally{CultureInfo.CurrentUICulture=culture;I18NExtension.Culture=markupCulture;PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private static async Task Preview(DatumEditorWindow editor)
    {await ClickButton(editor.PreviewButton);if(editor.Editor.PreviewCommand.ExecutionTask is {} task)await task;await Idle();}
    private static async Task RevolveControls(CadDocumentViewModel document,string output)
    {
        var before=document.Session.Snapshot;var part=before.Definitions.Values.OfType<PartDefinition>().Single().Id;
        var axis=new CadDatumAxis(Guid.NewGuid(),part,"Revolution datum",DatumAxisKind.Independent,Origin:new(2,0,0),Direction:Vector3d.UnitZ,Reverse:true);
        await document.Session.ExecuteAsync(new SetDatumsCommand(before.Datums with{Axes=before.Datums.Axes.Add(axis.Id,axis)}));axis=document.Session.Snapshot.Datums.Axes[axis.Id];
        await document.Session.ExecuteAsync(new AddBodyCommand(new RevolveRecipe(SketchProfile.FromCircle(new(-4,0),1),Math.PI,RigidTransform3d.Identity)
            {Axis=new(axis.Frame.Translation,axis.Frame.Rotation.Rotate(Vector3d.UnitZ),axis.Id,axis.Revision)},"Axis ring",part));
        var feature=document.Session.Snapshot.Features.Values.Single(f=>f.Name=="Axis ring");var baseline=document.Session.Snapshot;
        foreach(string language in new[]{"en-US","zh-CN","ja-JP"})
        {
            CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;document.EditFeature(feature.Id);
            var view=new Views.Toolboxes.ModelingToolboxView{DataContext=new{Document=document}};
            var host=new MahApps.Metro.Controls.MetroWindow{Title=Cadoryx.Lang.Strings.Strings.RevolveAxis,Width=760,Height=Math.Min(980,SystemParameters.WorkArea.Height),Content=view,Owner=Application.Current.MainWindow,WindowStartupLocation=WindowStartupLocation.CenterOwner};
            try
            {
                host.Show();host.Activate();await Idle();var selector=(ComboBox)view.FindName("RevolveAxisInput");
                Check(((RevolveAxisChoice)selector.SelectedItem).Id==axis.Id,"Production axis selection retains stable identity");
                async Task PreviewModel(){var button=(Button)view.FindName("ModelingPreviewButton");button.BringIntoView();await Idle();await ClickButton(button);if(document.PreviewCommand.ExecutionTask is {} task)await task;await Idle();Check(document.HasPreview,document.ToolStatus);}
                await PreviewModel();Check(Math.Abs(document.PreviewGeometry!.VolumeMm3-6*Math.PI*Math.PI)<1e-5,"Directed native half-ring preview");
                selector.BringIntoView();await Idle();Save(host,Path.Combine(output,$"revolve-axis-{language}.png"));
                var detach=(Button)view.FindName("DetachRevolveAxisButton");await ClickButton(detach);Check(!document.HasPreview&&!document.CanDetachRevolveAxis,"Actual detach invalidates preview");
                await PreviewModel();await ClickButton((Button)view.FindName("ModelingCancelButton"));Check(ReferenceEquals(baseline,document.Session.Snapshot),"Actual cancel keeps committed axis recipe");
                selector.SelectedItem=document.RevolveAxes.Single(a=>a.Id==axis.Id);await PreviewModel();await ClickButton((Button)view.FindName("ModelingConfirmButton"));if(document.ConfirmCommand.ExecutionTask is {} confirm)await confirm;
                Check(((RevolveRecipe)document.Session.Snapshot.Features[feature.Id].Recipe).Axis!.Datum==axis.Id,"Actual confirm retains datum");
                await document.Session.UndoAsync();Check(ReferenceEquals(baseline,document.Session.Snapshot),"Single native rotation undo");
            }
            finally{document.CancelCommand.Execute(null);host.Close();}
        }
    }
    private static async Task ReselectFrozenAxis(MainWindowViewModel main,CadDocumentViewModel document,FeatureDefinition cylinder,string output)
    {
        await Operate(()=>main.EditDatumsCommand.ExecuteAsync(null),async editor=>
        {
            editor.ConstructionTabs.SelectedIndex=1;editor.Editor.NewAxisCommand.Execute(null);editor.AxisPanel.AxisNameInput.Text="Cylinder driven rotation";
            editor.NativeSourceInput.SelectedItem=editor.Editor.NativeSources.Single(s=>s.Feature==cylinder.Id);editor.Editor.PickEdges=false;await Pick(editor,CadProjection.Front,new(30,-3,3));
            await ClickButton(editor.AxisPanel.ApplyAxisButton);await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);await ClickButton(editor.ConfirmButton);if(editor.Editor.ConfirmCommand.ExecutionTask is {} task)await task;
        });
        var axis=document.Session.Snapshot.Datums.Axes.Values.Single(a=>a.Name=="Cylinder driven rotation");
        await document.Session.ExecuteAsync(new AddBodyCommand(new RevolveRecipe(SketchProfile.FromCircle(new(40,0),1),Math.PI,RigidTransform3d.Identity)
            {Axis=new(axis.Frame.Translation,axis.Frame.Rotation.Rotate(Vector3d.UnitZ),axis.Id,axis.Revision)},"Source driven ring",cylinder.PartId));
        var ring=document.Session.Snapshot.Features.Values.Single(f=>f.Name=="Source driven ring");
        await document.Session.ExecuteAsync(new RecomputeCommand(cylinder.Id,new CylinderRecipe(3,9,RigidTransform3d.Translate(30,0,0))));
        Check(document.Session.Snapshot.Datums.Axes[axis.Id].IsStale&&document.Session.Snapshot.Features[ring.Id].IsStale,"Changed native source freezes axis and dependent rotation");
        await Operate(()=>main.EditDatumsCommand.ExecuteAsync(null),async editor=>
        {
            editor.ConstructionTabs.SelectedIndex=1;editor.AxisPanel.AxisList.SelectedItem=editor.Editor.Axes.Single(a=>a.Axis.Id==axis.Id);await Idle();
            Check(editor.Editor.SelectedAxis!.Axis.IsStale,"Production editor exposes stale selected axis");
            editor.NativeSourceInput.SelectedItem=editor.Editor.NativeSources.Single(s=>s.Feature==cylinder.Id);editor.Editor.PickEdges=false;await Pick(editor,CadProjection.Front,new(30,-3,4.5));
            await ClickButton(editor.AxisPanel.ApplyAxisButton);await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);
            Check(editor.Editor.Axes.Single(a=>a.Axis.Id==axis.Id).Axis.Exact!.Selection.Revision==document.Session.Snapshot.Features[cylinder.Id].Result.Revision,"Explicit new native evidence retains datum ID");
            Save(editor,Path.Combine(output,"extended-stale-axis-reselected.png"));await ClickButton(editor.ConfirmButton);if(editor.Editor.ConfirmCommand.ExecutionTask is {} task)await task;
        });
        Check(!document.Session.Snapshot.Datums.Axes[axis.Id].IsStale&&!document.Session.Snapshot.Features[ring.Id].IsStale,"Explicit same-ID reselection restores dependent native rotation");
    }
    private static async Task Operate(Func<Task> open,Func<DatumEditorWindow,Task> action)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _=Application.Current.Dispatcher.InvokeAsync(async()=>
        {
            DatumEditorWindow? editor=null;
            try{await Idle();editor=Application.Current.Windows.OfType<DatumEditorWindow>().Single();for(int i=0;i<100&&(!editor.IsLoaded||!editor.IsVisible||editor.Host.Viewport is null);i++)await Task.Delay(20);Check(editor.IsLoaded&&editor.IsVisible&&editor.Host.Viewport is not null,"Datum window/native host not ready");await action(editor);completion.TrySetResult();}
            catch(Exception ex){completion.TrySetException(ex);}finally{if(editor is {IsVisible:true})editor.Close();}
        },DispatcherPriority.ApplicationIdle);
        await open();await completion.Task;
    }
    private static async Task OperateSketch(Func<Task> open,Func<SketchEditorWindow,Task> action)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _=Application.Current.Dispatcher.InvokeAsync(async()=>
        {SketchEditorWindow? editor=null;try{await Idle();editor=Application.Current.Windows.OfType<SketchEditorWindow>().Single();await action(editor);completion.TrySetResult();}catch(Exception ex){completion.TrySetException(ex);}finally{if(editor is {IsVisible:true})editor.Close();}},DispatcherPriority.ApplicationIdle);
        await open();await completion.Task;
    }
    private static async Task Pick(DatumEditorWindow editor,CadProjection projection,Vector3d point)
    {
        var viewport=editor.Host.Viewport??throw new InvalidOperationException("Native datum viewport not ready");viewport.SetProjection(projection);viewport.FitAll();await Idle();
        var at=viewport.WorldToScreen(point);viewport.PointerPressed(0,at.X,at.Y,0);viewport.PointerReleased(0,at.X,at.Y,0);
        for(int i=0;i<100&&editor.Editor.IsWorking;i++)await Task.Delay(20);await Idle();Check(!editor.Editor.IsWorking,"Datum native pick did not finish");
    }
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
