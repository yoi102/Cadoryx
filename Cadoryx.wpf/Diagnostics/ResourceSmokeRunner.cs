using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cadoryx.Db;
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
        await ModalAsync(()=>{workspace.ManageResourcesCommand.Execute(null);return Task.CompletedTask;},async()=>
        {
            var dialog=Find<DocumentResourcesDialog>(window)??throw new InvalidOperationException("Resource dialog was not shown.");
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
        await ModalAsync(()=>{workspace.ManageResourcesCommand.Execute(null);return Task.CompletedTask;},async()=>
        {
                var dialog=Find<DocumentResourcesDialog>(window)??throw new InvalidOperationException("Resource dialog was not shown.");var vm=(DocumentResourcesViewModel)dialog.DataContext;
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
            var dialog=Find<ApplicationSettingsWindow>(window)??throw new InvalidOperationException("Settings dialog was not shown.");
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
            try{await Idle();await Task.Delay(250);await exercise();await Task.Delay(250);await showing;complete.SetResult();}
            catch(Exception ex)
            {
                complete.SetException(ex);
                DialogHost.GetDialogSession(ViewServiceIdentifiers.RootDialogHost)?.Close();
            }
        }));
        showing=show();await complete.Task;
    }
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
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
