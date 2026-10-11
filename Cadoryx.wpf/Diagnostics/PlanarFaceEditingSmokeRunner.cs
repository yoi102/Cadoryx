using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using Antelcat.I18N.WPF;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Rendering;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Views;
using Microsoft.Extensions.DependencyInjection;
using OcctSharp;
using static Cadoryx.wpf.Diagnostics.SketchEditorSmokeRunner;
using static Cadoryx.wpf.Diagnostics.FaceFeatureEditorSmokeRunner;

namespace Cadoryx.wpf.Diagnostics;

internal static class PlanarFaceEditingSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));using var listener=new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        var culture=CultureInfo.CurrentUICulture;var vm=(MainWindowViewModel)window.DataContext;var markup=CultureInfo.GetCultureInfo(vm.CurrentCultureLCID);
        void Progress(string step)=>File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {step}{Environment.NewLine}");
        try
        {
            await Idle();var document=vm.ActiveDocument!;var assets=document.Session.Assets;var storage=services.GetRequiredService<IDocumentStorage>();
            using(var box=ShapeFactory.CreateBox(10,12,15))using(var cylinder=ShapeFactory.CreateCylinder(2,17))using(var tool=cylinder.Transformed(ShapeTransform.CreateTranslation(5,6,-1)))
            using(var shape=box.Cut(tool))using(var stored=OcctGeometryBridge.StoreShape(shape,assets))await document.Session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(stored.Geometry),"Perforated source"));
            var baseline=document.Session.Snapshot;var source=baseline.Features.Values.Single();ExactTopologySelection top,wall;
            using(var shape=OcctGeometryBridge.ReadShape(source.Result,assets))using(var map=RepairSnapshot.Create(shape))
            {
                ExactTopologySelection Select(Func<Shape,bool> predicate)
                {
                    var item=map.Topology.Single(t=>{if(t.Kind!=ShapeKind.Face)return false;using var face=map.CopySubshape(t.Selection);return predicate(face);});
                    return new(baseline.Id,source.Id,source.Result.Revision,source.Result.AssetId,map.Fingerprint,item.Selection.Index,HistoryShapeKind.Face,OcctGeometryKernel.HistoryAdapterVersion);
                }
                top=Select(s=>Math.Abs(s.GetBoundingBox().Minimum.Z-15)<1e-5&&Math.Abs(s.GetBoundingBox().Maximum.Z-15)<1e-5);
                wall=Select(s=>s.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder);
            }
            async Task Configure(FaceFeatureEditorWindow editor,bool removal,bool nativePick=false)
            {
                Check(editor.Editor.SelectedSource is null,"Explicit source required");editor.SourcePicker.SelectedItem=editor.Editor.Sources.Single(s=>s.Feature==source.Id);await Idle();
                Check(editor.ClosingFaceList.IsVisible&&!editor.ThicknessInput.IsVisible&&!editor.ProgramList.IsVisible&&!editor.OffsetDistanceInput.IsVisible,"Matching face controls only");
                Check(editor.PushPullDistanceInput.IsVisible==!removal,"Signed distance visibility");
                if(nativePick)
                {
                    var viewport=editor.Host.Viewport!;viewport.SetProjection(CadProjection.Top);viewport.FitAll();await Idle();var at=viewport.WorldToScreen(new(8,8,15));
                    viewport.PointerPressed(0,at.X,at.Y,0);viewport.PointerReleased(0,at.X,at.Y,0);await Idle();
                    if(editor.Editor.InspectFaceCommand.ExecutionTask is {} pending)await pending;else throw new InvalidOperationException("Native face-pick event did not start discovery");
                }
                else await editor.Editor.PickExactAsync(removal?wall:top);
                Check(editor.Editor.Faces.SequenceEqual(new[]{removal?wall:top}),"Complete exact selection");
                if(!removal){editor.PushPullDistanceInput.Value=-2;await Idle();Check(editor.Editor.PushPullDistance==-2,"Signed control binding");}
            }
            void Volume(double expected)=>Check(Math.Abs(document.PreviewScene!.Items.Single().Geometry.VolumeMm3-expected)<1e-5,"Exact native volume");
            async Task Capture(FaceFeatureEditorWindow editor,string name)
            {
                editor.Host.Viewport!.SetProjection(CadProjection.Axonometric);editor.Host.Viewport.FitAll();await Idle();
                Save(editor,Path.Combine(output,name+".png"));editor.Host.Viewport.SaveScreenshot(Path.Combine(output,name+"-native.png"));
            }
            foreach(var language in new[]{"en-US","zh-CN","ja-JP"})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;Progress(language+" push/pull");
                await Operate(()=>vm.PlanarPushPullCommand.ExecuteAsync(null),async editor=>
                {
                    await Configure(editor,false,language=="en-US");await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Volume((120-4*Math.PI)*13);await Capture(editor,"push-negative-"+language);
                    Check(ReferenceEquals(baseline,document.Session.Snapshot),"Atomic preview");editor.PushPullDistanceInput.Value=0;Check(!editor.Editor.CanConfirm,"Edited candidate invalidated");await Preview(editor);Check(!editor.Editor.CanConfirm,"Zero rejected");
                    editor.PushPullDistanceInput.Value=-20;await Preview(editor);Check(!editor.Editor.CanConfirm,"Complete removal rejected");
                    editor.PushPullDistanceInput.Value=2;await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Volume((120-4*Math.PI)*17);await Capture(editor,"push-positive-"+language);await ClickButton(editor.CancelButton);
                });
                Check(ReferenceEquals(baseline,document.Session.Snapshot),"Cancel preserves source");Progress(language+" heal");
                await Operate(()=>vm.DefeatureCommand.ExecuteAsync(null),async editor=>
                {await Configure(editor,true);await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Volume(1800);await Capture(editor,"remove-heal-"+language);await ClickButton(editor.CancelButton);});
                Check(ReferenceEquals(baseline,document.Session.Snapshot),"Healing cancel preserves source");
            }
            CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("en-US");I18NExtension.Culture=CultureInfo.CurrentUICulture;Progress("commit tree and storage");
            await Operate(()=>vm.PlanarPushPullCommand.ExecuteAsync(null),async editor=>{await Configure(editor,false);await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);await Confirm(editor);});
            var accepted=document.Session.Snapshot;var feature=accepted.Features.Values.Single(f=>f.Recipe is PlanarPushPullRecipe);
            await document.Session.UndoAsync();Check(ReferenceEquals(baseline,document.Session.Snapshot),"Single undo");await document.Session.RedoAsync();Check(ReferenceEquals(accepted,document.Session.Snapshot),"Exact redo");
            await Operate(()=>{document.EditFeature(feature.Id);return Task.CompletedTask;},async editor=>
            {Check(editor.Editor.IsEditing&&!editor.SourcePicker.IsEnabled&&editor.Editor.PushPullDistance==-2,"Complete tree author");editor.PushPullDistanceInput.Value=2;await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Volume((120-4*Math.PI)*17);await Confirm(editor);});
            var edited=document.Session.Snapshot;await document.Session.SaveAsync(storage,Path.Combine(output,"push.cadoryx"));
            using(var loaded=await storage.LoadAsync(Path.Combine(output,"push.cadoryx"),assets)){loaded.Snapshot.Validate();Check(((PlanarPushPullRecipe)loaded.Snapshot.Features[feature.Id].Recipe).Distance==2,"Native storage proof");}
            await Operate(()=>{document.EditFeature(feature.Id);return Task.CompletedTask;},async editor=>{await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);editor.Close();});Check(ReferenceEquals(edited,document.Session.Snapshot),"Close cancels candidate");
            await document.Session.UndoAsync();await document.Session.UndoAsync();Check(ReferenceEquals(baseline,document.Session.Snapshot),"Restore displayed source");
            await Operate(()=>vm.DefeatureCommand.ExecuteAsync(null),async editor=>{await Configure(editor,true);await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);await Confirm(editor);});
            var healed=document.Session.Snapshot;var removal=healed.Features.Values.Single(f=>f.Recipe is DefeatureRecipe);Check(Math.Abs(removal.Result.VolumeMm3-1800)<1e-5,"Complete healed solid");
            await Operate(()=>{document.EditFeature(removal.Id);return Task.CompletedTask;},async editor=>{Check(editor.Editor.Faces.SequenceEqual(new[]{wall}),"Stored exact removal set");await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);await ClickButton(editor.CancelButton);});
            await document.Session.SaveAsync(storage,Path.Combine(output,"healed.cadoryx"));using(var loaded=await storage.LoadAsync(Path.Combine(output,"healed.cadoryx"),assets))loaded.Snapshot.Validate();
            Progress("cleanup");Check(await vm.CloseAllAsync(),"Close all documents");await ((App)Application.Current).StopRecoveryAsync();await Idle();Check(((IAssetStoreStatistics)assets).Count==0,"Zero assets");listener.Flush();bindings.Flush();
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+"E4 en-US/zh-CN/ja-JP actual source/distance controls, finite perforated plane positive/negative native volume, native viewport API top-face pick, exact-selection API bore removal, zero/collapse rejection, cancel/close, commit/undo/redo/tree edit/native storage and zero assets. Physical input, external CAD and general face movement NOT VERIFIED.");window.CloseAfterSmoke();
        }
        catch(Exception e){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+e);Application.Current.Shutdown(1);}
        finally{CultureInfo.CurrentUICulture=culture;I18NExtension.Culture=markup;PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
