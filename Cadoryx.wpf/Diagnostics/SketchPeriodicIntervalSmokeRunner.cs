using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using Antelcat.I18N.WPF;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Rendering;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Views;
using Microsoft.Extensions.DependencyInjection;
using static Cadoryx.wpf.Diagnostics.SketchEditorSmokeRunner;

namespace Cadoryx.wpf.Diagnostics;

internal static class SketchPeriodicIntervalSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));using var listener=new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        var original=CultureInfo.CurrentUICulture;var main=(MainWindowViewModel)window.DataContext;var markup=CultureInfo.GetCultureInfo(main.CurrentCultureLCID);
        try
        {
            await Idle();var doc=main.ActiveDocument!;var session=doc.Session;var kernel=services.GetRequiredService<IGeometryKernel>();
            var solver=services.GetRequiredService<ISketchConstraintSolver>();var storage=services.GetRequiredService<IDocumentStorage>();
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(1,1,1,RigidTransform3d.Translate(80,0,0)),"Support"));var part=session.Snapshot.Features.Values.Single().PartId;
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})for(int range=0;range<4;range++)foreach(bool reversed in new[]{false,true})
            {
                doc.Review.ShowAll();doc.Selection.Replace([]);await Idle();
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;
                string sample=$"{range}-{(reversed?1:0)}-{language}";
                File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {sample}: periodic finite controls{Environment.NewLine}");
                var g=range==3?new SketchFreeformGeometry(SketchFreeformKind.BSpline,
                    [new(8,0),new(10,4),new(12,7),new(16,0),new(12,-4),new(10,-2)],
                    [1,.7,1.1,.8,1.2,1],[5,5.7,7],[3,3,3],3,true,5.1,5.7):
                    new SketchFreeformGeometry(SketchFreeformKind.BSpline,[new(8,0),new(12,4),new(16,0),new(12,-4)],
                    [1,.7,1.2,.8],[0,1,2,3,4],[1,1,1,1,1],3,true,.5,1.5);
                var draft=new SketchDraft(CadSketch.Create(part,$"Periodic {range+1}",RigidTransform3d.Identity));draft.AddFreeform(g);var id=draft.Value.Freeforms.Single().Id;
                draft.ExposeCurveEndpoints(id);var ends=SketchFixedCurveEndpoints.All(draft.Value).Single().Ends;var(a,b)=SketchFixedCurveEndpoints.Positions(draft.Value,id);
                draft.AddLine(b,a,start:ends.End,end:ends.Start);await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,solver));var initial=session.Snapshot.Sketches[draft.Value.Id];var baseline=session.Snapshot;
                double first=range==0?3.5:range==1?4.25:range==2?-.75:7.5,last=range==0?4.5:range==1?5.25:range==2?.25:9;
                var expected=g with{FirstParameter=first,LastParameter=last,Reversed=reversed};
                await using(var editor=new SketchEditorViewModel(doc,kernel,solver,initial,false))
                {
                    var host=new SketchEditorWindow(editor){Owner=window};host.Show();await Idle();
                    try
                    {
                        editor.Select(id);host.FreeformSection.IsExpanded=true;host.ApplyFreeformButton.BringIntoView();await Idle();
                        host.FreeformFirstInput.Value=first;host.FreeformLastInput.Value=first+5;await ClickButton(host.ApplyFreeformButton);
                        Check(g.SameValues(editor.Sketch.Freeforms.Single().Geometry),"Invalid multi-period input changed the draft");
                        host.FreeformLastInput.Value=last;if(reversed)await ClickButton(host.FreeformReverseInput);await ClickButton(host.ApplyFreeformButton);
                        Check(expected.SameValues(editor.Sketch.Freeforms.Single().Geometry),"Periodic controls lost basis, interval or direction");
                        Check(SketchFixedCurveEndpoints.All(editor.Sketch).Single().Ends==ends,"Periodic numeric edit changed endpoint IDs");host.Canvas.Fit();await Idle();
                        Save(host,Path.Combine(output,$"periodic-{sample}.png"));
                        await ClickButton(host.PreviewButton);if(editor.PreviewCommand.ExecutionTask is {} preview)await preview;Check(editor.CanConfirm,editor.Status);
                        Check(ReferenceEquals(baseline,session.Snapshot),"Periodic preview committed");await ClickButton(host.ConfirmButton);if(editor.ConfirmCommand.ExecutionTask is {} confirm)await confirm;await Idle();
                    }
                    finally{host.Close();await Idle();}
                }
                var accepted=session.Snapshot;var updated=accepted.Sketches[initial.Id];Check(expected.SameValues(updated.Freeforms.Single().Geometry),"Confirmed periodic author definition changed");
                await session.UndoAsync();Check(ReferenceEquals(baseline,session.Snapshot),"Periodic one undo");await session.RedoAsync();Check(ReferenceEquals(accepted,session.Snapshot),"Periodic one redo");
                var source=SketchProfileReference.CreateMixed(updated,[id,updated.Lines.Single().Id]);await session.ExecuteAsync(new AddBodyCommand(source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,updated.Plane)),"Periodic solid",part,sketchSource:source));
                var feature=session.Snapshot.Features.Values.Single(f=>f.SketchSource?.SketchId==updated.Id);Check(feature.Result.VolumeMm3>0,"Native periodic solid unavailable");
                await Idle();
                Check(doc.Scene.StateId==session.Snapshot.StateId,"Periodic scene has not reached the accepted document revision");
                var sceneItem=doc.Scene.Items.Single(i=>i.BodyId==feature.OutputBodyId&&i.Geometry.Revision==feature.Result.Revision);
                doc.Selection.Replace([new(sceneItem.Path,sceneItem.BodyId,sceneItem.Geometry.Revision)]);
                Check(doc.Review.IsolateCommand.CanExecute(null),"Periodic solid cannot be isolated");
                doc.Review.IsolateCommand.Execute(null);doc.Selection.Replace([]);await Idle();
                var visible=doc.Review.Filter(doc.Scene).Items;
                Check(visible.Length==1&&visible[0].Geometry.Revision==feature.Result.Revision,"Periodic screenshot includes another sample");
                var viewport=WindowSmokeRunner.Host(doc).Viewport!;
                Check(viewport.VisibleBodyCount==1,"Native periodic viewport has not isolated the current sample");
                viewport.SetProjection(CadProjection.Axonometric);viewport.FitAll();viewport.Redraw();await Idle();
                viewport.SaveScreenshot(Path.Combine(output,$"native-periodic-{sample}.png"));
                await File.WriteAllTextAsync(Path.Combine(output,$"native-periodic-{sample}.json"),JsonSerializer.Serialize(new{
                    sample,feature=feature.Id,body=feature.OutputBodyId,geometryRevision=feature.Result.Revision,
                    sceneState=doc.Scene.StateId,visibleBodies=viewport.VisibleBodyCount,volumeMm3=feature.Result.VolumeMm3,
                    originalBasis=expected,isolatedCurrentSample=true}));
                string path=Path.Combine(output,$"periodic-{sample}.cadoryx");await session.SaveAsync(storage,path);
                using(var loaded=await storage.LoadAsync(path,session.Assets)){loaded.Snapshot.Validate();Check(expected.SameValues(loaded.Snapshot.Sketches[updated.Id].Freeforms.Single().Geometry),"Periodic storage altered the original basis");}
            }
            Check(await main.CloseAllAsync(),"Close periodic interval documents");await ((App)Application.Current).StopRecoveryAsync();await Idle();
            Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Periodic interval assets remain");
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+"B11 periodic finite interval production controls en-US/zh-CN/ja-JP; seam, positive/negative shifted domain, reversed direction, invalid multi-period refusal, retained original rational basis and explicit endpoint IDs, native mixed solids, preview/confirm, one undo/redo, save/reopen and zero remaining assets. WPF layout and native viewport screenshots are separate; physical input remains unverified.");window.CloseAfterSmoke();
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+ex);window.CloseAfterSmoke();}
        finally{CultureInfo.CurrentUICulture=original;I18NExtension.Culture=markup;PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
