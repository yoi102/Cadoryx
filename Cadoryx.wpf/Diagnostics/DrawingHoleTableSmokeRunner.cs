using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Antelcat.I18N.WPF;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Lang.Strings;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Views;
using Microsoft.Extensions.DependencyInjection;
using OcctSharp;

namespace Cadoryx.wpf.Diagnostics;

internal static class DrawingHoleTableSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));using var listener=new TextWriterTraceListener(bindings);
        using var events=new StreamWriter(Path.Combine(output,"events.jsonl")){AutoFlush=true};
        CadDocumentSession? observedSession=null;
        bool authoredClick=false;
        var observedWindows=new List<TechnicalDrawingWindow>();
        void Record(object value){lock(events)events.WriteLine(JsonSerializer.Serialize(value));}
        void RecordCommand(object? sender,CadDocumentCommandActivity activity)=>Record(new
        {
            time=DateTimeOffset.UtcNow,eventKind="commit",activity.Verb,activity.Name,
            stateId=observedSession?.Snapshot.StateId,
            sheets=observedSession?.Snapshot.DrawingSheets.Values.Select(s=>new{s.Id,s.Name,views=s.Views.Length,tables=s.HoleTables.Length,rows=s.HoleTables.Sum(t=>t.Rows.Length)}).ToArray()
        });
        void RecordClick(object sender,RoutedEventArgs e)=>Record(new
        {
            time=DateTimeOffset.UtcNow,eventKind="click",authoredClick,
            source=(e.OriginalSource as FrameworkElement)?.Name,
            sheetCount=observedSession?.Snapshot.DrawingSheets.Count,
            stack=authoredClick?null:Environment.StackTrace
        });
        void Observe(TechnicalDrawingWindow value)
        {value.AddHandler(ButtonBase.ClickEvent,new RoutedEventHandler(RecordClick),true);observedWindows.Add(value);}
        void Click(Button button)
        {authoredClick=true;try{button.RaiseEvent(new(ButtonBase.ClickEvent));}finally{authoredClick=false;}}
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        var culture=CultureInfo.CurrentUICulture;var markup=CultureInfo.GetCultureInfo(((MainWindowViewModel)window.DataContext).CurrentCultureLCID);
        try
        {
            await Idle();var main=(MainWindowViewModel)window.DataContext;var document=main.ActiveDocument!;
            var session=document.Session;var kernel=services.GetRequiredService<IGeometryKernel>();
            observedSession=session;session.CommandCommitted+=RecordCommand;
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(12,10,15,RigidTransform3d.Identity),"Block"));var body=session.Snapshot.Bodies.Values.Single();
            await session.ExecuteAsync(new AddHoleCommand(body.Id,new(body.Geometry,RigidTransform3d.Translate(0,0,15),new(5,6),2,false,6,HoleHead.Counterbore,4,2),"Counterbore"));
            body=session.Snapshot.Bodies.Values.Single();var part=body.PartId;var second=ComponentSlotId.New();
            await session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(new(session.Snapshot.Id,[]),part,second,"Second",RigidTransform3d.Translate(30,0,0)));
            main.OpenTechnicalDrawingCommand.Execute(null);await Idle();var drawing=Application.Current.Windows.OfType<TechnicalDrawingWindow>().Single();
            Observe(drawing);
            Click(drawing.AddSheetButton);await Wait(()=>session.Snapshot.DrawingSheets.Count==1);
            drawing.KindChoice.SelectedIndex=(int)DrawingViewKind.Top;drawing.ViewX.Text="105";drawing.ViewY.Text="55";drawing.ViewScale.Text="2";
            Click(drawing.AddViewButton);await Wait(()=>session.Snapshot.DrawingSheets.Values.Single().Views.Length==1);
            drawing.ViewList.SelectedIndex=0;drawing.HoleTableTab.IsSelected=true;
            drawing.HoleTableName.Text="Manufacturing holes";drawing.HoleOriginZ.Text="15";drawing.HoleTableY.Text="85";
            Click(drawing.CreateHoleTableButton);await Wait(()=>session.Snapshot.DrawingSheets.Values.Single().HoleTables.Length==1&&!drawing.IsCalculating);
            var sheet=session.Snapshot.DrawingSheets.Values.Single();var table=sheet.HoleTables[0];drawing.HoleTableList.SelectedIndex=0;
            using var shape=OcctGeometryBridge.ReadShape(body.Geometry,session.Assets);using var map=RepairSnapshot.Create(shape);var proofs=new List<DrawingTopologyReference>();
            var firstPath=session.Snapshot.EnumerateOccurrences().First(o=>o.Path.Slots[0]!=second).Path;
            foreach(var item in map.Topology.Where(t=>t.Kind==ShapeKind.Face))
            {
                using var face=map.CopySubshape(item.Selection);if(face.GetFaceSurfaceSnapshot().SurfaceType!=SurfaceGeometryType.Cylinder)continue;
                var reference=await ((IDrawingTopologyResolver)kernel).ResolveDrawingTopologyAsync(session.Snapshot,firstPath,body.Id,item.Selection.Index,map.Fingerprint,session.Assets);
                try{await ((IDrawingHoleResolver)kernel).ResolveDrawingHoleAsync(session.Snapshot,reference,session.Assets);proofs.Add(reference);}catch(CadValidationException){}
            }
            if(proofs.Count!=1)throw new InvalidOperationException("No unique exact shaft selection.");var selected=proofs[0];
            async Task Pick(OccurrencePath path)
            {
                Click(drawing.PickHoleButton);
                if(!document.IsDrawingDatumPickPending)throw new InvalidOperationException("Production shaft pick did not start.");
                await document.ReceiveDrawingDatumPickAsync(path,body.Id,selected.FullTopologyIndex,selected.Fingerprint);await Idle();
                if(drawing.HoleEvidence.Text!=Strings.DrawingHoleSelected)throw new InvalidOperationException("Native original pick result did not reach hole controls.");
            }
            await Pick(firstPath);drawing.HoleNumber.Text="1";Click(drawing.AddHoleRowButton);
            await Wait(()=>session.Snapshot.DrawingSheets.Values.Single().HoleTables[0].Rows.Length==1&&!drawing.IsCalculating);
            await Pick(new(session.Snapshot.Id,[second]));drawing.HoleNumber.Text="2";Click(drawing.AddHoleRowButton);
            await Wait(()=>session.Snapshot.DrawingSheets.Values.Single().HoleTables[0].Rows.Length==2&&!drawing.IsCalculating);
            drawing.HoleOriginX.Text="1";drawing.HoleOriginY.Text="2";Click(drawing.ApplyHoleTableButton);
            await Wait(()=>session.Snapshot.DrawingSheets.Values.Single().HoleTables[0].LocalFrame.Translation.X==1&&!drawing.IsCalculating);
            table=session.Snapshot.DrawingSheets.Values.Single().HoleTables[0];
            if(table.Rows[0].CoordinatesMm!=new Point2d(4,4)||table.Rows[1].CoordinatesMm!=new Point2d(34,4)||
                table.Rows.Any(r=>r.Evidence.DepthMm!=6||r.Evidence.HeadDepthMm!=2||r.StaleReason is not null))
                throw new InvalidOperationException("Production table coordinates or total blind depth differ.");
            var rowId=table.Rows[0].Id;drawing.HoleRowList.SelectedIndex=0;await Pick(firstPath);drawing.HoleNumber.Text="7";
            Click(drawing.ReselectHoleRowButton);
            await Wait(()=>session.Snapshot.DrawingSheets.Values.Single().HoleTables[0].Rows.Any(r=>r.Id==rowId&&r.Number==7)&&!drawing.IsCalculating);
            drawing.HoleRowList.SelectedIndex=1;Click(drawing.DeleteHoleRowButton);
            await Wait(()=>session.Snapshot.DrawingSheets.Values.Single().HoleTables[0].Rows.Length==1&&!drawing.IsCalculating);await session.UndoAsync();
            Click(drawing.CancelHolePickButton);Click(drawing.PickHoleButton);
            Click(drawing.CancelHolePickButton);if(document.IsDrawingDatumPickPending)throw new InvalidOperationException("Cancel retained a native pick.");
            var plane=new CadDatumPlane(Guid.NewGuid(),part,"Table plane",DatumPlaneKind.Offset,DatumPlaneSource.Independent(RigidTransform3d.Translate(0,0,15)));
            var point=new CadDatumPoint(Guid.NewGuid(),part,"Table origin",DatumPointKind.Independent,new(1,2,15));
            var axis=new CadDatumAxis(Guid.NewGuid(),part,"Table X",DatumAxisKind.Independent,Direction:new(1,0,0));
            var book=session.Snapshot.Datums;
            await session.ExecuteAsync(new SetDatumsCommand(book with{Planes=book.Planes.Add(plane.Id,plane),Points=book.Points.Add(point.Id,point),Axes=book.Axes.Add(axis.Id,axis)}));await Idle();
            drawing.HoleTableList.SelectedIndex=0;
            drawing.HoleCoordinatePlaneChoice.SelectedValue=plane.Id;drawing.HoleCoordinateOriginChoice.SelectedValue=point.Id;drawing.HoleCoordinateAxisChoice.SelectedValue=axis.Id;
            Click(drawing.ApplyHoleTableButton);
            await Wait(()=>session.Snapshot.DrawingSheets.Values.Single().HoleTables[0].CoordinateBinding?.Plane==plane.Id&&!drawing.IsCalculating);
            if(drawing.HoleOriginX.IsEnabled||drawing.HoleAxisZ.IsEnabled)throw new InvalidOperationException("Bound numeric coordinate controls remained editable.");
            book=session.Snapshot.Datums;
            await session.ExecuteAsync(new SetDatumsCommand(book with{Points=book.Points.SetItem(point.Id,book.Points[point.Id] with{Value=new(2,3,15)})}));await Idle();
            table=session.Snapshot.DrawingSheets.Values.Single().HoleTables[0];
            if(table.Rows[0].CoordinatesMm!=new Point2d(3,3)||table.Rows[1].CoordinatesMm!=new Point2d(33,3)||table.CoordinateRevisions.Length!=3)
                throw new InvalidOperationException("Named datum transaction did not refresh coordinates.");
            drawing.HoleCoordinateReverseX.IsChecked=true;Click(drawing.ApplyHoleTableButton);
            await Wait(()=>session.Snapshot.DrawingSheets.Values.Single().HoleTables[0].CoordinateBinding?.ReverseX==true&&!drawing.IsCalculating);
            if(session.Snapshot.DrawingSheets.Values.Single().HoleTables[0].Rows[0].CoordinatesMm!=new Point2d(-3,-3))throw new InvalidOperationException("Reverse X control did not reverse the right-handed frame.");
            drawing.HoleCoordinateReverseX.IsChecked=false;Click(drawing.ApplyHoleTableButton);
            await Wait(()=>session.Snapshot.DrawingSheets.Values.Single().HoleTables[0].CoordinateBinding?.ReverseX==false&&!drawing.IsCalculating);
            drawing.Close();
            var languages=new List<object>();
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;
                var current=session.Snapshot.DrawingSheets.Values.Single();
                await session.ExecuteAsync(TechnicalDrawingCommands.EditSheet(current.Id,current.Name,current.WidthMm,current.HeightMm,current.Unit,current.Title,current.Author,
                    template:current.Template! with{Language=language switch{"zh-CN"=>DrawingCaptionLanguage.Chinese,"ja-JP"=>DrawingCaptionLanguage.Japanese,_=>DrawingCaptionLanguage.English}}));
                drawing=new TechnicalDrawingWindow(document){Owner=window};drawing.Show();await Idle();drawing.ZoomSlider.Value=.52;
                Observe(drawing);
                drawing.HoleTableTab.IsSelected=true;drawing.HoleTableList.SelectedIndex=0;
                drawing.HoleInspectorScroll.ScrollToTop();await Capture(drawing,"table-"+language);
                if(drawing.CreateHoleTableButton.Content?.ToString()!=Strings.DrawingHoleCreateTable||drawing.HoleTableList.Items.Count!=1)
                    throw new InvalidOperationException("Hole table controls are missing or not localized.");
                if(drawing.HoleCoordinatePlaneChoice.SelectedValue is not Guid selectedPlane||selectedPlane!=plane.Id||
                    drawing.HoleCoordinateOriginChoice.SelectedValue is not Guid selectedPoint||selectedPoint!=point.Id||
                    drawing.HoleCoordinateAxisChoice.SelectedValue is not Guid selectedAxis||selectedAxis!=axis.Id||
                    drawing.HoleCoordinateReverseX.Content?.ToString()!=Strings.DrawingHoleReverseX)
                    throw new InvalidOperationException("Typed coordinate choices did not preserve identity or localization.");
                drawing.HoleCoordinatePlaneChoice.BringIntoView();await Idle();await Capture(drawing,"datums-"+language);
                drawing.HoleRowList.SelectedIndex=0;drawing.UpdateLayout();drawing.AddHoleRowButton.BringIntoView();await Idle();
                drawing.HoleInspectorScroll.ScrollToBottom();await Capture(drawing,"rows-"+language);
                if(drawing.ReselectHoleRowButton.Content?.ToString()!=Strings.DrawingHoleReselectRow||drawing.HoleRowList.Items.Count!=2)
                    throw new InvalidOperationException("Hole row controls are missing or not localized.");
                string pdf=Path.Combine(output,"holes-"+language+".pdf");TechnicalDrawingPdf.Write(session.Snapshot,pdf);
                languages.Add(new{language,pdf});Click(drawing.PickHoleButton);drawing.Close();
                if(document.IsDrawingDatumPickPending)throw new InvalidOperationException("Closing retained a native pick.");
            }
            string native=Path.Combine(output,"holes.cadoryx");await session.SaveAsync(services.GetRequiredService<IDocumentStorage>(),native);
            using(var loaded=await services.GetRequiredService<IDocumentStorage>().LoadAsync(native,session.Assets))
            {
                var stored=loaded.Snapshot.DrawingSheets.Values.Single().HoleTables.Single();var actual=session.Snapshot.DrawingSheets.Values.Single().HoleTables.Single();
                if(stored.Id!=actual.Id||stored.Rows.Length!=2||stored.CoordinateBinding!=actual.CoordinateBinding||
                    !stored.CoordinateRevisions.SequenceEqual(actual.CoordinateRevisions)||stored.LocalFrame!=actual.LocalFrame||
                    stored.Rows.Where((r,i)=>r.Id!=actual.Rows[i].Id||r.CoordinatesMm!=actual.Rows[i].CoordinatesMm||!r.Evidence.SameProof(actual.Rows[i].Evidence)).Any())
                    throw new InvalidOperationException("Hole table did not preserve original proof and row identity on reopen.");
            }
            bindings.Flush();if(new FileInfo(Path.Combine(output,"bindings.log")).Length!=0)throw new InvalidOperationException("Binding errors.");
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{passed=true,tables=1,rows=2,totalDepth=6,headDepth=2,coordinateDatums=3,languages,native,
                input="WPF button events and original native face selection API; physical pointer not verified"},new JsonSerializerOptions{WriteIndented=true}));
            Application.Current.Shutdown(0);
        }
        catch(Exception error){Record(new{time=DateTimeOffset.UtcNow,eventKind="failure",error=error.ToString(),sheetCount=observedSession?.Snapshot.DrawingSheets.Count});File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{passed=false,error=error.ToString()},new JsonSerializerOptions{WriteIndented=true}));Application.Current.Shutdown(1);}
        finally
        {
            if(observedSession is not null)observedSession.CommandCommitted-=RecordCommand;
            foreach(var value in observedWindows)value.RemoveHandler(ButtonBase.ClickEvent,new RoutedEventHandler(RecordClick));
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);CultureInfo.CurrentUICulture=culture;I18NExtension.Culture=markup;
        }
        async Task Capture(TechnicalDrawingWindow drawing,string name)
        {
            await Idle();drawing.UpdateLayout();await Idle();var bitmap=new RenderTargetBitmap((int)drawing.ActualWidth,(int)drawing.ActualHeight,96,96,PixelFormats.Pbgra32);
            bitmap.Render(drawing);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,name+".png"));encoder.Save(file);
        }
    }
    private static async Task Idle()=>await Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
    private static async Task Wait(Func<bool> predicate)
    {for(int i=0;i<200;i++){await Idle();if(predicate())return;await Task.Delay(25);}throw new TimeoutException("Hole table command did not commit.");}
}
