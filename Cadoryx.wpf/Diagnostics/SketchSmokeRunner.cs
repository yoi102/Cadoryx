using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace Cadoryx.wpf.Diagnostics;

internal static class SketchSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output)
    {
        var vm=(MainWindowViewModel)window.DataContext;var document=vm.ActiveDocument!;var session=document.Session;
        var solver=services.GetRequiredService<ISketchConstraintSolver>();var storage=services.GetRequiredService<IDocumentStorage>();
        var part=DefinitionId.New();await session.ExecuteAsync(ResourceCommands.AddPart(part,"Sketch verification"));
        var sketch=Rectangle(part);var report=await solver.SolveAsync(sketch);
        Check(report.Succeeded&&report.DegreesOfFreedom==0,"Constrained rectangle solves");
        await session.ExecuteAsync(new UpsertSketchCommand(sketch,solver));var before=session.Snapshot;
        var width=(OffsetXConstraint)before.Sketches[sketch.Id].Constraints.Single(c=>c is OffsetXConstraint);
        var edited=before.Sketches[sketch.Id] with{Constraints=before.Sketches[sketch.Id].Constraints.Replace(width,width with{Offset=60})};
        await session.ExecuteAsync(new UpsertSketchCommand(edited,solver));var after=session.Snapshot;
        await session.UndoAsync();Check(ReferenceEquals(before,session.Snapshot),"Exact sketch undo");await session.RedoAsync();Check(ReferenceEquals(after,session.Snapshot),"Exact sketch redo");
        var solved=after.Sketches[sketch.Id];
        var conflict=solved with{Constraints=solved.Constraints.Add(width with{Id=SketchConstraintId.New(),Offset=90})};
        try{await session.ExecuteAsync(new UpsertSketchCommand(conflict,solver));throw new InvalidOperationException("Conflicting sketch committed");}
        catch(SketchSolveException ex){Check(ex.Report.Status==SketchSolveStatus.Inconsistent&&ReferenceEquals(after,session.Snapshot),"Conflict preserves committed state");}
        var profile=SketchProfileBuilder.Polygon(solved,solved.Lines.Select(l=>l.Id));
        await session.ExecuteAsync(new AddBodyCommand(new ExtrudeRecipe(profile,10,solved.Plane),"Solved sketch extrusion",part));
        var body=session.Snapshot.Bodies.Values.Single(b=>b.PartId==part);Check(Math.Abs(body.Geometry.VolumeMm3-18000)<1e-4,"Native extrusion volume");
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);await Task.Delay(150);
        var host=Find<OcctViewportHost>(window)??throw new InvalidOperationException("Sketch result viewport missing");
        host.Viewport!.FitAll();host.Viewport.SaveScreenshot(Path.Combine(output,"sketch-extrusion.png"));
        string path=Path.Combine(output,"sketches.cadoryx");await session.SaveAsync(storage,path);
        using(var loaded=await storage.LoadAsync(path,session.Assets))
        {Check(Describe(solved)==Describe(loaded.Snapshot.Sketches[solved.Id]),"Sketch protocol roundtrip");Check(loaded.Snapshot.Bodies[body.Id]==body,"Extrusion roundtrip");}
        await File.WriteAllTextAsync(Path.Combine(output,"sketch-result.json"),JsonSerializer.Serialize(new{solver=solver.Version,report.DegreesOfFreedom,report.VariableCount,report.EquationCount,
            volumeMm3=body.Geometry.VolumeMm3,sketchId=solved.Id,featureMode="frozen-profile",undoRedo=true,conflictPreservedState=true,saveReopen=true},new JsonSerializerOptions{WriteIndented=true}));
    }
    internal static CadSketch Rectangle(DefinitionId part)
    {
        var p=new[]{new Point2d(1,2),new(12,1),new(11,7),new(2,9)}.Select(p=>new SketchPoint(SketchEntityId.New(),p)).ToImmutableArray();
        var l=Enumerable.Range(0,4).Select(i=>new SketchLine(SketchEntityId.New(),p[i].Id,p[(i+1)%4].Id)).ToImmutableArray();
        return new(SketchId.New(),part,"Constrained rectangle",RigidTransform3d.Translate(100,0,0),p,l,[],[
            new FixPointConstraint(SketchConstraintId.New(),p[0].Id,new(0,0)),new HorizontalConstraint(SketchConstraintId.New(),l[0].Id),new VerticalConstraint(SketchConstraintId.New(),l[1].Id),
            new HorizontalConstraint(SketchConstraintId.New(),l[2].Id),new VerticalConstraint(SketchConstraintId.New(),l[3].Id),
            new OffsetXConstraint(SketchConstraintId.New(),p[0].Id,p[1].Id,40),new OffsetYConstraint(SketchConstraintId.New(),p[0].Id,p[3].Id,30)]);
    }
    internal static string Describe(CadSketch sketch)=>JsonSerializer.Serialize(new{sketch.Id,sketch.Revision,sketch.PartId,sketch.Name,sketch.Plane,sketch.Points,sketch.Lines,sketch.Circles,
        Constraints=sketch.Constraints.Select(c=>new{Kind=c.GetType().Name,Value=JsonSerializer.SerializeToElement(c,c.GetType(),CadJson.Options)}).ToArray()},CadJson.Options);
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    private static T? Find<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T found)return found;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)if(Find<T>(VisualTreeHelper.GetChild(root,i)) is {} child)return child;
        return null;
    }
}
