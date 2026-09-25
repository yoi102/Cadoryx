using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cadoryx.Db;
using Cadoryx.Commands;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform;
using Cadoryx.wpf.Controls;
using Cadoryx.wpf.Views;
using Cadoryx.wpf.Views.Dialogs;
using Cadoryx.wpf.Views.Settings;
using MahApps.Metro.Controls;
using AvalonDock.Core;
using MaterialDesignThemes.Wpf;

namespace Cadoryx.wpf.Diagnostics;

internal static class ResourceSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,MainWindowViewModel workspace,IDocumentStorage storage,string output)
    {
        var document=workspace.ActiveDocument!;int originalBodies=document.Session.Snapshot.Bodies.Count;
        DefinitionId target=default;LayerId layer=default;MaterialId material=default;
        await ModalAsync(()=>workspace.ManageResourcesCommand.ExecuteAsync(null),async()=>
        {
            var dialog=DialogHost.GetDialogSession(ViewServiceIdentifiers.RootDialogHost)?.Content as DocumentResourcesDialog??throw new InvalidOperationException("Resource dialog was not shown.");
            var vm=(DocumentResourcesViewModel)dialog.DataContext;
            try
            {
                dialog.PartNameInput.SetCurrentValue(TextBox.TextProperty,"Housing");await vm.AddPartCommand.ExecuteAsync(null);
                target=vm.SelectedPart!.Id;Require(document.SelectedTargetPart==target,"Added part did not become the target.");
                dialog.ResourceTabs.SelectedIndex=1;await Idle();
                dialog.LayerNameInput.SetCurrentValue(TextBox.TextProperty,"Finishing");vm.LayerArgb=0xFFED9A4B;
                await vm.AddLayerCommand.ExecuteAsync(null);layer=vm.SelectedLayer!.Id;
                dialog.ResourceTabs.SelectedIndex=2;await Idle();
                dialog.MaterialNameInput.SetCurrentValue(TextBox.TextProperty,"Aluminium");dialog.DensityInput.SetCurrentValue(MahApps.Metro.Controls.NumericUpDown.ValueProperty,2700d);
                await vm.AddMaterialCommand.ExecuteAsync(null);material=vm.SelectedMaterial!.Id;
                Require(Math.Abs(document.Session.Snapshot.Materials[material].DensityKgPerMm3-2.7e-6)<1e-12,"Density binding or unit conversion failed.");
                var culture=System.Globalization.CultureInfo.GetCultureInfo(workspace.CurrentCultureLCID);
                try
                {
                    foreach(var language in new[]{"en-US","zh-CN","ja-JP"})
                    {
                        Antelcat.I18N.WPF.I18NExtension.Culture=System.Globalization.CultureInfo.GetCultureInfo(language);
                        for(int tab=0;tab<3;tab++){dialog.ResourceTabs.SelectedIndex=tab;await Idle();Capture(dialog,Path.Combine(output,$"resources-{language}-{tab}.png"));}
                    }
                }
                finally{Antelcat.I18N.WPF.I18NExtension.Culture=culture;}
            }
            finally{DialogHost.GetDialogSession(ViewServiceIdentifiers.RootDialogHost)?.Close();}
        });
        document.SelectedCreationLayer=layer;document.SelectedCreationMaterial=material;document.StartTool("Box");
        document.SelectedTargetPart=target;await document.PreviewCommand.ExecuteAsync(null);Require(document.HasPreview,document.ToolStatus);
        await document.ConfirmCommand.ExecuteAsync(null);await Idle();
        var body=document.Session.Snapshot.Bodies.Values.Single(b=>b.PartId==target);
        Require(document.Session.Snapshot.Bodies.Count==originalBodies+1&&body.LayerId==layer&&body.MaterialId==material,"Creation target bindings were not committed.");
        var occurrence=document.Session.Snapshot.EnumerateOccurrences().Single(o=>o.DefinitionId==target);
        workspace.ModelTree.Select(workspace.ModelTree.Items.Single(i=>Equals(i.Path,occurrence.Path)));await Idle();
        workspace.LayoutService.ShowAnchorable(workspace.Properties);await Idle();
        var placement=Find<InstancePlacementView>(window)??throw new InvalidOperationException("Instance position UI is missing.");
        placement.XInput.SetCurrentValue(MahApps.Metro.Controls.NumericUpDown.ValueProperty,80d);placement.AngleInput.SetCurrentValue(MahApps.Metro.Controls.NumericUpDown.ValueProperty,30d);
        Require(document.Placement.X==80&&document.Placement.AngleDegrees==30,"Position UI bindings failed.");
        var before=document.Session.Snapshot;await document.Placement.ApplyCommand.ExecuteAsync(null);await Idle();
        Require(OccurrencePlacement.Resolve(document.Session.Snapshot,occurrence.Path).Slot.LocalTransform.Translation.X==80,"Position did not commit.");
        await document.Session.UndoAsync();Require(document.Session.Snapshot.StateId==before.StateId,"Position undo was not exact.");await document.Session.RedoAsync();
        Require(placement.CreateAssemblyButton.Command is not null,"Create assembly UI is unavailable.");
        document.Placement.NewAssemblyName="Shared group";
        await document.Placement.CreateAssemblyCommand.ExecuteAsync(null);await Idle();
        var leftPath=document.Selection.Occurrence!;var left=leftPath.Slots[^1];
        var sharedGroup=OccurrencePlacement.Resolve(document.Session.Snapshot,leftPath).Slot.DefinitionId;
        document.Placement.SelectedDefinition=document.Placement.Definitions.Single(d=>d.Id==target);
        await document.Placement.InsertCommand.ExecuteAsync(null);await Idle();
        await document.Session.ExecuteAsync(DocumentEdits.MoveOccurrence(leftPath,RigidTransform3d.Translate(12,0,0)));
        var right=ComponentSlotId.New();
        await document.Session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(new(document.Session.Snapshot.Id,[]),sharedGroup,right,
            "Right group",RigidTransform3d.Translate(100,0,0)));
        document.Selection.SelectOccurrence(leftPath);await Idle();
        Require(document.Placement.CanMakeIndependent&&placement.IndependentButton.IsEnabled&&placement.IndependentButton.Command is not null,
            "Shared assembly independence UI is unavailable.");
        Capture(placement,Path.Combine(output,"assembly-instance-controls.png"));
        await document.Placement.MakeIndependentCommand.ExecuteAsync(null);await Idle();
        var isolated=OccurrencePlacement.Resolve(document.Session.Snapshot,leftPath).Slot.DefinitionId;
        Require(isolated!=sharedGroup&&OccurrencePlacement.Resolve(document.Session.Snapshot,
            new(document.Session.Snapshot.Id,[right])).Slot.DefinitionId==sharedGroup,"UI isolation changed another group.");
        Require(document.Placement.CanInsertChild&&placement.InsertButton.IsEnabled&&placement.InsertButton.Command is not null,
            "Isolated assembly insert UI is unavailable.");
        document.Placement.SelectedDefinition=document.Placement.Definitions.Single(d=>d.Id==target);
        await document.Placement.InsertCommand.ExecuteAsync(null);await Idle();
        var addedPath=document.Selection.Occurrence!;var inserted=addedPath.Slots[^1];
        Require(addedPath.Slots.Length==2&&addedPath.Slots[0]==left,"Insert command did not select its new child.");
        document.Placement.SelectedParent=document.Placement.Parents.Single(p=>p.Path.Slots.IsEmpty);
        Require(placement.ReparentButton.Command is not null,"Reparent UI is unavailable.");
        await document.Placement.ReparentCommand.ExecuteAsync(null);await Idle();
        var reparented=new OccurrencePath(document.Session.Snapshot.Id,[inserted]);
        Require(document.Selection.Occurrence!.Equals(reparented)&&
            Math.Abs(OccurrencePlacement.Resolve(document.Session.Snapshot,reparented).Slot.LocalTransform.Translation.X-12)<1e-8,
            "UI reparent did not preserve world position.");
        await document.Placement.RemoveCommand.ExecuteAsync(null);await Idle();
        Require(!document.Session.Snapshot.EnumerateOccurrences().Any(o=>o.Path.Equals(reparented)),"UI remove left an instance.");
        document.Selection.SelectOccurrence(occurrence.Path);await Idle();
        Require(document.Placement.CanMakePartIndependent&&placement.IndependentPartButton.IsEnabled&&
            placement.IndependentPartButton.Command is not null,"Part isolation UI is unavailable.");
        await document.Placement.MakePartIndependentCommand.ExecuteAsync(null);await Idle();
        var copyId=OccurrencePlacement.Resolve(document.Session.Snapshot,occurrence.Path).Slot.DefinitionId;
        Require(copyId!=target&&document.SelectedTargetPart==copyId,"UI part isolation did not change the selected target.");
        var copy=(PartDefinition)document.Session.Snapshot.Definitions[copyId];
        var copyBody=document.Session.Snapshot.Bodies[copy.Bodies.Single()];
        Require(copyBody.Id!=body.Id&&copyBody.Geometry==body.Geometry,"Independent part did not retain immutable geometry.");
        placement.OccurrenceNameInput.SetCurrentValue(TextBox.TextProperty,"One placement");
        await document.Placement.RenameOccurrenceCommand.ExecuteAsync(null);await Idle();
        Require(OccurrencePlacement.Resolve(document.Session.Snapshot,occurrence.Path).Slot.Name=="One placement",
            "Instance rename UI did not reach the selected slot.");
        placement.DefinitionNameInput.SetCurrentValue(TextBox.TextProperty,"Independent housing");
        await document.Placement.RenameDefinitionCommand.ExecuteAsync(null);await Idle();
        Require(document.Session.Snapshot.Definitions[copyId].Name=="Independent housing"&&
            document.Session.Snapshot.Definitions[target].Name=="Housing","Definition rename affected another part.");
        await document.Placement.RemoveCommand.ExecuteAsync(null);await Idle();
        document.Selection.SelectOccurrence(leftPath);await Idle();
        Require(document.Placement.CanPruneUnused&&placement.PruneUnusedButton.Command is not null,
            "Unused definition cleanup UI is unavailable.");
        await document.Placement.PruneUnusedCommand.ExecuteAsync(null);await Idle();
        Require(!document.Session.Snapshot.Definitions.ContainsKey(copyId)&&
            !document.Session.Snapshot.Bodies.ContainsKey(copyBody.Id),"Unused independent part was not removed.");
        var relations=Find<AssemblyConstraintsView>(window)??throw new InvalidOperationException("Assembly relation UI is missing.");
        Require(relations.FixButton.Command is not null&&document.AssemblyConstraints.CanCreate,
            "Fixed relation UI is unavailable.");
        await document.AssemblyConstraints.AddFixedCommand.ExecuteAsync(null);await Idle();
        var fixedRelation=AssertSingleRelation(document.Session.Snapshot);
        Require(fixedRelation.Evaluate(document.Session.Snapshot).Status==AssemblyConstraintStatus.Satisfied,
            "Fixed relation did not capture the selected world pose.");
        try
        {
            await document.Session.ExecuteAsync(DocumentEdits.MoveOccurrence(leftPath,RigidTransform3d.Translate(13,0,0)));
            throw new InvalidOperationException("Fixed assembly instance moved.");
        }
        catch(CadValidationException){}
        document.AssemblyConstraints.SelectedConstraint=document.AssemblyConstraints.Constraints.Single(c=>c.Id==fixedRelation.Id);
        await document.AssemblyConstraints.ToggleCommand.ExecuteAsync(null);await Idle();
        await document.Session.ExecuteAsync(DocumentEdits.MoveOccurrence(leftPath,RigidTransform3d.Translate(13,0,0)));await Idle();
        await document.AssemblyConstraints.RetargetCommand.ExecuteAsync(null);await Idle();
        await document.AssemblyConstraints.ToggleCommand.ExecuteAsync(null);await Idle();
        Require(AssertSingleRelation(document.Session.Snapshot).Evaluate(document.Session.Snapshot).Status==AssemblyConstraintStatus.Satisfied,
            "Explicit fixed-pose recapture failed.");
        await document.AssemblyConstraints.RemoveCommand.ExecuteAsync(null);await Idle();
        document.AssemblyConstraints.SelectedReference=document.AssemblyConstraints.References.Single(r=>r.Path.Equals(new OccurrencePath(document.Session.Snapshot.Id,[right])));
        relations.DistanceInput.SetCurrentValue(MahApps.Metro.Controls.NumericUpDown.ValueProperty,40d);
        Require(relations.AddDistanceButton.Command is not null,"Distance relation UI is unavailable.");
        await document.AssemblyConstraints.AddDistanceCommand.ExecuteAsync(null);await Idle();
        var distanceRelation=AssertSingleRelation(document.Session.Snapshot);
        document.AssemblyConstraints.SelectedConstraint=document.AssemblyConstraints.Constraints.Single(c=>c.Id==distanceRelation.Id);
        await document.AssemblyConstraints.AdjustCommand.ExecuteAsync(null);await Idle();
        Require(AssertSingleRelation(document.Session.Snapshot).Evaluate(document.Session.Snapshot).Status==AssemblyConstraintStatus.Satisfied,
            "Distance adjustment did not satisfy the selected point pair.");
        await document.AssemblyConstraints.RemoveCommand.ExecuteAsync(null);await Idle();
        relations.SecondaryAxisXInput.SetCurrentValue(MahApps.Metro.Controls.NumericUpDown.ValueProperty,1d);
        relations.SecondaryAxisZInput.SetCurrentValue(MahApps.Metro.Controls.NumericUpDown.ValueProperty,0d);
        await Idle();
        Require(relations.CoaxialButton.Command is not null,"Coaxial relation UI is unavailable.");
        await document.AssemblyConstraints.AddCoaxialCommand.ExecuteAsync(null);await Idle();
        var coaxial=AssertSingleRelation(document.Session.Snapshot);
        Require(coaxial.Kind==AssemblyConstraintKind.Coaxial,"Coaxial relation was not created.");
        document.AssemblyConstraints.SelectedConstraint=document.AssemblyConstraints.Constraints.Single(c=>c.Id==coaxial.Id);
        relations.SecondaryXInput.SetCurrentValue(MahApps.Metro.Controls.NumericUpDown.ValueProperty,2d);
        await Idle();
        await document.AssemblyConstraints.ApplyAnchorsCommand.ExecuteAsync(null);await Idle();
        Require(AssertSingleRelation(document.Session.Snapshot).SecondaryLocalPoint.X==2,
            "Local anchor editing did not reach the document.");
        await document.AssemblyConstraints.AdjustCommand.ExecuteAsync(null);await Idle();
        Require(AssertSingleRelation(document.Session.Snapshot).Evaluate(document.Session.Snapshot).Status==AssemblyConstraintStatus.Satisfied,
            "Coaxial adjustment did not satisfy the selected axes.");
        await document.AssemblyConstraints.RemoveCommand.ExecuteAsync(null);await Idle();
        await ModalAsync(()=>workspace.ManageResourcesCommand.ExecuteAsync(null),async()=>
        {
                var dialog=DialogHost.GetDialogSession(ViewServiceIdentifiers.RootDialogHost)?.Content as DocumentResourcesDialog??throw new InvalidOperationException("Resource dialog was not shown.");var vm=(DocumentResourcesViewModel)dialog.DataContext;
            try
            {
                vm.SelectedLayer=vm.Layers.Single(l=>l.Id==layer);vm.LayerVisible=false;await vm.ApplyLayerCommand.ExecuteAsync(null);
                Require(document.Scene.Items.All(i=>i.BodyId!=body.Id),"Layer visibility did not reach the viewport scene.");
                vm.LayerVisible=true;await vm.ApplyLayerCommand.ExecuteAsync(null);Require(document.Scene.Items.Any(i=>i.BodyId==body.Id),"Layer visibility did not restore the scene.");
                await Idle();
            }
            finally{DialogHost.GetDialogSession(ViewServiceIdentifiers.RootDialogHost)?.Close();}
        });
        await Idle();var host=Find<OcctViewportHost>(window)!;host.Viewport!.FitAll();host.Viewport.SaveScreenshot(Path.Combine(output,"resources-viewport.png"));
        Capture(window,Path.Combine(output,"resources-shell.png"));
        await document.Session.SaveAsync(storage,Path.Combine(output,"resources.cadoryx"));
        using(var reopened=await storage.LoadAsync(Path.Combine(output,"resources.cadoryx"),document.Session.Assets))
            Require(reopened.Snapshot.Bodies[body.Id]==document.Session.Snapshot.Bodies[body.Id],"Resource round-trip changed the body.");

        var options=new ExportOptionsDialog(Path.Combine(output,"resources.stl"));
        await ModalAsync(()=>DialogHost.Show(options, ViewServiceIdentifiers.RootDialogHost),()=>
        {
            Capture(options,Path.Combine(output,"metro-export.png"));DialogHost.GetDialogSession(ViewServiceIdentifiers.RootDialogHost)?.Close();return Task.CompletedTask;
        });
        await ModalAsync(()=>{workspace.OpenApplicationSettingsCommand.Execute(null);return Task.CompletedTask;},()=>
        {
            var dialog=DialogHost.GetDialogSession(ViewServiceIdentifiers.RootDialogHost)?.Content as ApplicationSettingsWindow??throw new InvalidOperationException("Settings dialog was not shown.");
            Capture(dialog,Path.Combine(output,"metro-settings.png"));DialogHost.GetDialogSession(ViewServiceIdentifiers.RootDialogHost)?.Close();return Task.CompletedTask;
        });
        var confirmation=new ConfirmationDialog("Save changes","Save changes to Housing?","Save","Don't save");
        await ModalAsync(()=>DialogHost.Show(confirmation, ViewServiceIdentifiers.RootDialogHost),()=>
        {
            Capture(confirmation,Path.Combine(output,"metro-confirmation.png"));DialogHost.GetDialogSession(ViewServiceIdentifiers.RootDialogHost)?.Close();return Task.CompletedTask;
        });
    }

    private static async Task ModalAsync(Func<Task> show,Func<Task> exercise)
    {
        var complete=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task showing=Task.CompletedTask;
        _=System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(async()=>
        {
            try
            {
                var deadline=System.Diagnostics.Stopwatch.StartNew();
                while(DialogHost.GetDialogSession(ViewServiceIdentifiers.RootDialogHost)?.Content is not FrameworkElement {IsLoaded:true,IsVisible:true})
                {
                    if(showing.IsFaulted)await showing;
                    if(deadline.Elapsed>TimeSpan.FromSeconds(5))throw new TimeoutException("Dialog content did not become visible.");
                    await Idle();
                }
                await Idle();await exercise();await Idle();await showing;complete.SetResult();
            }
            catch(Exception ex)
            {
                complete.SetException(ex);
                DialogHost.GetDialogSession(ViewServiceIdentifiers.RootDialogHost)?.Close();
            }
        }));
        showing=show();await complete.Task;
    }
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static AssemblyConstraint AssertSingleRelation(DocumentSnapshot snapshot)=>snapshot.AssemblyConstraints.Values.Single();
    private static async Task Idle(){await System.Windows.Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);await Task.Delay(100);}
    private static void Capture(FrameworkElement window,string path)
    {
        var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);
    }
    private static T? Find<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T found)return found;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)if(Find<T>(VisualTreeHelper.GetChild(root,i)) is {} child)return child;
        return null;
    }
}
