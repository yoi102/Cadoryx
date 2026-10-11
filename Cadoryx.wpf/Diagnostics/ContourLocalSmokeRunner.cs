using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Antelcat.I18N.WPF;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Rendering;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Views;
using Microsoft.Extensions.DependencyInjection;
using static Cadoryx.wpf.Diagnostics.SketchEditorSmokeRunner;
using Operation=Cadoryx.Db.LocalFeatureOperation;

namespace Cadoryx.wpf.Diagnostics;

internal static class ContourLocalSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));using var listener=new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        var culture=CultureInfo.CurrentUICulture;var vm=(MainWindowViewModel)window.DataContext;var markupCulture=CultureInfo.GetCultureInfo(vm.CurrentCultureLCID);
        void Progress(string step)=>File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {step}{Environment.NewLine}");
        try
        {
            await Idle();var document=vm.ActiveDocument!;var storage=services.GetRequiredService<IDocumentStorage>();
            await document.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,7.5,RigidTransform3d.Identity),"Lower"));
            var lower=document.Session.Snapshot.Bodies.Values.Single();
            await document.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,7.5,RigidTransform3d.Translate(0,0,7.5)),"Upper",lower.PartId));
            await document.Session.ExecuteAsync(new BooleanCommand(BooleanOperation.Fuse,document.Session.Snapshot.Bodies.Keys.ToArray()));
            var baseline=document.Session.Snapshot;var source=baseline.Bodies.Values.Single();
            async Task ConfigureFillet(ContourLocalEditorWindow editor)
            {
                Check(editor.Editor.SelectedSource is null,"No implicit contour source");editor.SourcePicker.SelectedItem=editor.Editor.Sources.Single(s=>s.Body==source.Id);await Idle();
                await Pick(editor,CadProjection.Front,new(0,0,3));Check(editor.Editor.Programs.Count==1,"Native edge seed picked");
                Check(editor.Editor.Programs[0].Chain.Members.Length==2&&Math.Abs(editor.Editor.Programs[0].Chain.Length-15)<1e-5,"Full native two-edge tangent chain");
                await Pick(editor,CadProjection.Back,new(10,12,3));Check(editor.Editor.Programs.Count==2,"Separate second contour");editor.RadiusInput.Value=.8;await Idle();
                editor.ProgramList.SelectedIndex=0;await Idle();editor.RadiusInput.Value=.5;await Idle();
            }
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;
                Progress(language+" fillet laws");
                await Operate(()=>vm.ContourFilletCommand.ExecuteAsync(null),async editor=>
                {
                    await ConfigureFillet(editor);var program=editor.Editor.Programs[0].Id;
                    foreach(var kind in Enum.GetValues<ContourLawKind>())
                    {
                        editor.LawPicker.SelectedValue=kind;await Idle();Check(editor.Editor.SelectedProgram!.LawKind==kind,"Actual law selector");
                        switch(kind)
                        {
                            case ContourLawKind.Constant:editor.RadiusInput.Value=.5;break;
                            case ContourLawKind.Linear:editor.FirstRadiusInput.Value=.5;editor.LastRadiusInput.Value=1.5;break;
                            case ContourLawKind.Smooth:editor.FirstRadiusInput.Value=.5;editor.LastRadiusInput.Value=1.5;editor.FirstDerivativeInput.Value=.3;editor.LastDerivativeInput.Value=0;break;
                            case ContourLawKind.Sampled:
                                await Cell(editor.SampleGrid,0,2,"0.5");await Cell(editor.SampleGrid,1,2,"1.5");await ClickButton(editor.AddSampleButton);
                                Check(editor.Editor.SelectedProgram.Samples.Count==3,"Actual add sample command");await Cell(editor.SampleGrid,1,2,"1.2");
                                await Cell(editor.SampleGrid,1,1,"0");await Preview(editor);Check(!editor.Editor.CanConfirm,"Non-increasing sample domain rejected");await Cell(editor.SampleGrid,1,1,"0.5");break;
                            case ContourLawKind.Spline:
                                await Cell(editor.PoleGrid,0,1,"0.5");await Cell(editor.PoleGrid,1,1,"1.5");await Cell(editor.PoleGrid,2,1,"1");
                                editor.DegreeInput.Value=3;await Preview(editor);Check(!editor.Editor.CanConfirm,"Invalid spline pole count rejected");editor.DegreeInput.Value=2;break;
                        }
                        await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Check(ReferenceEquals(baseline,document.Session.Snapshot),"Preview leaves author unchanged");
                        Check(editor.Editor.Programs[0].Id==program,"Law changes retain program identity");
                        if(kind==ContourLawKind.Constant)Volume(document,1800-15*(1-Math.PI/4)*(.25+.64));
                        if(kind==ContourLawKind.Linear)Volume(document,1800-15*(1-Math.PI/4)*((.25+.75+2.25)/3+.64),.002);
                        editor.Host.Viewport!.SetProjection(CadProjection.Axonometric);editor.Host.Viewport.FitAll();editor.LawPicker.BringIntoView();await Idle();
                        if(kind is ContourLawKind.Constant or ContourLawKind.Sampled or ContourLawKind.Spline)
                        {Save(editor,Path.Combine(output,$"fillet-{kind}-{language}.png"));editor.Host.Viewport.SaveScreenshot(Path.Combine(output,$"fillet-native-{kind}-{language}.png"));}
                        if(kind==ContourLawKind.Spline){editor.KnotGrid.BringIntoView();await Idle();Save(editor,Path.Combine(output,$"fillet-Spline-tables-{language}.png"));}
                    }
                    await ClickButton(editor.CancelButton);
                });
                Check(ReferenceEquals(baseline,document.Session.Snapshot),"Cancel restores baseline");
            }
            CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("en-US");I18NExtension.Culture=CultureInfo.CurrentUICulture;Progress("confirm tree edit files");
            await Operate(()=>vm.ContourFilletCommand.ExecuteAsync(null),async editor=>{await ConfigureFillet(editor);await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);await Confirm(editor);});
            var accepted=document.Session.Snapshot;var feature=accepted.Features.Values.Single(f=>f.Recipe is ContourLocalRecipe);var ids=((ContourLocalRecipe)feature.Recipe).Programs.Select(p=>p.Id).ToArray();
            await document.Session.UndoAsync();Check(ReferenceEquals(baseline,document.Session.Snapshot),"Single undo");await document.Session.RedoAsync();Check(ReferenceEquals(accepted,document.Session.Snapshot),"Exact redo");
            await Operate(()=>{document.EditFeature(feature.Id);return Task.CompletedTask;},async editor=>
            {
                Check(!editor.SourcePicker.IsEnabled&&editor.Editor.Programs.Select(p=>p.Id).SequenceEqual(ids),"Complete tree editor and stable programs");
                editor.ProgramList.SelectedIndex=0;await Idle();editor.RadiusInput.Value=.6;await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);await Confirm(editor);
            });
            var edited=document.Session.Snapshot;await Operate(()=>{document.EditFeature(feature.Id);return Task.CompletedTask;},async editor=>{await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);editor.Close();});
            Check(ReferenceEquals(edited,document.Session.Snapshot),"Window close releases candidate");
            string path=Path.Combine(output,"accepted-fillet.cadoryx");await document.Session.SaveAsync(storage,path);
            using(var loaded=await storage.LoadAsync(path,document.Session.Assets)){loaded.Snapshot.Validate();Check(((ContourLocalRecipe)loaded.Snapshot.Features[feature.Id].Recipe).Programs.Select(p=>p.Id).SequenceEqual(ids),"Stored programs reverified");}
            vm.NewCommand.Execute(null);await Idle();var chamferDocument=vm.ActiveDocument!;
            await chamferDocument.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,15,RigidTransform3d.Identity),"Chamfer source"));var chamferBaseline=chamferDocument.Session.Snapshot;var chamferSource=chamferBaseline.Bodies.Values.Single();
            async Task ConfigureChamfer(ContourLocalEditorWindow editor)
            {
                editor.SourcePicker.SelectedItem=editor.Editor.Sources.Single(s=>s.Body==chamferSource.Id);await Idle();
                await Pick(editor,CadProjection.Front,new(0,0,3));Check(editor.Editor.WantsSupportFace&&editor.Editor.Programs.Count==0,"Seed then explicit support face");
                await Pick(editor,CadProjection.Left,new(0,6,4));Check(editor.Editor.Programs.Count==1,"Native support selected");editor.DistanceInput.Value=1;await ClickButton(editor.SecondDistanceCheck);editor.SecondDistanceInput.Value=2;
                await Pick(editor,CadProjection.Back,new(10,12,3));await Pick(editor,CadProjection.Right,new(10,6,4));Check(editor.Editor.Programs.Count==2,"Second support contour");
                editor.DistanceInput.Value=1;await ClickButton(editor.SecondDistanceCheck);editor.SecondDistanceInput.Value=2;await Idle();
            }
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;Progress(language+" chamfer");
                await Operate(()=>vm.ContourChamferCommand.ExecuteAsync(null),async editor=>
                {
                    await ConfigureChamfer(editor);await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Volume(chamferDocument,1770);
                    editor.Host.Viewport!.SetProjection(CadProjection.Axonometric);editor.Host.Viewport.FitAll();editor.EditorScroll.ScrollToTop();await Idle();
                    Save(editor,Path.Combine(output,$"chamfer-{language}.png"));editor.Host.Viewport.SaveScreenshot(Path.Combine(output,$"chamfer-native-{language}.png"));
                    await ClickButton(editor.CancelButton);
                });
                Check(ReferenceEquals(chamferBaseline,chamferDocument.Session.Snapshot),"Chamfer cancel baseline");
            }
            CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("en-US");I18NExtension.Culture=CultureInfo.CurrentUICulture;
            await Operate(()=>vm.ContourChamferCommand.ExecuteAsync(null),async editor=>{await ConfigureChamfer(editor);await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);await Confirm(editor);});
            path=Path.Combine(output,"accepted-chamfer.cadoryx");await chamferDocument.Session.SaveAsync(storage,path);using(var loaded=await storage.LoadAsync(path,chamferDocument.Session.Assets))Check(Math.Abs(loaded.Snapshot.Bodies.Values.Single().Geometry.VolumeMm3-1770)<1e-5,"Chamfer reopen");
            Progress("cleanup");Check(await vm.CloseAllAsync(),"Close documents");await ((App)Application.Current).StopRecoveryAsync();await Idle();Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Zero assets");listener.Flush();bindings.Flush();
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+"E3 production contour windows en-US/zh-CN/ja-JP; native viewport API edge/support picking, full two-edge tangent chain, independent multiple contours, all five radius laws, actual scalar/table/add-row controls, malformed sample/spline rejection, exact analytic volumes, preview/cancel/close, confirm, single undo/redo, stable complete tree edit, native save/reopen and zero assets. Physical pointer NOT VERIFIED.");window.CloseAfterSmoke();
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+ex);Application.Current.Shutdown(1);}
        finally{CultureInfo.CurrentUICulture=culture;I18NExtension.Culture=markupCulture;PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private static async Task Cell(DataGrid grid,int row,int column,string value)
    {
        var item=grid.Items[row];var field=grid.Columns[column];grid.ScrollIntoView(item,field);grid.SelectedItem=item;grid.CurrentCell=new(item,field);await Idle();
        Check(grid.BeginEdit(),"DataGrid enters edit");await Idle();var editor=field.GetCellContent(item) as TextBox??throw new InvalidOperationException("DataGrid text editor missing");
        editor.Text=value;editor.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();Check(grid.CommitEdit(DataGridEditingUnit.Cell,true),"Commit table cell");Check(grid.CommitEdit(DataGridEditingUnit.Row,true),"Commit table row");await Idle();
    }
    private static async Task Pick(ContourLocalEditorWindow editor,CadProjection projection,Vector3d point)
    {
        await Idle();var viewport=editor.Host.Viewport??throw new InvalidOperationException("Native viewport unavailable");viewport.SetProjection(projection);viewport.FitAll();await Idle();
        var at=viewport.WorldToScreen(point);viewport.PointerPressed(0,at.X,at.Y,0);viewport.PointerReleased(0,at.X,at.Y,0);await Idle();if(editor.Editor.SelectionTask is {} task)await task;await Idle();
    }
    private static async Task Preview(ContourLocalEditorWindow editor){await ClickButton(editor.PreviewButton);await (editor.Editor.PreviewCommand.ExecutionTask??throw new InvalidOperationException("Preview did not execute"));await Idle();}
    private static async Task Confirm(ContourLocalEditorWindow editor){await ClickButton(editor.ConfirmButton);await (editor.Editor.ConfirmCommand.ExecutionTask??throw new InvalidOperationException("Confirm did not execute"));}
    private static async Task Operate(Func<Task> open,Func<ContourLocalEditorWindow,Task> action)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _=Application.Current.Dispatcher.InvokeAsync(async()=>
        {
            ContourLocalEditorWindow? editor=null;
            try
            {
                var deadline=DateTime.UtcNow.AddSeconds(15);
                do{await Idle();editor=Application.Current.Windows.OfType<ContourLocalEditorWindow>().SingleOrDefault();if(editor is {IsLoaded:true,IsVisible:true,Host.Viewport:not null})break;if(DateTime.UtcNow>=deadline)throw new InvalidOperationException("Contour window not initialized: "+((MainWindowViewModel)Application.Current.MainWindow.DataContext).StatusText);}while(true);
                editor.Activate();await Idle();await action(editor);completion.TrySetResult();
            }
            catch(Exception ex){completion.TrySetException(ex);}
            finally{if(editor is {IsVisible:true})editor.Close();}
        },DispatcherPriority.ApplicationIdle);
        await open();await completion.Task;
    }
    private static void Volume(CadDocumentViewModel document,double expected,double tolerance=1e-5)=>Check(document.PreviewScene!.Items.Any(i=>Math.Abs(i.Geometry.VolumeMm3-expected)<tolerance),"Exact native volume "+expected);
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
