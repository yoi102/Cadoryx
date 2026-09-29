using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Controls;
using Cadoryx.wpf.Views;
using Cadoryx.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Cadoryx.wpf.Diagnostics;

internal static class M11DrawingWindowSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        using var bindingOutput=new StreamWriter(Path.Combine(output,"bindings.log"));
        using var listener=new TextWriterTraceListener(bindingOutput);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        try
        {
            await Idle();var main=(MainWindowViewModel)window.DataContext;
            var document=main.ActiveDocument??throw new InvalidOperationException("No document.");
            await document.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
            var part=document.Session.Snapshot.Definitions.Values.OfType<PartDefinition>().Single();
            await document.Session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(
                new(document.Session.Snapshot.Id,[]),part.Id,ComponentSlotId.New(),"Second",
                RigidTransform3d.Translate(40,0,0)));
            main.OpenTechnicalDrawingCommand.Execute(null);await Idle();
            var drawing=Application.Current.Windows.OfType<TechnicalDrawingWindow>().Single();
            if(!drawing.IsLoaded||drawing.Owner!=window)throw new InvalidOperationException("Owned drawing MetroWindow did not open.");
            drawing.AddSheetButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Wait(()=>document.Session.Snapshot.DrawingSheets.Count==1);
            drawing.SheetTitle.Text="工程图纸";
            drawing.ApplySheetButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Wait(()=>document.Session.Snapshot.DrawingSheets.Values.Single().Title=="工程图纸");
            drawing.ViewX.Text="115";drawing.ViewY.Text="90";drawing.ViewScale.Text="1";
            drawing.AddViewButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Wait(()=>document.Session.Snapshot.DrawingSheets.Values.Single().Views.Length==1);
            drawing.ViewList.SelectedIndex=0;
            var sheet=document.Session.Snapshot.DrawingSheets.Values.Single();
            if(sheet.Views[0].Strokes.IsEmpty||drawing.PaperHost.Child is not DrawingSheetCanvas)
                throw new InvalidOperationException("Drawing window has no projected geometry.");
            var viewport=Find<OcctViewportHost>(window)?.Viewport??
                throw new InvalidOperationException("Native viewport unavailable for dimension selection.");
            viewport.SetProjection(CadProjection.Top);viewport.FitAll();await Idle();
            async Task Pick(bool first,Vector3d world)
            {
                (first?drawing.PickFirstButton:drawing.PickSecondButton)
                    .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var pixel=viewport.WorldToScreen(world);
                viewport.PointerPressed(0,pixel.X,pixel.Y,0);
                viewport.PointerReleased(0,pixel.X,pixel.Y,0);
                try{await Wait(()=>(first?drawing.FirstEvidence:drawing.SecondEvidence).Text!="—");}
                catch(TimeoutException error){throw new TimeoutException($"Datum pick {(first?1:2)} failed: {drawing.Status.Text}; first={drawing.FirstEvidence.Text}; second={drawing.SecondEvidence.Text}",error);}
            }
            await Pick(true,new(5,10,30));
            await Pick(false,new(45,10,30));
            drawing.DimensionX.Text="145";drawing.DimensionY.Text="45";
            drawing.AddDimensionButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            try{await Wait(()=>document.Session.Snapshot.DrawingSheets.Values.Single().Dimensions.Length==1);}
            catch(TimeoutException error){throw new TimeoutException("Dimension entry failed: "+drawing.Status.Text,error);}
            if(document.Session.Snapshot.DrawingSheets.Values.Single().Dimensions[0].Value<=0)
                throw new InvalidOperationException("Picked drawing dimension has no geometric distance.");
            await Idle();
            var dpi=VisualTreeHelper.GetDpi(drawing);
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(drawing.ActualWidth*dpi.DpiScaleX),
                (int)Math.Ceiling(drawing.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            using(var stream=File.Create(Path.Combine(output,"drawing-window.png")))
            {var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));png.Save(stream);}
            var pdf=Path.Combine(output,"drawing.pdf");TechnicalDrawingPdf.Write(document.Session.Snapshot,pdf);
            var native=Path.Combine(output,"drawing.cadoryx");
            await document.Session.SaveAsync(services.GetRequiredService<IDocumentStorage>(),native);
            using(var loaded=await services.GetRequiredService<IDocumentStorage>().LoadAsync(native,document.Session.Assets))
                if(loaded.Snapshot.DrawingSheets.Values.Single().Views[0].Strokes.IsEmpty)
                    throw new InvalidOperationException("Drawing did not roundtrip.");
            drawing.PickFirstButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if(!document.IsDrawingDatumPickPending)throw new InvalidOperationException("Drawing pick did not start.");
            drawing.Close();
            if(document.IsDrawingDatumPickPending)throw new InvalidOperationException("Closing the drawing window left a native pick active.");
            bindingOutput.Flush();
            if(new FileInfo(Path.Combine(output,"bindings.log")).Length>0)
                throw new InvalidOperationException("Drawing window has binding errors.");
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new
                {passed=true,sheets=1,views=1,dimensions=1,pdf,native},new JsonSerializerOptions{WriteIndented=true}));
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
    private static async Task Wait(Func<bool> condition)
    {
        for(int i=0;i<100;i++){await Idle();if(condition())return;await Task.Delay(20);}
        throw new TimeoutException("Drawing command did not commit.");
    }
    private static async Task Idle()=>await Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
    private static T? Find<T>(DependencyObject parent) where T:DependencyObject
    {
        if(parent is T found)return found;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
            if(Find<T>(VisualTreeHelper.GetChild(parent,i)) is {} match)return match;
        return null;
    }
}
