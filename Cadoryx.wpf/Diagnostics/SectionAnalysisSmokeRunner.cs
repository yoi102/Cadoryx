using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AvalonDock.Core;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Controls;
using Cadoryx.wpf.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Cadoryx.wpf.Diagnostics;

internal static class SectionAnalysisSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,MainWindowViewModel workspace,IServiceProvider services,string output)
    {
        var document=workspace.ActiveDocument!;var before=document.Session.Snapshot;
        var original=before.Bodies.Values.Single();var root=(AssemblyDefinition)before.Definitions[before.RootAssemblyId];
        double offset=(original.Geometry.Bounds.Max.X-original.Geometry.Bounds.Min.X)/2;
        await document.Session.ExecuteAsync(new EditDocumentCommand("Interference fixture",s=>s with
        {Definitions=s.Definitions.SetItem(root.Id,root with{Children=root.Children.Add(new(ComponentSlotId.New(),original.PartId,"Overlap instance",RigidTransform3d.Translate(offset,0,0)))})}));
        await Idle();var source=document.Session.Snapshot;
        document.Selection.Replace(document.Scene.Items.Select(i=>new SelectionTarget(i.Path,i.BodyId,i.Geometry.Revision)));
        workspace.LayoutService.ShowAnchorable(workspace.Properties);await Idle();var view=Find<DocumentReviewView>(window)!;
        Require(view.CheckInterferenceButton.Command==document.Review.CheckInterferenceCommand,"Interference button binding failed.");
        await document.Review.CheckInterferenceCommand.ExecuteAsync(null);
        var finding=document.Review.InterferenceRows.Single();Require(finding.Finding.Relation==BodyPairRelation.Interfering&&finding.Finding.OverlapVolumeMm3>0,"Native interference result missing.");
        document.Review.SelectedInterference=finding;document.Review.LocateInterferenceCommand.Execute(null);await Task.Delay(400);await Idle();
        Require(document.Selection.Items.Length==2&&document.Review.InterferenceRows.Count==1,"Pair locate lost identity or report.");
        Require(document.Session.Snapshot.StateId==source.StateId,"Inspection mutated the document.");
        var previousName=document.Review.SectionName;
        document.Review.SectionOffsetMm=(original.Geometry.Bounds.Min.Z+original.Geometry.Bounds.Max.Z)/2;
        document.Review.SectionName="Smoke section";
        Require(view.CreateSectionButton.Command==document.Review.CreateSectionCommand,"Section button binding failed.");
        await document.Review.CreateSectionCommand.ExecuteAsync(null);await Idle();
        var section=document.Session.Snapshot.Bodies.Values.Single(b=>b.Geometry.Kind==BodyKind.Wire);
        var storage=services.GetRequiredService<IDocumentStorage>();var kernel=services.GetRequiredService<IGeometryKernel>();
        await storage.SaveAsync(document.Session.Snapshot,document.Session.Assets,Path.Combine(output,"section-curves.cadoryx"));
        using(var loaded=await storage.LoadAsync(Path.Combine(output,"section-curves.cadoryx"),document.Session.Assets))
            Require(loaded.Snapshot.Bodies[section.Id]==section,"Section did not survive document reload.");
        var curves=document.Session.Snapshot with{Bodies=document.Session.Snapshot.Bodies.ToImmutableDictionary(p=>p.Key,p=>p.Value with{IsVisible=p.Key==section.Id})};
        foreach(var extension in new[]{"step","iges"})await kernel.ExportAsync(curves,document.Session.Assets,Path.Combine(output,"section-curves."+extension));
        var item=document.Scene.Items.Single(i=>i.BodyId==section.Id);document.Selection.Replace([new(item.Path,item.BodyId,item.Geometry.Revision)]);
        document.Review.IsolateCommand.Execute(null);document.Review.ResetSectionCommand.Execute(null);await Idle();
        var viewport=Find<OcctViewportHost>(window)!.Viewport!;viewport.FitAll();viewport.SaveScreenshot(Path.Combine(output,"section-curves.png"));
        var created=document.Session.Snapshot;await document.Session.UndoAsync();Require(document.Session.Snapshot.StateId==source.StateId,"Section undo is not atomic.");
        await document.Session.RedoAsync();Require(document.Session.Snapshot.StateId==created.StateId,"Section redo lost exact state.");
        await document.Session.UndoAsync();await document.Session.UndoAsync();document.Review.ShowAll();document.Selection.Replace([]);document.Review.SectionName=previousName;await Idle();
        Require(document.Session.Snapshot.StateId==before.StateId,"Smoke did not restore its fixture.");
        await File.WriteAllTextAsync(Path.Combine(output,"section-analysis-result.txt"),"PASS: native volume interference, UI command bindings and exact pair locate, section geometry creation in independent part, document reload, STEP/IGES curve export, wire viewport screenshot, atomic undo/redo, original state restored.");
    }
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static async Task Idle(){await Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);await Task.Delay(120);}
    private static T? Find<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T item)return item;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)if(Find<T>(VisualTreeHelper.GetChild(root,i)) is {} child)return child;
        return null;
    }
}
