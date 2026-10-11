using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Antelcat.I18N.WPF;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Lang.Strings;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Views;
using Microsoft.Extensions.DependencyInjection;
using OcctSharp;

namespace Cadoryx.wpf.Diagnostics;

internal static class DrawingPlanarSizeSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));using var listener=new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        var culture=CultureInfo.CurrentUICulture;var markup=CultureInfo.GetCultureInfo(((MainWindowViewModel)window.DataContext).CurrentCultureLCID);
        try
        {
            await Idle();var main=(MainWindowViewModel)window.DataContext;var document=main.ActiveDocument!;var session=document.Session;
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(20,12,4,RigidTransform3d.Identity),"Plate"));
            var plate=session.Snapshot.Bodies.Values.Single();var part=plate.PartId;
            using(var wire=ShapeFactory.CreatePolygonWire([new(40,0,0),new(60,0,0),new(60,12,0),new(53,12,0),
                new(53,5,0),new(47,5,0),new(47,12,0),new(40,12,0)],true))
            using(var face=ShapeFactory.CreatePlanarFace(wire))
            using(var vector=GpVec.Create(0,0,4))
            using(var slot=face.Extrude(vector))
            using(var stored=OcctGeometryBridge.StoreShape(slot,session.Assets))
                await session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(stored.Geometry),"Slot",part));
            var slotBody=session.Snapshot.Bodies.Values.Single(b=>b.Name=="Slot");
            main.OpenTechnicalDrawingCommand.Execute(null);await Idle();var drawing=Application.Current.Windows.OfType<TechnicalDrawingWindow>().Single();
            drawing.AddSheetButton.RaiseEvent(new(ButtonBase.ClickEvent));await Wait(()=>session.Snapshot.DrawingSheets.Count==1&&!drawing.IsCalculating);
            drawing.KindChoice.SelectedIndex=(int)DrawingViewKind.Front;drawing.ViewX.Text="140";drawing.ViewY.Text="80";drawing.ViewScale.Text="2";
            drawing.AddViewButton.RaiseEvent(new(ButtonBase.ClickEvent));await Wait(()=>session.Snapshot.DrawingSheets.Values.Single().Views.Length==1&&!drawing.IsCalculating);
            drawing.ViewList.SelectedIndex=0;drawing.InspectorTabs.SelectedIndex=3;
            async Task<(int First,int Second,string Fingerprint)> Pair(BodyId bodyId,Vector3d axis,bool inner)
            {
                var doc=session.Snapshot;using var shape=OcctGeometryBridge.ReadShape(doc.Bodies[bodyId].Geometry,session.Assets);using var map=RepairSnapshot.Create(shape);
                var positions=new List<(int Index,double Position)>();
                foreach(var item in map.Topology.Where(t=>t.Kind==ShapeKind.Face))
                {
                    using var face=map.CopySubshape(item.Selection);var s=face.GetFaceSurfaceSnapshot();
                    var at=face.EvaluateFace((s.FirstUParameter+s.LastUParameter)/2,(s.FirstVParameter+s.LastVParameter)/2);
                    if(Math.Abs(new Vector3d(at.Normal.X,at.Normal.Y,at.Normal.Z).Dot(axis))>1-1e-8)
                        positions.Add((item.Selection.Index,new Vector3d(at.Point.X,at.Point.Y,at.Point.Z).Dot(axis)));
                }
                var pair=positions.OrderBy(p=>p.Position).Skip(inner?1:0).Take(2).ToArray();await Idle();
                return (pair[0].Index,pair[1].Index,map.Fingerprint);
            }
            async Task PickPair(BodyId bodyId,bool inner,bool reverse=false)
            {
                var pair=await Pair(bodyId,inner?new(1,0,0):Vector3d.UnitZ,inner);var doc=session.Snapshot;
                drawing.PlanarSizeKind.SelectedIndex=inner?1:0;drawing.PickAnnotationButton.RaiseEvent(new(ButtonBase.ClickEvent));
                await document.ReceiveDrawingDatumPickAsync(doc.EnumerateOccurrences().Single().Path,bodyId,reverse?pair.Second:pair.First,pair.Fingerprint);
                await Wait(()=>drawing.AnnotationEvidence.Text!="—");
                drawing.PickOppositeAnnotationButton.RaiseEvent(new(ButtonBase.ClickEvent));
                await document.ReceiveDrawingDatumPickAsync(doc.EnumerateOccurrences().Single().Path,bodyId,reverse?pair.First:pair.Second,pair.Fingerprint);
                await Wait(()=>drawing.OppositeAnnotationEvidence.Text!="—");
            }
            async Task AddMedian(string name,BodyId bodyId,bool inner,double x)
            {
                drawing.AnnotationList.SelectedIndex=-1;drawing.AnnotationType.SelectedIndex=0;drawing.ManufacturingDatumType.SelectedIndex=2;
                await PickPair(bodyId,inner);drawing.AnnotationName.Text=name;drawing.AnnotationX.Text=x.ToString(CultureInfo.InvariantCulture);drawing.AnnotationY.Text="25";
                drawing.AddAnnotationButton.RaiseEvent(new(ButtonBase.ClickEvent));
                await Wait(()=>!drawing.IsCalculating&&session.Snapshot.DrawingSheets.Values.Single().DatumSymbols.Any(d=>d.Name==name),drawing);
            }
            await AddMedian("A",plate.Id,false,25);await AddMedian("B",slotBody.Id,true,55);
            var doc0=session.Snapshot;using(var shape=OcctGeometryBridge.ReadShape(plate.Geometry,session.Assets))using(var map=RepairSnapshot.Create(shape))
            {
                var p=map.Topology.First(t=>{if(t.Kind!=ShapeKind.Face)return false;using var face=map.CopySubshape(t.Selection);var s=face.GetFaceSurfaceSnapshot();
                    return Math.Abs(face.EvaluateFace((s.FirstUParameter+s.LastUParameter)/2,(s.FirstVParameter+s.LastVParameter)/2).Normal.X)>1-1e-8;});
                drawing.AnnotationType.SelectedIndex=0;drawing.ManufacturingDatumType.SelectedIndex=0;drawing.PickAnnotationButton.RaiseEvent(new(ButtonBase.ClickEvent));
                await document.ReceiveDrawingDatumPickAsync(doc0.EnumerateOccurrences().Single().Path,plate.Id,p.Selection.Index,map.Fingerprint);
            }
            drawing.AnnotationName.Text="C";drawing.AnnotationX.Text="85";drawing.AnnotationY.Text="25";drawing.AddAnnotationButton.RaiseEvent(new(ButtonBase.ClickEvent));
            await Wait(()=>!drawing.IsCalculating&&session.Snapshot.DrawingSheets.Values.Single().DatumSymbols.Length==3,drawing);
            drawing.AnnotationType.SelectedIndex=1;drawing.ControlFrameOptions.IsExpanded=true;drawing.ToleranceTarget.SelectedIndex=2;
            await PickPair(plate.Id,false);drawing.Characteristic.SelectedIndex=(int)DrawingGeometricCharacteristic.Position;
            drawing.DiameterZone.IsChecked=false;drawing.MaterialCondition.SelectedIndex=1;drawing.AnnotationTolerance.Text="0.025";drawing.OrderedDatums.Text="B(M),C";
            drawing.AnnotationX.Text="25";drawing.AnnotationY.Text="55";drawing.AddAnnotationButton.RaiseEvent(new(ButtonBase.ClickEvent));
            await Wait(()=>!drawing.IsCalculating&&session.Snapshot.DrawingSheets.Values.Single().ControlFrames.Length==1,drawing);
            var author=session.Snapshot.DrawingSheets.Values.Single();var before=session.Snapshot;
            drawing.DiameterZone.IsChecked=true;drawing.AnnotationList.SelectedIndex=3;drawing.DiameterZone.IsChecked=true;
            drawing.ApplyAnnotationButton.RaiseEvent(new(ButtonBase.ClickEvent));await Wait(()=>!drawing.IsCalculating&&!string.IsNullOrWhiteSpace(drawing.Status.Text),drawing);
            if(!ReferenceEquals(before,session.Snapshot))throw new InvalidOperationException("A median plane accepted a diameter zone.");
            drawing.DiameterZone.IsChecked=false;
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(4,4,6,RigidTransform3d.Translate(18,10,-1)),"Corner cutter",part));
            var cutter=session.Snapshot.Bodies.Values.Single(b=>b.Name=="Corner cutter");
            await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Cut,[plate.Id,cutter.Id]));
            drawing.InspectorTabs.SelectedIndex=1;drawing.ViewList.SelectedIndex=0;drawing.ReselectViewSourceButton.RaiseEvent(new(ButtonBase.ClickEvent));
            await Wait(()=>!drawing.IsCalculating&&session.Snapshot.DrawingSheets.Values.Single().Views.Single().StaleReason is null,drawing);
            var continued=session.Snapshot.DrawingSheets.Values.Single();var a=continued.DatumSymbols.Single(d=>d.Name=="A");var frame=continued.ControlFrames.Single();
            if(a.StaleReason is not null||frame.StaleReason is not null||a.Source.HistoryOrigin!=author.DatumSymbols[0].Source||
                a.PlanarSizeEvidence!.OppositeSource.HistoryOrigin!=author.DatumSymbols[0].PlanarSizeEvidence!.OppositeSource||
                frame.PlanarSizeEvidence!.OppositeSource.HistoryOrigin!=author.ControlFrames[0].PlanarSizeEvidence!.OppositeSource)
                throw new InvalidOperationException("Both original size authors did not continue with complete finite proof.");
            File.WriteAllText(Path.Combine(output,"planar-proof.json"),JsonSerializer.Serialize(new{author,current=continued},new JsonSerializerOptions{WriteIndented=true}));
            var template=Guid.NewGuid();await session.ExecuteAsync(DrawingTemplateCommands.Save(continued.Id,template,"Planar"));
            var oldSheet=session.Snapshot.DrawingSheets.Values.Single();await session.ExecuteAsync(DrawingTemplateCommands.Apply(continued.Id,template));
            var applied=session.Snapshot.DrawingSheets.Values.Single();
            if(!oldSheet.DatumSymbols.SequenceEqual(applied.DatumSymbols)||!oldSheet.ControlFrames.SequenceEqual(applied.ControlFrames))
                throw new InvalidOperationException("Template changed either size face or datum binding.");
            var storage=services.GetRequiredService<IDocumentStorage>();string native=Path.Combine(output,"planar.cadoryx");await session.SaveAsync(storage,native);
            using(var loaded=await storage.LoadAsync(native,session.Assets))
                if(!frame.PlanarSizeEvidence.SameProof(loaded.Snapshot.DrawingSheets.Values.Single().ControlFrames.Single().PlanarSizeEvidence!))
                    throw new InvalidOperationException("Native reopen changed the planar proof.");
            drawing.Close();var languages=new List<object>();
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;
                drawing=new(document){Owner=window};drawing.Show();await Idle();drawing.SheetList.SelectedIndex=0;drawing.ViewList.SelectedIndex=0;
                var standard=language switch{"zh-CN"=>DrawingStandard.GB,"ja-JP"=>DrawingStandard.ASME,_=>DrawingStandard.ISO};
                var captions=language switch{"zh-CN"=>DrawingCaptionLanguage.Chinese,"ja-JP"=>DrawingCaptionLanguage.Japanese,_=>DrawingCaptionLanguage.English};
                drawing.StandardChoice.SelectedItem=standard;drawing.CaptionLanguageChoice.SelectedIndex=(int)captions;drawing.ProjectionChoice.SelectedIndex=standard==DrawingStandard.ASME?1:0;
                drawing.ApplySheetButton.RaiseEvent(new(ButtonBase.ClickEvent));await Wait(()=>!drawing.IsCalculating&&session.Snapshot.DrawingSheets.Values.Single().Standard==standard&&session.Snapshot.DrawingSheets.Values.Single().Template?.Language==captions,drawing);
                drawing.InspectorTabs.SelectedIndex=3;drawing.AnnotationList.SelectedIndex=1;drawing.AnnotationInspectorScroll.ScrollToTop();await Capture(drawing,"median-"+language);
                if(drawing.ManufacturingDatumType.SelectedIndex!=2||drawing.PlanarSizeKind.SelectedIndex!=1||drawing.PickOppositeAnnotationButton.Content?.ToString()!=Strings.DrawingPickOppositeSizeFace)
                    throw new InvalidOperationException("Two-face median datum controls are missing or not localized.");
                drawing.AnnotationList.SelectedIndex=3;drawing.ControlFrameOptions.IsExpanded=true;await Idle();drawing.UpdateLayout();
                double y=drawing.ToleranceTarget.TranslatePoint(new Point(0,0),drawing.AnnotationInspectorScroll).Y;
                drawing.AnnotationInspectorScroll.ScrollToVerticalOffset(drawing.AnnotationInspectorScroll.VerticalOffset+y-100);await Capture(drawing,"system-"+language);
                if(drawing.ToleranceTarget.SelectedIndex!=2||drawing.DiameterZone.IsChecked==true||drawing.OrderedDatums.Text!="B(M),C")
                    throw new InvalidOperationException("Median target lost its non-diameter zone or two-face datum binding.");
                string pdf=Path.Combine(output,"planar-"+language+".pdf");TechnicalDrawingPdf.Write(session.Snapshot,pdf);languages.Add(new{language,standard=standard.ToString(),pdf});drawing.Close();
            }
            drawing=new(document){Owner=window};drawing.Show();await Idle();drawing.SheetList.SelectedIndex=0;drawing.ViewList.SelectedIndex=0;drawing.InspectorTabs.SelectedIndex=3;drawing.AnnotationList.SelectedIndex=1;
            await PickPair(slotBody.Id,true,true);drawing.ApplyAnnotationButton.RaiseEvent(new(ButtonBase.ClickEvent));
            await Wait(()=>!drawing.IsCalculating&&session.Snapshot.DrawingSheets.Values.Single().ControlFrames.Single().StaleReason is not null,drawing);
            drawing.AnnotationList.SelectedIndex=3;drawing.ApplyAnnotationButton.RaiseEvent(new(ButtonBase.ClickEvent));
            await Wait(()=>!drawing.IsCalculating&&session.Snapshot.DrawingSheets.Values.Single().ControlFrames.Single().StaleReason is null,drawing);
            drawing.Close();await session.SaveAsync(storage,Path.Combine(output,"reselected.cadoryx"));
            bindings.Flush();if(new FileInfo(Path.Combine(output,"bindings.log")).Length!=0)throw new InvalidOperationException("Binding errors.");
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{passed=true,externalWidth=4,internalWidth=6,bothAuthorsContinued=true,languages,native,
                input="WPF button events and original native two-face selection API; physical pointer separate"},new JsonSerializerOptions{WriteIndented=true}));
            Application.Current.Shutdown(0);
        }
        catch(Exception error){File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{passed=false,error=error.ToString()},new JsonSerializerOptions{WriteIndented=true}));Application.Current.Shutdown(1);}
        finally{PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);CultureInfo.CurrentUICulture=culture;I18NExtension.Culture=markup;}
        async Task Capture(TechnicalDrawingWindow drawing,string name)
        {
            await Idle();await Task.Delay(250);drawing.UpdateLayout();var dpi=VisualTreeHelper.GetDpi(drawing);
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(drawing.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(drawing.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);
            bitmap.Render(drawing);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,name+".png"));encoder.Save(file);
        }
    }
    private static async Task Idle()=>await Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
    private static async Task Wait(Func<bool> predicate,TechnicalDrawingWindow? drawing=null)
    {
        var timer=Stopwatch.StartNew();while(timer.Elapsed.TotalSeconds<60){await Idle();if(predicate())return;await Task.Delay(25);}
        throw new TimeoutException("Planar manufacturing command did not commit: "+drawing?.Status.Text);
    }
}

