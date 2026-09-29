using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Controls;
using Cadoryx.wpf.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Cadoryx.wpf.Diagnostics;

internal static class M10AssemblyWindowSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        using var bindingOutput=new StreamWriter(Path.Combine(output,"bindings.log"));
        using var listener=new System.Diagnostics.TextWriterTraceListener(bindingOutput);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level=System.Diagnostics.SourceLevels.Error;
        try
        {
            await Idle();var vm=(MainWindowViewModel)window.DataContext;
            var document=vm.ActiveDocument??throw new InvalidOperationException("No document.");
            await document.Session.ExecuteAsync(new AddBodyCommand(
                new BoxRecipe(10,10,10,RigidTransform3d.Identity),"M10 box"));
            var snapshot=document.Session.Snapshot;
            var part=snapshot.Definitions.Values.OfType<PartDefinition>().Single();
            var a=snapshot.EnumerateOccurrences().Single(o=>o.DefinitionId==part.Id).Path;
            var root=new OccurrencePath(snapshot.Id,[]);var second=ComponentSlotId.New();
            await document.Session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(root,part.Id,second,
                "Moving box",RigidTransform3d.Translate(30,0,0)));
            await Idle();
            var viewport=Find<OcctViewportHost>(window)?.Viewport??
                throw new InvalidOperationException("Native viewport is unavailable.");
            viewport.SetProjection(Cadoryx.Rendering.CadProjection.Top);viewport.FitAll();
            var relationVm=document.AssemblyConstraints;
            async Task Pick(bool reference,Vector3d world)
            {
                relationVm.DatumPickKind=TopologyKind.Face;
                if(reference)relationVm.PickReferenceDatumCommand.Execute(null);
                else relationVm.PickMovingDatumCommand.Execute(null);
                var pixel=viewport.WorldToScreen(world);
                viewport.PointerPressed(0,pixel.X,pixel.Y,0);
                viewport.PointerReleased(0,pixel.X,pixel.Y,0);
                for(int i=0;i<80;i++)
                {
                    await Idle();
                    if((reference?relationVm.ReferenceDatumLabel:relationVm.MovingDatumLabel)!="—")return;
                    await Task.Delay(25);
                }
                throw new InvalidOperationException("Native analytic datum pick did not reach the relation editor: "+relationVm.Status);
            }
            await Pick(true,new(5,5,10));
            await Pick(false,new(35,5,10));
            relationVm.DatumRelationKind=AssemblyConstraintKind.Coincident;
            await relationVm.AddDatumRelationCommand.ExecuteAsync(null);
            var relation=AssertSingle(document.Session.Snapshot);
            if(relation.PrimaryDatum is null||relation.SecondaryDatum is null||relation.SchemaVersion!=2)
                throw new InvalidOperationException("Geometric mate was not committed with exact BRep datums.");
            var beforeSolve=document.Session.Snapshot;
            await relationVm.PreviewSolveCommand.ExecuteAsync(null);
            if(!relationVm.HasSolvePreview||document.PreviewScene is null||
               !ReferenceEquals(beforeSolve,document.Session.Snapshot))
                throw new InvalidOperationException("Assembly preview did not preserve the document candidate boundary: "+relationVm.Status);
            relationVm.CancelSolvePreviewCommand.Execute(null);
            if(relationVm.HasSolvePreview||document.PreviewScene is not null||
               !ReferenceEquals(beforeSolve,document.Session.Snapshot))
                throw new InvalidOperationException("Cancel did not clear the uncommitted assembly preview.");
            await relationVm.PreviewSolveCommand.ExecuteAsync(null);
            await relationVm.ConfirmSolveCommand.ExecuteAsync(null);
            relation=AssertSingle(document.Session.Snapshot);
            if(relation.Evaluate(document.Session.Snapshot).Status!=AssemblyConstraintStatus.Satisfied)
                throw new InvalidOperationException("Viewport-created mate was not solved: "+relationVm.Status);
            var saved=Path.Combine(output,"m10-window.cadoryx");
            await document.Session.SaveAsync(services.GetRequiredService<IDocumentStorage>(),saved);
            using(var loaded=await services.GetRequiredService<IDocumentStorage>().LoadAsync(saved,document.Session.Assets))
                if(AssertSingle(loaded.Snapshot).PrimaryDatum?.Fingerprint!=relation.PrimaryDatum!.Fingerprint)
                    throw new InvalidOperationException("BRep datum did not roundtrip.");
            viewport.SaveScreenshot(Path.Combine(output,"assembly.png"));
            bindingOutput.Flush();
            if(new FileInfo(Path.Combine(output,"bindings.log")).Length!=0)
                throw new InvalidOperationException("Window has XAML binding errors.");
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new
                {passed=true,relation=relation.Id.ToString(),saved},new JsonSerializerOptions{WriteIndented=true}));
            Application.Current.Shutdown(0);
        }
        catch(Exception error)
        {
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new
                {passed=false,error=error.ToString()},new JsonSerializerOptions{WriteIndented=true}));
            Application.Current.Shutdown(1);
        }
        finally{PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private static AssemblyConstraint AssertSingle(DocumentSnapshot document)=>
        document.AssemblyConstraints.Values.Single();
    private static T? Find<T>(DependencyObject parent) where T:DependencyObject
    {
        if(parent is T found)return found;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
            if(Find<T>(VisualTreeHelper.GetChild(parent,i)) is {} match)return match;
        return null;
    }
    private static async Task Idle()=>await Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
}
