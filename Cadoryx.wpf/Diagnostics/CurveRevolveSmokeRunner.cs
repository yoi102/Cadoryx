using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Antelcat.I18N.WPF;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Rendering;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Views;
using Cadoryx.wpf.Views.Toolboxes;
using MahApps.Metro.Controls;
using Microsoft.Extensions.DependencyInjection;
using static Cadoryx.wpf.Diagnostics.SketchEditorSmokeRunner;

namespace Cadoryx.wpf.Diagnostics;

internal static class CurveRevolveSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));using var listener=new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        var original=CultureInfo.CurrentUICulture;var main=(MainWindowViewModel)window.DataContext;
        var markup=CultureInfo.GetCultureInfo(main.CurrentCultureLCID);
        void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        void Progress(string value)=>File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {value}{Environment.NewLine}");
        try
        {
            await Idle();var document=main.ActiveDocument!;
            await document.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(1,1,1,RigidTransform3d.Translate(30,0,0)),"Support"));
            var part=document.Session.Snapshot.Features.Values.Single().PartId;
            var curve=new SketchDraft(CadSketch.Create(part,"Curves",RigidTransform3d.Identity));
            curve.AddBezier(new(8,0),new(10,2),new(12,0));curve.AddSpline([new(8,6),new(28d/3,8),new(32d/3,8),new(12,6)]);
            var region=new SketchDraft(CadSketch.Create(part,"Nested regions",RigidTransform3d.Identity));
            foreach(double radius in new[]{5d,3,2,1,.5})region.AddCircle(new(10,0),radius);
            await document.Session.ExecuteAsync(new UpsertSketchCommand(curve.Value,new ManagedSketchConstraintSolver()));
            await document.Session.ExecuteAsync(new UpsertSketchCommand(region.Value,new ManagedSketchConstraintSolver()));
            var baseline=document.Session.Snapshot;var storage=services.GetRequiredService<IDocumentStorage>();
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})foreach(string kind in new[]{"bezier","spline","regions"})
            {
                Progress($"{language} {kind}: production controls");
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;
                document.SelectedTargetPart=part;document.SelectedSketchId=kind=="regions"?region.Value.Id:curve.Value.Id;document.StartSketchFeature("Revolve");
                var view=new ModelingToolboxView{DataContext=new{Document=document}};
                var host=new MetroWindow{Title=Cadoryx.Lang.Strings.Strings.PolygonRevolve,Width=760,Height=Math.Min(960,SystemParameters.WorkArea.Height),
                    Owner=window,Content=view,WindowStartupLocation=WindowStartupLocation.CenterOwner};
                try
                {
                    host.Show();host.Activate();await Idle();var selector=(ComboBox)view.FindName("SketchProfileInput");
                    selector.SelectedItem=document.SketchProfiles.Single(p=>kind switch
                    {
                        "bezier"=>p.Source.BezierId is not null,"spline"=>p.Source.SplineId is not null,
                        _=>p.Source.CircleId==region.Value.Circles[0].Id&&p.Source.HoleCircleIds.IsEmpty
                    });
                    ((NumericUpDown)view.FindName("RevolveAngleInput")).Value=180;await Idle();
                    if(kind=="regions")
                    {
                        var expander=(Expander)view.FindName("ProfileRegionsExpander");expander.IsExpanded=true;
                        expander.BringIntoView();host.UpdateLayout();await Idle();
                        var hole=document.SketchHoleChoices.Single(c=>c.Id==region.Value.Circles[1].Id);
                        var toggles=Descendants<CheckBox>(expander).Where(c=>ReferenceEquals(c.DataContext,hole)).ToArray();
                        if(toggles.Length!=1)Save(host,Path.Combine(output,$"missing-hole-toggle-{language}.png"));
                        Check(toggles.Length==1,$"Expected one realized hole Toggle; found {toggles.Length}, choices {document.SketchHoleChoices.Count}, expanded {expander.IsExpanded}, visible {expander.IsVisible}");
                        await ClickButton(toggles[0]);
                        Check(hole.IsSelected,"Production hole Toggle did not apply");
                        await ClickButton((Button)view.FindName("AutoNestRegionsButton"));
                        Check(!string.IsNullOrEmpty(document.AutoNestStatus)&&!document.SketchHoleChoices.Any(c=>c.IsSelected),"Nested selection button did not replace manual hole selection");
                    }
                    async Task Preview()
                    {
                        await ClickButton((Button)view.FindName("ModelingPreviewButton"));if(document.PreviewCommand.ExecutionTask is {} task)await task;
                        await Idle();Check(document.HasPreview,document.ToolStatus);Check(ReferenceEquals(baseline,document.Session.Snapshot),"Preview mutated document");
                    }
                    await Preview();double expected=kind switch{"bezier"=>80*Math.PI/3,"spline"=>40*Math.PI,_=>192.5*Math.PI*Math.PI};
                    Check(Math.Abs(document.PreviewGeometry!.VolumeMm3-expected)<.002,"Native exact curve/region preview volume");
                    Check(document.PreviewGeometry.Kind==(kind=="regions"?BodyKind.Compound:BodyKind.Solid),"Native region output kind");
                    selector.BringIntoView();await Idle();Save(host,Path.Combine(output,$"revolve-{kind}-{language}.png"));
                    var viewport=WindowSmokeRunner.Host(document).Viewport!;viewport.SetProjection(CadProjection.Axonometric);viewport.FitAll();await Idle();
                    viewport.SaveScreenshot(Path.Combine(output,$"native-{kind}-{language}.png"));
                    await ClickButton((Button)view.FindName("ModelingCancelButton"));Check(!document.HasPreview&&ReferenceEquals(baseline,document.Session.Snapshot),"Actual cancel changed committed state");
                    // Cancel retains the selected recipe controls; confirm a fresh preview through the same controls.
                    await Preview();await ClickButton((Button)view.FindName("ModelingConfirmButton"));if(document.ConfirmCommand.ExecutionTask is {} confirm)await confirm;
                    var feature=document.Session.Snapshot.Features.Values.Single(f=>!baseline.Features.ContainsKey(f.Id));
                    Check(feature.SketchSource is not null&&Math.Abs(feature.Result.VolumeMm3-expected)<.002,"Actual confirm lost profile source/volume");
                    var saved=Path.Combine(output,$"accepted-{kind}-{language}.cadoryx");await document.Session.SaveAsync(storage,saved);
                    using(var loaded=await storage.LoadAsync(saved,document.Session.Assets))
                    {
                        loaded.Snapshot.Validate();Check(SketchProfileReference.SameProfile(((RevolveRecipe)feature.Recipe).Profile,((RevolveRecipe)loaded.Snapshot.Features[feature.Id].Recipe).Profile),"Roundtrip altered complete curve/region recipe");
                    }
                    await document.Session.UndoAsync();Check(ReferenceEquals(baseline,document.Session.Snapshot),"One undo did not restore baseline");
                    await document.Session.RedoAsync();Check(document.Session.Snapshot.Features.ContainsKey(feature.Id),"One redo missing revolution");await document.Session.UndoAsync();
                }
                finally{document.CancelCommand.Execute(null);host.Close();}
            }
            Progress("Save baseline after final undo before closing documents");
            await document.Session.SaveAsync(storage,Path.Combine(output,"final-baseline.cadoryx"));
            Progress("Close documents and stop recovery");
            Check(await main.CloseAllAsync(),"Close documents");await ((App)Application.Current).StopRecoveryAsync();await Idle();
            Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Native candidate assets not released");listener.Flush();bindings.Flush();
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+"B8 production exact Bezier/spline and nested-region revolve in en-US/zh-CN/ja-JP; actual profile/angle controls, manual hole Toggle and explicit nesting button, native volumes and compound identity, preview/cancel/confirm, one undo/redo, save/reopen and zero remaining assets. Layout and native viewport screenshots are separate; physical pointer remains unverified.");
            window.CloseAfterSmoke();
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+ex);Application.Current.Shutdown(1);}
        finally{CultureInfo.CurrentUICulture=original;I18NExtension.Culture=markup;PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=VisualTreeHelper.GetChild(parent,i);if(child is T value)yield return value;
            foreach(var nested in Descendants<T>(child))yield return nested;
        }
    }
}
