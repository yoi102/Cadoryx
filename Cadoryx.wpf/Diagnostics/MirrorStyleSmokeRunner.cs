using System.IO;
using System.Windows;
using System.Windows.Media;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Views;
using OcctSharp;
using static Cadoryx.wpf.Diagnostics.SketchEditorSmokeRunner;

namespace Cadoryx.wpf.Diagnostics;

internal static class MirrorStyleSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,MainWindowViewModel vm,IDocumentStorage storage,string output)
    {
        var input=Path.Combine(output,"two-face-source.step");
        using(var native=XdeDocument.Create())using(var box=ShapeFactory.CreateBox(12,9,6))
        {
            using(var edit=native.BeginTransaction("Face color fixture"))
            {
                var part=native.AddShape(box,"RGB source");part.Color=new(0,0,1);
                using var repair=new RepairDocumentSession(native,part);
                foreach(var item in repair.Source.Topology.Where(t=>t.Kind==ShapeKind.Face))
                {
                    using var face=repair.Source.CopySubshape(item.Selection);var bounds=face.GetBoundingBox();
                    if(Math.Abs(bounds.Maximum.Z-bounds.Minimum.Z)<1e-6&&Math.Abs(bounds.Maximum.Z-6)<1e-6)
                        repair.GetOrCreateSubshapeLabel(item.Selection).Color=new(1,0,0);
                    if(Math.Abs(bounds.Maximum.X-bounds.Minimum.X)<1e-6&&Math.Abs(bounds.Maximum.X-12)<1e-6)
                        repair.GetOrCreateSubshapeLabel(item.Selection).Color=new(0,1,0);
                }
                edit.Commit();
            }
            native.WriteStep(input);
        }
        await vm.OpenPathAsync(input);await Idle();var document=vm.ActiveDocument??throw new InvalidOperationException("Colored source did not open.");
        var source=document.Session.Snapshot.EnumerateOccurrences().Single(o=>document.Session.Snapshot.Definitions[o.DefinitionId] is PartDefinition).Path;
        var placement=OccurrencePlacement.Resolve(document.Session.Snapshot,source);
        var pattern=new AssemblyRepetition(Guid.NewGuid(),placement.OwnerId,placement.Slot.Id,"Colored reflection",RepetitionKind.Mirror,
            RigidTransform3d.Translate(0,0,10),2,0,[ComponentSlotId.New()]);
        await document.Session.ExecuteAsync(AssemblyRepetitionCommands.Set(source,pattern));await Idle();
        var host=WindowSmokeRunner.Host(document);var viewport=host.Viewport!;
        var pane=Ancestor<ViewportPane>(host)??throw new InvalidOperationException("Missing colored viewport pane.");
        var reflected=document.Scene.Items.Single(i=>i.Path.Slots.Contains(pattern.CopySlots.Single()));
        if(!reflected.PreserveSourceStyles||reflected.Geometry.Source is null)throw new InvalidOperationException("Reflected XDE style context missing.");
        // Show only the reflected result, so source pixels cannot mask a loss of its styles.
        pane.ShowSceneForSmoke(document.Scene with{Items=[reflected]});viewport.FitAll();
        WindowSmokeRunner.CaptureColors(viewport,output,"mirror-face-styles");
        document.Selection.Replace([new(reflected.Path,reflected.BodyId,reflected.Geometry.Revision)]);await Idle();
        viewport.SaveScreenshot(Path.Combine(output,"mirror-face-selected.png"));
        document.Selection.Replace([]);await Idle();WindowSmokeRunner.CaptureColors(viewport,output,"mirror-face-deselected");
        pane.ShowSceneForSmoke(document.Scene);var saved=Path.Combine(output,"accepted-colored-mirror.cadoryx");await document.Session.SaveAsync(storage,saved);
        using(var reopened=await storage.LoadAsync(saved,document.Session.Assets))
            if(reopened.Snapshot.Bodies[reflected.BodyId].Geometry.Source!=reflected.Geometry.Source)throw new InvalidOperationException("Saved reflected style identity changed.");
    }
    private static T? Ancestor<T>(DependencyObject child) where T:DependencyObject
    {for(DependencyObject? node=child;node is not null;node=VisualTreeHelper.GetParent(node))if(node is T value)return value;return null;}
}
