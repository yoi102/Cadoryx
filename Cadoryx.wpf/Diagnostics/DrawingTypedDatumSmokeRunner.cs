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

internal static class DrawingTypedDatumSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output,bool continueHistory=false,bool composedDrilling=false)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));using var listener=new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        var culture=CultureInfo.CurrentUICulture;var markup=CultureInfo.GetCultureInfo(((MainWindowViewModel)window.DataContext).CurrentCultureLCID);
        TechnicalDrawingWindow? observedDrawing=null;bool expectedClose=false;string stage="initialization";
        void Trace(string message)=>File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        string State()
        {
            var sheet=((MainWindowViewModel)window.DataContext).ActiveDocument?.Session.Snapshot.DrawingSheets.Values.SingleOrDefault();
            return $"stage={stage}; visible={observedDrawing?.IsVisible}; calculating={observedDrawing?.IsCalculating}; status={observedDrawing?.Status.Text}; standard={sheet?.Standard}; captions={sheet?.Template?.Language}; view={sheet?.Views.FirstOrDefault()?.StaleReason}";
        }
        void Observe(TechnicalDrawingWindow drawing)
        {
            observedDrawing=drawing;expectedClose=false;
            drawing.Closed+=(_,_)=>Trace($"drawing closed; expected={expectedClose}; {State()}");
        }
        void CloseExpected(TechnicalDrawingWindow drawing){expectedClose=true;drawing.Close();}
        try
        {
            Trace(stage);
            await Idle();var main=(MainWindowViewModel)window.DataContext;var document=main.ActiveDocument!;var session=document.Session;
            var kernel=services.GetRequiredService<IGeometryKernel>();
            await session.ExecuteAsync(new AddBodyCommand(new CylinderRecipe(composedDrilling?15:5,20,RigidTransform3d.Identity),"Controlled"));
            var controlled=session.Snapshot.Bodies.Values.Single();var part=controlled.PartId;
            await session.ExecuteAsync(new AddBodyCommand(new CylinderRecipe(2,10,RigidTransform3d.Translate(0,0,25)),"Datum A",part));
            await session.ExecuteAsync(new AddBodyCommand(new CylinderRecipe(3,10,RigidTransform3d.Translate(0,0,40)),"Datum B",part));
            var bodyA=session.Snapshot.Bodies.Values.Single(b=>b.Name=="Datum A");var bodyB=session.Snapshot.Bodies.Values.Single(b=>b.Name=="Datum B");
            main.OpenTechnicalDrawingCommand.Execute(null);await Idle();var drawing=Application.Current.Windows.OfType<TechnicalDrawingWindow>().Single();
            Observe(drawing);
            drawing.AddSheetButton.RaiseEvent(new(ButtonBase.ClickEvent));await Wait(()=>session.Snapshot.DrawingSheets.Count==1&&!drawing.IsCalculating);
            drawing.KindChoice.SelectedIndex=(int)DrawingViewKind.Front;drawing.ViewX.Text="135";drawing.ViewY.Text="78";drawing.ViewScale.Text="2";
            drawing.AddViewButton.RaiseEvent(new(ButtonBase.ClickEvent));await Wait(()=>session.Snapshot.DrawingSheets.Values.Single().Views.Length==1&&!drawing.IsCalculating);
            drawing.ViewList.SelectedIndex=0;drawing.InspectorTabs.SelectedIndex=3;
            async Task Pick(BodyId bodyId,bool cylinder=true)
            {
                var doc=session.Snapshot;var body=doc.Bodies[bodyId];using var shape=OcctGeometryBridge.ReadShape(body.Geometry,session.Assets);using var map=RepairSnapshot.Create(shape);
                var selected=map.Topology.First(t=>
                {if(t.Kind!=ShapeKind.Face)return false;using var face=map.CopySubshape(t.Selection);return face.GetFaceSurfaceSnapshot().SurfaceType==(cylinder?SurfaceGeometryType.Cylinder:SurfaceGeometryType.Plane);});
                drawing.PickAnnotationButton.RaiseEvent(new(ButtonBase.ClickEvent));
                if(!document.IsDrawingDatumPickPending)throw new InvalidOperationException("Original manufacturing selection did not start.");
                await document.ReceiveDrawingDatumPickAsync(doc.EnumerateOccurrences().Single().Path,bodyId,selected.Selection.Index,map.Fingerprint);
                await Wait(()=>drawing.AnnotationEvidence.Text!="—");
            }
            async Task AddDatum(string name,BodyId source,double x,bool axis)
            {
                drawing.AnnotationList.SelectedIndex=-1;drawing.AnnotationType.SelectedIndex=0;drawing.ManufacturingDatumType.SelectedIndex=axis?1:0;
                await Pick(source,axis);drawing.AnnotationName.Text=name;drawing.AnnotationX.Text=x.ToString(CultureInfo.InvariantCulture);drawing.AnnotationY.Text="25";
                drawing.AddAnnotationButton.RaiseEvent(new(ButtonBase.ClickEvent));await Wait(()=>!drawing.IsCalculating&&session.Snapshot.DrawingSheets.Values.Single().DatumSymbols.Any(d=>d.Name==name));
            }
            await AddDatum("A",bodyA.Id,25,true);await AddDatum("B",bodyB.Id,50,true);await AddDatum("C",controlled.Id,75,false);
            var sheet=session.Snapshot.DrawingSheets.Values.Single();var a=sheet.DatumSymbols.Single(d=>d.Name=="A");
            if(sheet.DatumSymbols.Take(2).Any(d=>d.FeatureType!=DrawingDatumFeatureType.CylinderSizeAxis||d.SizeEvidence is null))throw new InvalidOperationException("Axis datum omitted original size proof.");
            drawing.AnnotationType.SelectedIndex=1;drawing.ControlFrameOptions.IsExpanded=true;await Pick(controlled.Id);
            drawing.Characteristic.SelectedIndex=(int)DrawingGeometricCharacteristic.Position;drawing.ToleranceTarget.SelectedIndex=1;drawing.DiameterZone.IsChecked=true;
            drawing.MaterialCondition.SelectedIndex=1;drawing.AnnotationTolerance.Text="0.025";drawing.OrderedDatums.Text="A(M)-B(L),C";
            drawing.AnnotationX.Text="25";drawing.AnnotationY.Text="55";drawing.AddAnnotationButton.RaiseEvent(new(ButtonBase.ClickEvent));
            await Wait(()=>!drawing.IsCalculating&&session.Snapshot.DrawingSheets.Values.Single().ControlFrames.Length==1);
            var frame=session.Snapshot.DrawingSheets.Values.Single().ControlFrames.Single();
            if(frame.SizeEvidence is null||frame.DatumSystem.Length!=2||frame.DatumSystem[0].References.Length!=2)throw new InvalidOperationException("Common datum compartment or target size proof missing.");
            drawing.AnnotationList.SelectedIndex=0;drawing.AnnotationName.Text="D";drawing.RenameAnnotationDatumButton.RaiseEvent(new(ButtonBase.ClickEvent));
            await Wait(()=>!drawing.IsCalculating&&session.Snapshot.DrawingSheets.Values.Single().DatumSymbols.Any(d=>d.Id==a.Id&&d.Name=="D"));
            drawing.AnnotationList.SelectedIndex=3;
            if(drawing.OrderedDatums.Text!="D(M)-B(L),C")throw new InvalidOperationException("Rename changed reference identity or boundary placement.");
            var before=session.Snapshot;drawing.OrderedDatums.Text="D(M),D";drawing.ApplyAnnotationButton.RaiseEvent(new(ButtonBase.ClickEvent));
            await Wait(()=>!drawing.IsCalculating&&!string.IsNullOrWhiteSpace(drawing.Status.Text));
            if(!ReferenceEquals(before,session.Snapshot))throw new InvalidOperationException("Invalid repeated datum partially committed.");
            drawing.OrderedDatums.Text="D(M)-B(L),C";
            if(continueHistory)
            {
                stage="continue original hole attachment";Trace(stage);
                var authoredFrame=session.Snapshot.DrawingSheets.Values.Single().ControlFrames.Single();
                var authoredC=session.Snapshot.DrawingSheets.Values.Single().DatumSymbols.Single(d=>d.Name=="C");
                await session.ExecuteAsync(new AddHoleCommand(controlled.Id,new(controlled.Geometry,RigidTransform3d.Translate(0,0,20),new(0,0),2,true,0,
                    composedDrilling?HoleHead.Counterbore:HoleHead.Plain,composedDrilling?4:0,composedDrilling?2:0),"History hole"));
                var unselected=session.Snapshot;
                if(unselected.DrawingSheets.Values.Single().Views.Single().StaleReason is null)throw new InvalidOperationException("Consumed view did not require explicit source selection.");
                drawing.InspectorTabs.SelectedIndex=1;drawing.ViewList.SelectedIndex=0;
                drawing.ReselectViewSourceButton.RaiseEvent(new(ButtonBase.ClickEvent));
                await Wait(()=>!drawing.IsCalculating&&session.Snapshot.DrawingSheets.Values.Single().Views.Single().StaleReason is null);
                var continued=session.Snapshot;var continuedSheet=continued.DrawingSheets.Values.Single();
                frame=continuedSheet.ControlFrames.Single();var continuedC=continuedSheet.DatumSymbols.Single(d=>d.Id==authoredC.Id);
                if(frame.StaleReason is not null||continuedC.StaleReason is not null||frame.Source.HistoryOrigin!=authoredFrame.Source||
                   continuedC.Source.HistoryOrigin!=authoredC.Source||frame.DatumBindings.Single(b=>b.Id==authoredC.Id).Source!=continuedC.Source)
                    throw new InvalidOperationException("Explicit view source did not preserve author attachments and continue their proven current bindings.");
                await session.UndoAsync();if(!ReferenceEquals(unselected,session.Snapshot))throw new InvalidOperationException("Continuation undo did not restore stale original selection.");
                await session.RedoAsync();if(!ReferenceEquals(continued,session.Snapshot))throw new InvalidOperationException("Continuation redo did not restore the same evidence.");
                if(composedDrilling)
                {
                    stage="compose counterbore, countersink and pattern";Trace(stage);
                    var bore=session.Snapshot.Features.Values.Single(f=>f.Name=="History hole");
                    if(bore.TopologyHistory?.SchemaVersion!=3)throw new InvalidOperationException("Counterbore omitted composed native history.");
                    await session.ExecuteAsync(new AddHoleCommand(bore.OutputBodyId,new(bore.Result,RigidTransform3d.Translate(0,0,20),new(4,0),2,true,0,
                        HoleHead.Countersink,4,0,Math.PI/2),"Sink seed"));
                    var seed=session.Snapshot.Features.Values.Single(f=>f.Name=="Sink seed");
                    var layout=new RepetitionRecipe(seed.Result,RepetitionKind.Linear,RigidTransform3d.Identity,3,4,
                        [Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid()],RepetitionOutput.Fused);
                    await session.ExecuteAsync(new AddHolePatternCommand(seed.OutputBodyId,new(seed.Id,(HoleRecipe)seed.Recipe,layout),"Sink pattern"));
                    var target=session.Snapshot.Features.Values.Single(f=>f.Name=="Sink pattern");
                    if(seed.TopologyHistory?.SchemaVersion!=3||target.TopologyHistory?.SchemaVersion!=3)
                        throw new InvalidOperationException("Countersink or included pattern omitted composed native history.");
                    drawing.InspectorTabs.SelectedIndex=1;drawing.ViewList.SelectedIndex=0;
                    drawing.ReselectViewSourceButton.RaiseEvent(new(ButtonBase.ClickEvent));
                    stage="reselect composed view";Trace(State());
                    await Wait(()=>!drawing.IsCalculating&&session.Snapshot.DrawingSheets.Values.Single().Views.Single().StaleReason is null,60,
                        ()=>drawing.Status.Text+" / "+session.Snapshot.DrawingSheets.Values.Single().Views.Single().StaleReason);
                    continuedSheet=session.Snapshot.DrawingSheets.Values.Single();frame=continuedSheet.ControlFrames.Single();
                    continuedC=continuedSheet.DatumSymbols.Single(d=>d.Id==authoredC.Id);
                    if(frame.StaleReason is not null||continuedC.StaleReason is not null||frame.Source.HistoryOrigin!=authoredFrame.Source||
                       continuedC.Source.HistoryOrigin!=authoredC.Source||frame.Source.FeatureId!=target.Id||
                       frame.DatumBindings.Single(b=>b.Id==authoredC.Id).Source!=continuedC.Source)
                        throw new InvalidOperationException("Composed drilling did not preserve author and datum bindings through all three features.");
                    var template=Guid.NewGuid();await session.ExecuteAsync(DrawingTemplateCommands.Save(continuedSheet.Id,template,"Composed history"));
                    var beforeTemplate=session.Snapshot.DrawingSheets.Values.Single();await session.ExecuteAsync(DrawingTemplateCommands.Apply(continuedSheet.Id,template));
                    var afterTemplate=session.Snapshot.DrawingSheets.Values.Single();
                    if(!beforeTemplate.DatumSymbols.SequenceEqual(afterTemplate.DatumSymbols)||!beforeTemplate.ControlFrames.SequenceEqual(afterTemplate.ControlFrames))
                        throw new InvalidOperationException("Template changed continued manufacturing evidence.");
                    File.WriteAllText(Path.Combine(output,"composed-stages.json"),JsonSerializer.Serialize(new{counterbore=bore.Id,countersink=seed.Id,pattern=target.Id,
                        schemas=new[]{bore.TopologyHistory.SchemaVersion,seed.TopologyHistory.SchemaVersion,target.TopologyHistory.SchemaVersion},nativeStages=8},new JsonSerializerOptions{WriteIndented=true}));
                }
                drawing.InspectorTabs.SelectedIndex=3;drawing.AnnotationList.SelectedIndex=3;
                if(drawing.AnnotationEvidence.Text!=Strings.DrawingAttachmentContinued)throw new InvalidOperationException("Continued attachment is not identified in the editor.");
                File.WriteAllText(Path.Combine(output,"continued-proof.json"),JsonSerializer.Serialize(new{author=authoredFrame.Source,current=frame.Source,datumAuthor=authoredC.Source,datumCurrent=continuedC.Source},new JsonSerializerOptions{WriteIndented=true}));
            }
            string native=Path.Combine(output,"typed-datums.cadoryx");var storage=services.GetRequiredService<IDocumentStorage>();await session.SaveAsync(storage,native);
            stage="reopen native proof";Trace(State());
            using(var loaded=await storage.LoadAsync(native,session.Assets))
            {
                var stored=loaded.Snapshot.DrawingSheets.Values.Single().ControlFrames.Single();
                if(frame.SizeEvidence is null||stored.SizeEvidence is null||!frame.SizeEvidence.SameProof(stored.SizeEvidence)||!frame.DatumSystem[0].SameReferences(stored.DatumSystem[0])||
                    !frame.DatumBindings.SequenceEqual(stored.DatumBindings))throw new InvalidOperationException("Native reopen changed typed evidence or common datum identity.");
            }
            CloseExpected(drawing);var languages=new List<object>();
            foreach(var language in new[]{"en-US","zh-CN","ja-JP"})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;
                drawing=new(document){Owner=window};drawing.Show();await Idle();drawing.SheetList.SelectedIndex=0;drawing.ViewList.SelectedIndex=0;
                Observe(drawing);stage="apply sheet "+language;
                var standard=language switch{"zh-CN"=>DrawingStandard.GB,"ja-JP"=>DrawingStandard.ASME,_=>DrawingStandard.ISO};
                var captions=language switch{"zh-CN"=>DrawingCaptionLanguage.Chinese,"ja-JP"=>DrawingCaptionLanguage.Japanese,_=>DrawingCaptionLanguage.English};
                drawing.StandardChoice.SelectedItem=standard;drawing.CaptionLanguageChoice.SelectedIndex=(int)captions;drawing.ProjectionChoice.SelectedIndex=standard==DrawingStandard.ASME?1:0;
                Trace($"{State()}; requested={standard}/{captions}; selectedCaptionIndex={drawing.CaptionLanguageChoice.SelectedIndex}; enabled={drawing.ApplySheetButton.IsEnabled}");
                drawing.ApplySheetButton.RaiseEvent(new(ButtonBase.ClickEvent));
                await Wait(()=>!drawing.IsCalculating&&session.Snapshot.DrawingSheets.Values.Single().Standard==standard&&session.Snapshot.DrawingSheets.Values.Single().Template?.Language==captions,
                    details:()=>State()+$"; requested={standard}/{captions}");
                Trace("sheet committed; "+State());
                drawing.InspectorTabs.SelectedIndex=3;drawing.AnnotationList.SelectedIndex=0;
                drawing.AnnotationInspectorScroll.ScrollToTop();await Capture(drawing,"datum-"+language);
                if(drawing.ManufacturingDatumType.Items.Count!=3||drawing.ManufacturingDatumType.SelectedIndex!=1||drawing.AddAnnotationButton.Content?.ToString()!=Strings.DrawingAddAnnotation)
                    throw new InvalidOperationException("Typed datum editor is missing or not localized.");
                drawing.AnnotationList.SelectedIndex=3;drawing.ControlFrameOptions.IsExpanded=true;await Idle();drawing.UpdateLayout();
                double groupTop=drawing.OrderedDatums.TranslatePoint(new Point(0,0),drawing.AnnotationInspectorScroll).Y;
                drawing.AnnotationInspectorScroll.ScrollToVerticalOffset(drawing.AnnotationInspectorScroll.VerticalOffset+groupTop-180);await Idle();
                await Capture(drawing,"system-"+language);
                string guidance=standard==DrawingStandard.ASME?Strings.DrawingAsmeSizeRule:Strings.DrawingIsoSizeRule;
                if(drawing.OrderedDatums.Text!="D(M)-B(L),C"||drawing.ManufacturingStandardRule.Text!=guidance)throw new InvalidOperationException("Typed frame editor lost groups, conditions or convention guidance.");
                if(continueHistory&&drawing.AnnotationEvidence.Text!=Strings.DrawingAttachmentContinued)throw new InvalidOperationException("Continued attachment label is not localized.");
                string pdf=Path.Combine(output,"typed-"+language+".pdf");TechnicalDrawingPdf.Write(session.Snapshot,pdf);languages.Add(new{language,standard=standard.ToString(),pdf});CloseExpected(drawing);
            }
            // Recompute preserves old author proof, then explicit datum and frame controls restore validity.
            var producer=session.Snapshot.Features[a.Source.FeatureId!.Value];await session.ExecuteAsync(new RecomputeCommand(producer.Id,new CylinderRecipe(2.5,10,RigidTransform3d.Translate(0,0,25))));
            sheet=session.Snapshot.DrawingSheets.Values.Single();if(sheet.DatumSymbols.Single(d=>d.Id==a.Id).StaleReason is null||sheet.ControlFrames.Single().StaleReason is null)
                throw new InvalidOperationException("Changed original feature did not invalidate its typed datum system.");
            drawing=new(document){Owner=window};drawing.Show();await Idle();drawing.SheetList.SelectedIndex=0;drawing.ViewList.SelectedIndex=0;drawing.InspectorTabs.SelectedIndex=3;drawing.AnnotationList.SelectedIndex=0;
            Observe(drawing);stage="reselect changed datum and frame";Trace(State());
            await Pick(bodyA.Id);drawing.ApplyAnnotationButton.RaiseEvent(new(ButtonBase.ClickEvent));await Wait(()=>!drawing.IsCalculating&&session.Snapshot.DrawingSheets.Values.Single().DatumSymbols.Single(d=>d.Id==a.Id).StaleReason is null);
            if(session.Snapshot.DrawingSheets.Values.Single().ControlFrames.Single().StaleReason is null)throw new InvalidOperationException("Datum reselect silently rebound its frame.");
            drawing.AnnotationList.SelectedIndex=3;drawing.ApplyAnnotationButton.RaiseEvent(new(ButtonBase.ClickEvent));await Wait(()=>!drawing.IsCalculating&&session.Snapshot.DrawingSheets.Values.Single().ControlFrames.Single().StaleReason is null);
            CloseExpected(drawing);await session.SaveAsync(storage,Path.Combine(output,"reselected.cadoryx"));
            bindings.Flush();if(new FileInfo(Path.Combine(output,"bindings.log")).Length!=0)throw new InvalidOperationException("Binding errors.");
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{passed=true,continuedHistory=continueHistory,composedDrilling,datums=3,commonMembers=2,precedenceGroups=2,languages,native,
                input="WPF button events and original native face selection API; physical pointer separate"},new JsonSerializerOptions{WriteIndented=true}));Application.Current.Shutdown(0);
        }
        catch(Exception error){Trace("failed; "+State());File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{passed=false,error=error.ToString(),state=State()},new JsonSerializerOptions{WriteIndented=true}));Application.Current.Shutdown(1);}
        finally{PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);CultureInfo.CurrentUICulture=culture;I18NExtension.Culture=markup;}
        async Task Capture(TechnicalDrawingWindow drawing,string name)
        {
            await Idle();await Task.Delay(250);drawing.UpdateLayout();var dpi=VisualTreeHelper.GetDpi(drawing);
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(drawing.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(drawing.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);
            bitmap.Render(drawing);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,name+".png"));encoder.Save(file);
        }
    }
    private static async Task Idle()=>await Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
    private static async Task Wait(Func<bool> predicate,int seconds=60,Func<string>? details=null)
    {
        var clock=Stopwatch.StartNew();
        while(clock.Elapsed.TotalSeconds<seconds){await Idle();if(predicate())return;await Task.Delay(25);}
        throw new TimeoutException("Typed manufacturing command did not commit: "+details?.Invoke());
    }
}
