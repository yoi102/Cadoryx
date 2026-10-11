using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Antelcat.I18N.WPF;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Views;
using MahApps.Metro.Controls;
using Microsoft.Extensions.DependencyInjection;
using static Cadoryx.wpf.Diagnostics.SketchEditorSmokeRunner;

namespace Cadoryx.wpf.Diagnostics;

internal static class RegionOffsetSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output,
        SketchOffsetJoin join=SketchOffsetJoin.Miter,bool verifyJoins=false)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));using var listener=new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        var original=CultureInfo.CurrentUICulture;var main=(MainWindowViewModel)window.DataContext;var markup=CultureInfo.GetCultureInfo(main.CurrentCultureLCID);
        void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        void Progress(string value)=>File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {value}{Environment.NewLine}");
        try
        {
            await Idle();var document=main.ActiveDocument!;var session=document.Session;var kernel=services.GetRequiredService<IGeometryKernel>();
            var solver=services.GetRequiredService<ISketchConstraintSolver>();var storage=services.GetRequiredService<IDocumentStorage>();
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(1,1,1,RigidTransform3d.Translate(80,0,0)),"Support"));
            var part=session.Snapshot.Features.Values.Single().PartId;
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})
            {
                Progress(language+": region controls");CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;
                var draft=new SketchDraft(CadSketch.Create(part,"B10 "+language,RigidTransform3d.Identity));
                draft.AddRectangle(new(0,0),new(50,40));draft.AddCircle(new(25,20),12);draft.AddCircle(new(25,20),7);draft.AddCircle(new(25,20),3);
                await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,solver));var sketch=session.Snapshot.Sketches[draft.Value.Id];var baseline=session.Snapshot;
                var seeds=new[]{sketch.Lines[0].Id,sketch.Circles[0].Id,sketch.Circles[1].Id,sketch.Circles[2].Id};SketchEntityId[] created=[];
                for(int attempt=0;attempt<2;attempt++)
                {
                    await using var editor=new SketchEditorViewModel(document,kernel,solver,sketch,false);var host=new SketchEditorWindow(editor){Owner=window};
                    try
                    {
                        host.Show();host.Activate();await Idle();host.ToolPicker.SelectedValue="OffsetRegions";await Idle();host.GeometrySection.IsExpanded=true;await Idle();
                        if(verifyJoins)
                        {
                            var picker=(ComboBox)host.FindName("OffsetJoinPicker");Check(picker.IsVisible&&picker.Items.Count==3,"Missing production join picker");
                            picker.SelectedValue=join;await Idle();Check(editor.OffsetJoin==join,"Production join binding");
                            string label=join switch{SketchOffsetJoin.Miter=>Cadoryx.Lang.Strings.Strings.OffsetJoinMiter,
                                SketchOffsetJoin.Round=>Cadoryx.Lang.Strings.Strings.OffsetJoinRound,_=>Cadoryx.Lang.Strings.Strings.OffsetJoinBevel};
                            Check(editor.OffsetJoins.Single(j=>j.Key==join).Label==label,"Production join translation");
                        }
                        Check(editor.Tool=="OffsetRegions"&&editor.Tools.Single(t=>t.Key=="OffsetRegions").Label==Cadoryx.Lang.Strings.Strings.OffsetRegions,"Production tool label/binding");
                        foreach(var id in seeds)host.EntityList.SelectedItems.Add(editor.Entities.Single(e=>e.Id==id));await Idle();
                        Check(editor.SelectedEntities.ToHashSet().SetEquals(seeds),"Production multiselection lost a boundary");
                        var last=editor.Entities.Single(e=>e.Id==seeds[^1]);host.EntityList.SelectedItems.Remove(last);await Idle();
                        Check(!editor.SelectedEntities.Contains(seeds[^1])&&editor.SelectedEntities.Count==3,"Production boundary deselection failed");
                        host.EntityList.SelectedItems.Add(last);await Idle();var numeric=(NumericUpDown)host.FindName("OffsetDistanceInput");numeric.Value=4;await Idle();
                        var apply=(Button)host.FindName("ApplyRegionOffsetButton");Check(apply.IsVisible&&Equals(apply.Content,Cadoryx.Lang.Strings.Strings.ApplyRegionOffset),"Production apply visibility/translation");
                        var before=editor.Sketch;await ClickButton(apply);Check(ReferenceEquals(before,editor.Sketch),"Collapsed hole left a partial offset");
                        Check(editor.Status==Cadoryx.Lang.Strings.Strings.RegionOffsetCollapse,"Collapse diagnostic is not localized");
                        if(attempt==0)Save(host,Path.Combine(output,$"reject-{language}.png"));numeric.Value=1;await Idle();await ClickButton(apply);
                        Check(editor.Sketch.Lines.Length==(join==SketchOffsetJoin.Bevel?12:8)&&editor.Sketch.Circles.Length==6&&
                            editor.Sketch.Arcs.Length==(join==SketchOffsetJoin.Round?4:0),"Production region offset count");
                        Check(editor.Sketch.Constraints.SequenceEqual(sketch.Constraints),"Offset copied or removed source constraints");
                        created=editor.SelectedEntities.ToArray();Check(created.Length==4&&created.All(id=>!seeds.Contains(id)),"Output selection did not use newly authored identities");
                        host.Canvas.Fit();await Idle();if(attempt==0)Save(host,Path.Combine(output,$"offset-{language}.png"));
                        await ClickButton(host.PreviewButton);if(editor.PreviewCommand.ExecutionTask is {} preview)await preview;
                        Check(editor.CanConfirm,editor.Status);Check(ReferenceEquals(baseline,session.Snapshot),"Preview committed");
                        if(verifyJoins)
                        {
                            var accepted=editor.Sketch;((ComboBox)host.FindName("OffsetJoinPicker")).SelectedValue=join==SketchOffsetJoin.Round?SketchOffsetJoin.Bevel:SketchOffsetJoin.Round;
                            await Idle();Check(!editor.CanConfirm&&ReferenceEquals(accepted,editor.Sketch),"Join input reapplied offset or retained preview");
                            ((ComboBox)host.FindName("OffsetJoinPicker")).SelectedValue=join;await Idle();await ClickButton(host.PreviewButton);
                            if(editor.PreviewCommand.ExecutionTask is {} refreshed)await refreshed;Check(editor.CanConfirm,editor.Status);
                        }
                        if(attempt==0){await ClickButton(host.CancelButton);await Idle();Check(ReferenceEquals(baseline,session.Snapshot)&&document.PreviewScene is null,"Cancel changed state");}
                        else
                        {
                            await ClickButton(host.ConfirmButton);if(editor.ConfirmCommand.ExecutionTask is {} confirm)await confirm;await Idle();
                            var after=session.Snapshot;Check(!ReferenceEquals(baseline,after),"Confirm did not publish");
                            await session.UndoAsync();Check(ReferenceEquals(baseline,session.Snapshot),"Single document undo");
                            await session.RedoAsync();Check(ReferenceEquals(after,session.Snapshot),"Single document redo");
                        }
                    }
                    finally{host.Close();await Idle();}
                }
                Progress(language+": explicit resulting author tree and linked native solid");
                var committed=session.Snapshot.Sketches[sketch.Id];
                var root=SketchRegionOffset.SelectedRegions(committed,created).Single();var inner=root.Children[0].Children[0].Children[0];
                var reference=new SketchProfileReference(committed.Id,committed.Revision,root.PolygonLineIds){MixedBoundaryIds=root.MixedCurveIds,Regions=root.Children};
                var recipe=reference.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,committed.Plane));
                await session.ExecuteAsync(new AddBodyCommand(recipe,"B10 region body",part,sketchSource:reference));var feature=session.Snapshot.Features.Values.Single(f=>f.SketchSource?.SketchId==sketch.Id);
                double rectangle=50*40+2*(50+40)+(join==SketchOffsetJoin.Miter?4:join==SketchOffsetJoin.Round?Math.PI:2);
                double expected=(rectangle-Math.PI*11*11+Math.PI*8*8-Math.PI*2*2)*3;
                Check(Math.Abs(feature.Result.VolumeMm3-expected)<expected*1e-8,"Native material volume");
                await session.ExecuteAsync(new EditDocumentCommand("Isolate accepted region",doc=>doc with{Bodies=doc.Bodies.ToImmutableDictionary(p=>p.Key,p=>p.Value with{IsVisible=p.Key==feature.OutputBodyId})}));
                await Idle();var viewport=WindowSmokeRunner.Host(document).Viewport!;viewport.FitAll();await Idle();viewport.SaveScreenshot(Path.Combine(output,$"native-{language}.png"));
                string file=Path.Combine(output,$"accepted-{language}.cadoryx");await session.SaveAsync(storage,file);
                using(var loaded=await storage.LoadAsync(file,session.Assets))
                {
                    loaded.Snapshot.Validate();var loadedFeature=loaded.Snapshot.Features[feature.Id];
                    Check(loadedFeature.SketchSource!.Regions[0].Children[0].Children[0].CircleId==inner.CircleId,"Nested authored reference lost on reopen");
                    Check(SketchProfileReference.SameProfile(((ExtrudeRecipe)feature.Recipe).Profile,((ExtrudeRecipe)loadedFeature.Recipe).Profile),"Stored region profile differs");
                }
                if(verifyJoins)
                {
                    Progress(language+": single-loop join controls");
                    var loopDraft=new SketchDraft(CadSketch.Create(part,"B17 loop "+language,RigidTransform3d.Identity));loopDraft.AddRectangle(new(60,0),new(80,12));
                    await session.ExecuteAsync(new UpsertSketchCommand(loopDraft.Value,solver));var loopSource=session.Snapshot.Sketches[loopDraft.Value.Id];
                    for(int attempt=0;attempt<2;attempt++)
                    {
                        Progress(language+": loop attempt "+attempt);
                        var loopBaseline=session.Snapshot;
                        await using var editor=new SketchEditorViewModel(document,kernel,solver,loopSource,false);var host=new SketchEditorWindow(editor){Owner=window};
                        try
                        {
                            host.Show();host.Activate();await Idle();host.ToolPicker.SelectedValue="OffsetLoop";await Idle();
                            var picker=(ComboBox)host.FindName("OffsetJoinPicker");Check(picker.IsVisible,"Loop join picker hidden");picker.SelectedValue=join;
                            ((NumericUpDown)host.FindName("OffsetDistanceInput")).Value=1;await Idle();
                            editor.EditCurve(loopSource.Lines[0].Id,new(65,0));await Idle();
                            Check(editor.Sketch.Arcs.Length==(join==SketchOffsetJoin.Round?4:0)&&editor.SelectedEntities.Count==1&&!editor.SelectedEntities.Contains(loopSource.Lines[0].Id),"Loop join/output author selection");
                            host.Canvas.Fit();await Idle();if(attempt==0)Save(host,Path.Combine(output,$"loop-{language}.png"));
                            await ClickButton(host.PreviewButton);if(editor.PreviewCommand.ExecutionTask is {} preview)await preview;Check(editor.CanConfirm,editor.Status);
                            Progress(language+": loop preview "+attempt);
                            Check(ReferenceEquals(loopBaseline,session.Snapshot),"Loop preview committed");
                            if(attempt==0){await ClickButton(host.CancelButton);await Idle();Check(ReferenceEquals(loopBaseline,session.Snapshot),"Loop cancel changed state");}
                            else
                            {
                                await ClickButton(host.ConfirmButton);if(editor.ConfirmCommand.ExecutionTask is {} confirm)await confirm;await Idle();
                                var after=session.Snapshot;Check(!ReferenceEquals(loopBaseline,after),"Loop confirm omitted commit");
                                await session.UndoAsync();Check(ReferenceEquals(loopBaseline,session.Snapshot),"Loop document undo");await session.RedoAsync();Check(ReferenceEquals(after,session.Snapshot),"Loop document redo");
                            }
                        }
                        finally{host.Close();await Idle();}
                    }
                    // The loop confirmation occurs after the region-only saved fixture. Save the
                    // full accepted state before invoking the normal close flow, which correctly
                    // asks about unsaved authors instead of discarding them for this diagnostic.
                    await session.SaveAsync(storage,Path.Combine(output,$"loop-accepted-{language}.cadoryx"));
                    Check(!session.IsDirty,"Accepted loop was not saved before close");
                }
            }
            Progress("closing accepted documents; dirty="+session.IsDirty);
            Check(await main.CloseAllAsync(),"Close all");await ((App)Application.Current).StopRecoveryAsync();await Idle();
            Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Preview/author assets leaked");listener.Flush();bindings.Flush();
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+(verifyJoins?"B17 "+join+" loop/region join controls and preview invalidation; ":"B10 ")+"en-US/zh-CN/ja-JP production tool, numeric input, multiselection/deselection, localized atomic rejection, offset selected boundaries, preview/cancel/confirm, document undo/redo, explicit authored four-level region, native volume, save/reopen and zero remaining assets. Physical pointer is separate and unverified.");window.CloseAfterSmoke();
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+ex);Application.Current.Shutdown(1);}
        finally{CultureInfo.CurrentUICulture=original;I18NExtension.Culture=markup;PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
}
