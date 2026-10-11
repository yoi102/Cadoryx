using System.Diagnostics;
using System.Globalization;
using Antelcat.I18N.WPF;
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
        var originalCulture=CultureInfo.CurrentUICulture;var originalMarkup=CultureInfo.GetCultureInfo(((MainWindowViewModel)window.DataContext).CurrentCultureLCID);
        try
        {
            await Idle();var main=(MainWindowViewModel)window.DataContext;
            var document=main.ActiveDocument??throw new InvalidOperationException("No document.");
            var material=new CadMaterial(MaterialId.New(),"Steel",7.85e-6);
            await document.Session.ExecuteAsync(ResourceCommands.AddMaterial(material));
            await document.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
            var body=document.Session.Snapshot.Bodies.Values.Single();
            await document.Session.ExecuteAsync(DocumentEdits.SetBodyProperties(body.Id,body.Name,body.Appearance,
                body.IsVisible,body.LayerId,material.Id));
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
            drawing.DrawingNumberText.Text="CAD-F2-001";drawing.RevisionText.Text="B";
            drawing.OrganizationText.Text="Cadoryx";drawing.TitleMaterialText.Text="Steel";
            drawing.SurfaceFinishText.Text="Ra 1.6";drawing.GeneralToleranceText.Text="ISO 2768-m";
            drawing.DrawnByText.Text="Design";drawing.CheckedByText.Text="Check";drawing.ApprovedByText.Text="Approve";
            drawing.IssueDateText.Text="2026-10-03";
            drawing.ApplySheetButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Wait(()=>document.Session.Snapshot.DrawingSheets.Values.Single().Title=="工程图纸");
            if(document.Session.Snapshot.DrawingSheets.Values.Single().Template?.TitleBlock.DrawingNumber!="CAD-F2-001")
                throw new InvalidOperationException("Production sheet controls did not save the title block.");
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
            drawing.InspectorTabs.SelectedIndex=3;
            drawing.PickAnnotationButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var annotationPixel=viewport.WorldToScreen(new(5,10,30));
            viewport.PointerPressed(0,annotationPixel.X,annotationPixel.Y,0);viewport.PointerReleased(0,annotationPixel.X,annotationPixel.Y,0);
            await Wait(()=>drawing.AnnotationEvidence.Text!="—");
            drawing.AnnotationType.SelectedIndex=0;drawing.AnnotationName.Text="A";
            drawing.AnnotationX.Text="35";drawing.AnnotationY.Text="30";
            drawing.AddAnnotationButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Wait(()=>document.Session.Snapshot.DrawingSheets.Values.Single().DatumSymbols.Length==1);
            drawing.AnnotationType.SelectedIndex=1;drawing.Characteristic.SelectedIndex=(int)DrawingGeometricCharacteristic.Perpendicularity;
            // Control a different original face on the same occurrence as datum A.
            var annotationDoc=document.Session.Snapshot;var annotationDatum=annotationDoc.DrawingSheets.Values.Single().DatumSymbols[0].Source;
            using(var annotationShape=Cadoryx.Kernel.Occt.OcctGeometryBridge.ReadShape(annotationDoc.Bodies[annotationDatum.BodyId].Geometry,document.Session.Assets))
            using(var annotationMap=OcctSharp.RepairSnapshot.Create(annotationShape))
            {
                var different=annotationMap.Topology.First(t=>t.Kind==OcctSharp.ShapeKind.Face&&t.Selection.Index!=annotationDatum.FullTopologyIndex);
                drawing.PickAnnotationButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await document.ReceiveDrawingDatumPickAsync(annotationDatum.Path,annotationDatum.BodyId,different.Selection.Index,annotationMap.Fingerprint);
                await Wait(()=>drawing.AnnotationEvidence.Text!="—");
            }
            drawing.OrderedDatums.Text="A";drawing.AnnotationTolerance.Text="0.02";
            drawing.AnnotationX.Text="65";drawing.AnnotationY.Text="30";
            drawing.AddAnnotationButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Wait(()=>document.Session.Snapshot.DrawingSheets.Values.Single().ControlFrames.Length==1);
            drawing.AnnotationType.SelectedIndex=2;drawing.AnnotationName.Text="1";
            drawing.AnnotationX.Text="70";drawing.AnnotationY.Text="65";
            drawing.AddAnnotationButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Wait(()=>document.Session.Snapshot.DrawingSheets.Values.Single().Balloons.Length==1);
            drawing.PartsTableX.Text="30";drawing.PartsTableY.Text="140";
            drawing.PartsTableButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Wait(()=>document.Session.Snapshot.DrawingSheets.Values.Single().PartsTablePositionMm is not null);
            if(document.Session.Snapshot.DrawingSheets.Values.Single().Balloons[0].Quantity!=2)
                throw new InvalidOperationException("Parts table did not count shared instances by exact paths.");
            drawing.KindChoice.SelectedIndex=(int)DrawingViewKind.Section;
            drawing.NormalX.Text="0";drawing.NormalY.Text="0";drawing.NormalZ.Text="1";drawing.SectionOffset.Text="15";
            drawing.ViewX.Text="200";drawing.ViewY.Text="125";drawing.ViewScale.Text="1";
            drawing.AddViewButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Wait(()=>document.Session.Snapshot.DrawingSheets.Values.Single().Views.Length==2);
            var section=document.Session.Snapshot.DrawingSheets.Values.Single().Views.Single(v=>v.Kind==DrawingViewKind.Section);
            int hatchCount=section.Strokes.Count(line=>line.Kind==DrawingLineKind.SectionHatch);
            if(hatchCount==0||section.StaleReason is not null)
                throw new InvalidOperationException("Production drawing controls did not create current exact section hatching.");
            drawing.ViewList.SelectedItem=section;drawing.KindChoice.SelectedIndex=(int)DrawingViewKind.Detail;
            drawing.DetailX.Text="5";drawing.DetailY.Text="10";drawing.DetailRadius.Text="6";
            drawing.ViewX.Text="215";drawing.ViewY.Text="65";drawing.ViewScale.Text="2";
            drawing.AddViewButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Wait(()=>document.Session.Snapshot.DrawingSheets.Values.Single().Views.Length==3);
            var detail=document.Session.Snapshot.DrawingSheets.Values.Single().Views.Single(v=>v.Kind==DrawingViewKind.Detail);
            int detailHatchesBefore=detail.Strokes.Count(line=>line.Kind==DrawingLineKind.SectionHatch);
            drawing.ViewList.SelectedItem=detail;drawing.ViewScale.Text="3";
            drawing.EditViewButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Wait(()=>document.Session.Snapshot.DrawingSheets.Values.Single().Views.Single(v=>v.Id==detail.Id).Scale==3);
            detail=document.Session.Snapshot.DrawingSheets.Values.Single().Views.Single(v=>v.Id==detail.Id);
            int detailHatchCount=detail.Strokes.Count(line=>line.Kind==DrawingLineKind.SectionHatch);
            if(detailHatchCount<=detailHatchesBefore||detail.StaleReason is not null||!detail.Sources.SequenceEqual(section.Sources))
                throw new InvalidOperationException("Production detail scale did not reclip its own associated section hatching.");
            drawing.ViewList.SelectedItem=section;drawing.HatchAngle.Text="30";drawing.HatchSpacing.Text="3.5";
            drawing.HatchAlternate.IsChecked=false;
            drawing.ApplyHatchButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Wait(()=>document.Session.Snapshot.DrawingSheets.Values.Single().Views.Single(v=>v.Id==section.Id).Hatching?.Default.AngleDegrees==30);
            drawing.HatchMaterialChoice.SelectedIndex=1;drawing.HatchEnabled.IsChecked=false;
            drawing.ApplyHatchButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Wait(()=>document.Session.Snapshot.DrawingSheets.Values.Single().Views.Single(v=>v.Id==section.Id).Strokes
                .All(s=>s.Kind!=DrawingLineKind.SectionHatch));
            if(document.Session.Snapshot.DrawingSheets.Values.Single().Views.Single(v=>v.Id==detail.Id).Strokes
                .Any(s=>s.Kind==DrawingLineKind.SectionHatch))throw new InvalidOperationException("Detail ignored a disabled material rule.");
            drawing.RemoveHatchRuleButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Wait(()=>document.Session.Snapshot.DrawingSheets.Values.Single().Views.Single(v=>v.Id==section.Id).Hatching?.Materials.IsEmpty==true);
            section=document.Session.Snapshot.DrawingSheets.Values.Single().Views.Single(v=>v.Id==section.Id);
            detail=document.Session.Snapshot.DrawingSheets.Values.Single().Views.Single(v=>v.Id==detail.Id);
            hatchCount=section.Strokes.Count(s=>s.Kind==DrawingLineKind.SectionHatch);
            detailHatchCount=detail.Strokes.Count(s=>s.Kind==DrawingLineKind.SectionHatch);
            if(hatchCount==0||detailHatchCount==0||section.StaleReason is not null||detail.StaleReason is not null)
                throw new InvalidOperationException("Removing a material rule did not restore associated default hatching.");
            drawing.HatchOptions.IsExpanded=true;drawing.InspectorTabs.SelectedIndex=1;
            async Task Capture(string name)
            {
                await Idle();await Task.Delay(250);drawing.UpdateLayout();var dpi=VisualTreeHelper.GetDpi(drawing);
                var bitmap=new RenderTargetBitmap((int)Math.Ceiling(drawing.ActualWidth*dpi.DpiScaleX),
                    (int)Math.Ceiling(drawing.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);
                bitmap.Render(drawing);
                using var stream=File.Create(Path.Combine(output,name+"-window.png"));
                var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));png.Save(stream);
            }
            async Task Reveal(FrameworkElement control,string name)
            {
                await Idle();drawing.UpdateLayout();
                double top=control.TranslatePoint(new Point(0,0),drawing.SheetInspectorScroll).Y;
                drawing.SheetInspectorScroll.ScrollToVerticalOffset(drawing.SheetInspectorScroll.VerticalOffset+top-35);
                await Capture(name);
            }
            var templates=new List<object>();
            await Capture("hatch-rules");
            drawing.InspectorTabs.SelectedIndex=0;drawing.SheetContentOptions.IsExpanded=true;
            drawing.SheetContentKind.SelectedIndex=0;drawing.SheetContentCaption.Text="Process";drawing.SheetContentText.Text="Milling";
            drawing.SheetContentX.Text="30";drawing.SheetContentY.Text="110";drawing.SheetContentWidth.Text="70";drawing.SheetContentHeight.Text="12";
            drawing.AddSheetContentButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Wait(()=>!drawing.IsCalculating&&document.Session.Snapshot.DrawingSheets.Values.Single().Template?.CustomFields.Length==1);
            drawing.SheetContentKind.SelectedIndex=1;drawing.SheetContentText.Text="1. Deburr all edges\r\n2. Protect finished surfaces";
            drawing.SheetContentX.Text="30";drawing.SheetContentY.Text="170";drawing.SheetContentWidth.Text="70";
            drawing.AddSheetContentButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Wait(()=>!drawing.IsCalculating&&document.Session.Snapshot.DrawingSheets.Values.Single().Template?.Notes.Length==1);
            drawing.FrameZonesEnabled.IsChecked=true;drawing.FrameZoneColumns.Text="8";drawing.FrameZoneRows.Text="6";
            drawing.ApplyFrameZonesButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Wait(()=>!drawing.IsCalculating&&document.Session.Snapshot.DrawingSheets.Values.Single().Template?.Zones is not null);
            drawing.TemplateLibraryName.Text="Workshop";drawing.SaveTemplateButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Wait(()=>!drawing.IsCalculating&&document.Session.Snapshot.DrawingTemplates.Count==1);
            drawing.SheetContentList.SelectedIndex=0;drawing.SheetContentText.Text="Turning";
            drawing.ApplySheetContentButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Wait(()=>!drawing.IsCalculating&&document.Session.Snapshot.DrawingSheets.Values.Single().Template?.CustomFields[0].Value=="Turning");
            var sheetBeforeTemplate=document.Session.Snapshot.DrawingSheets.Values.Single();
            drawing.ApplyTemplateButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Wait(()=>!drawing.IsCalculating&&document.Session.Snapshot.DrawingSheets.Values.Single().Template?.CustomFields[0].Value=="Milling");
            var appliedTemplate=document.Session.Snapshot.DrawingSheets.Values.Single();
            if(!appliedTemplate.Views.SequenceEqual(sheetBeforeTemplate.Views)||!appliedTemplate.DatumSymbols.SequenceEqual(sheetBeforeTemplate.DatumSymbols)||
                !appliedTemplate.ControlFrames.SequenceEqual(sheetBeforeTemplate.ControlFrames)||!appliedTemplate.Balloons.SequenceEqual(sheetBeforeTemplate.Balloons))
                throw new InvalidOperationException("Template application moved views or altered exact annotation sources.");
            drawing.TemplateLibraryName.Text="Workshop final";drawing.ReplaceTemplateButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Wait(()=>!drawing.IsCalculating&&document.Session.Snapshot.DrawingTemplates.Values.Single().Name=="Workshop final");
            var libraryFile=Path.Combine(output,"workshop.cadtemplate");
            DrawingTemplateFile.Write(document.Session.Snapshot.DrawingTemplates.Values.Single(),libraryFile);
            if(DrawingTemplateFile.Read(libraryFile)!=document.Session.Snapshot.DrawingTemplates.Values.Single())
                throw new InvalidOperationException("Detached template file did not preserve all sheet content.");
            drawing.TemplateLibraryName.Text="Disposable";drawing.SaveTemplateButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Wait(()=>!drawing.IsCalculating&&document.Session.Snapshot.DrawingTemplates.Count==2);
            drawing.DeleteTemplateButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Wait(()=>!drawing.IsCalculating&&document.Session.Snapshot.DrawingTemplates.Count==1);
            drawing.TemplateLibraryOptions.IsExpanded=true;await Reveal(drawing.TemplateLibraryChoice,"template-library");
            drawing.SheetContentList.SelectedIndex=1;await Reveal(drawing.SheetContentList,"sheet-content");
            drawing.FrameZonesOptions.IsExpanded=true;await Reveal(drawing.FrameZonesEnabled,"frame-zones");
            drawing.InspectorTabs.SelectedIndex=0;drawing.TitleBlockOptions.IsExpanded=true;
            foreach(var standard in Enum.GetValues<DrawingStandard>())
            {
                drawing.StandardChoice.SelectedItem=standard;
                drawing.PaperPresetChoice.SelectedIndex=(int)(standard==DrawingStandard.ASME?DrawingPaperFormat.AnsiB:DrawingPaperFormat.A4)+1;
                drawing.LandscapeCheck.IsChecked=true;drawing.ProjectionChoice.SelectedIndex=standard==DrawingStandard.ASME?1:0;
                drawing.CaptionLanguageChoice.SelectedIndex=standard switch{DrawingStandard.ISO=>0,DrawingStandard.GB=>1,_=>2};
                drawing.ApplySheetButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var expectedLanguage=standard switch{DrawingStandard.ISO=>DrawingCaptionLanguage.English,
                    DrawingStandard.GB=>DrawingCaptionLanguage.Chinese,_=>DrawingCaptionLanguage.Japanese};
                await Wait(()=>!drawing.IsCalculating&&document.Session.Snapshot.DrawingSheets.Values.Single().Standard==standard&&
                    document.Session.Snapshot.DrawingSheets.Values.Single().Template?.Language==expectedLanguage);
                var currentSheet=document.Session.Snapshot.DrawingSheets.Values.Single();
                if(currentSheet.Template?.TitleBlock.DrawingNumber!="CAD-F2-001"||
                    currentSheet.Template.Projection!=(standard==DrawingStandard.ASME?DrawingProjectionMethod.ThirdAngle:DrawingProjectionMethod.FirstAngle))
                    throw new InvalidOperationException("Manufacturing template controls lost metadata or projection method.");
                drawing.ZoomSlider.Value=standard==DrawingStandard.ASME?.37:.5;
                await Capture("template-"+standard);
                var templatePdf=Path.Combine(output,"template-"+standard+".pdf");
                TechnicalDrawingPdf.Write(document.Session.Snapshot,templatePdf);
                templates.Add(new{standard,width=currentSheet.WidthMm,height=currentSheet.HeightMm,
                    language=currentSheet.Template.Language,projection=currentSheet.Template.Projection,pdf=templatePdf});
            }
            drawing.DrawingNumberText.BringIntoView();await Capture("title-fields");
            drawing.IssueDateText.BringIntoView();await Capture("title-fields-bottom");
            drawing.InspectorTabs.SelectedIndex=3;drawing.AnnotationList.BringIntoView();await Capture("manufacturing-annotations");
            await Capture("drawing");
            var pdf=Path.Combine(output,"drawing.pdf");TechnicalDrawingPdf.Write(document.Session.Snapshot,pdf);
            var native=Path.Combine(output,"drawing.cadoryx");
            await document.Session.SaveAsync(services.GetRequiredService<IDocumentStorage>(),native);
            using(var loaded=await services.GetRequiredService<IDocumentStorage>().LoadAsync(native,document.Session.Assets))
            {
                var reopened=loaded.Snapshot.DrawingSheets.Values.Single();
                var reopenedDetail=reopened.Views.Single(v=>v.Id==detail.Id);
                if(loaded.Snapshot.DrawingSheets.Values.Single().Views[0].Strokes.IsEmpty||
                    loaded.Snapshot.DrawingSheets.Values.Single().Views.Single(v=>v.Id==section.Id).Strokes
                        .Count(line=>line.Kind==DrawingLineKind.SectionHatch)!=hatchCount||
                    reopenedDetail.Scale!=3||reopenedDetail.StaleReason is not null||
                    reopenedDetail.Strokes.Count(line=>line.Kind==DrawingLineKind.SectionHatch)!=detailHatchCount||
                    !reopenedDetail.Sources.SequenceEqual(detail.Sources)||
                    reopened.Template!=document.Session.Snapshot.DrawingSheets.Values.Single().Template||
                    reopened.DatumSymbols.Length!=1||reopened.ControlFrames.Length!=1||reopened.Balloons.Length!=1||
                    reopened.Balloons[0].Quantity!=2||reopened.ControlFrames[0].ReferencedDatumIds.Single()!=reopened.DatumSymbols[0].Id||
                    loaded.Snapshot.DrawingTemplates.Count!=1||reopened.Template?.CustomFields.Length!=1||reopened.Template?.Notes.Length!=1||
                    reopened.Template?.Zones is null)
                    throw new InvalidOperationException("Drawing did not roundtrip.");
            }
            drawing.PickAnnotationButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if(!document.IsDrawingDatumPickPending)throw new InvalidOperationException("Drawing pick did not start.");
            drawing.Close();
            if(document.IsDrawingDatumPickPending)throw new InvalidOperationException("Closing the drawing window left a native pick active.");
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;
                drawing=new TechnicalDrawingWindow(document){Owner=window};drawing.Show();await Idle();
                drawing.ZoomSlider.Value=.37;
                drawing.InspectorTabs.SelectedIndex=3;drawing.ViewList.SelectedIndex=0;drawing.AnnotationList.SelectedIndex=1;
                drawing.ControlFrameOptions.IsExpanded=true;drawing.AnnotationList.BringIntoView();
                await Capture("annotation-"+language);
                if(drawing.AddAnnotationButton.Content?.ToString()!=Cadoryx.Lang.Strings.Strings.DrawingAddAnnotation||
                   drawing.AnnotationDatumKind.Items.Count!=2||drawing.Characteristic.Items.Count!=12)
                    throw new InvalidOperationException("Manufacturing controls were not localized or lacked choices: "+language);
                drawing.OrderedDatums.BringIntoView();await Capture("control-frame-"+language);drawing.Close();
                drawing=new TechnicalDrawingWindow(document){Owner=window};drawing.Show();await Idle();drawing.ZoomSlider.Value=.37;
                drawing.InspectorTabs.SelectedIndex=0;drawing.TemplateLibraryOptions.IsExpanded=true;
                drawing.TemplateLibraryChoice.SelectedIndex=0;await Reveal(drawing.TemplateLibraryChoice,"template-library-"+language);
                drawing.SheetContentOptions.IsExpanded=true;drawing.SheetContentList.SelectedIndex=1;
                await Reveal(drawing.SheetContentList,"sheet-content-"+language);
                drawing.FrameZonesOptions.IsExpanded=true;await Reveal(drawing.FrameZonesEnabled,"frame-zones-"+language);
                if(drawing.SaveTemplateButton.Content?.ToString()!=Cadoryx.Lang.Strings.Strings.DrawingSaveTemplate||
                    drawing.SheetContentKind.Items.Count!=2||drawing.TemplateLibraryChoice.Items.Count!=1)
                    throw new InvalidOperationException("Template library controls missing or not localized: "+language);
                drawing.Close();
            }
            bindingOutput.Flush();
            if(new FileInfo(Path.Combine(output,"bindings.log")).Length>0)
                throw new InvalidOperationException("Drawing window has binding errors.");
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new
                {passed=true,sheets=1,views=3,dimensions=1,sectionHatchCount=hatchCount,detailHatchCount,
                    datumSymbols=1,controlFrames=1,balloons=1,partsQuantity=2,libraryEntries=1,customFields=1,notes=1,frameZones="8x6",libraryFile,
                    hatchRules="default edit / material disable / remove / detail refresh",templates,pdf,native},new JsonSerializerOptions{WriteIndented=true}));
            Application.Current.Shutdown(0);
        }
        catch(Exception error)
        {
            var failed=Application.Current.Windows.OfType<TechnicalDrawingWindow>().SingleOrDefault();
            var snapshot=((MainWindowViewModel)window.DataContext).ActiveDocument?.Session.Snapshot;
            object? state=failed is null?null:new{enabled=failed.IsEnabled,status=failed.Status.Text,
                requestedStandard=failed.StandardChoice.SelectedItem,requestedLanguage=failed.CaptionLanguageChoice.SelectedItem,
                sheets=snapshot?.DrawingSheets.Values.Select(s=>new{s.Standard,language=s.Template?.Language,s.WidthMm,s.HeightMm,views=s.Views.Length}),
                processCpuSeconds=Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds};
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new
                {passed=false,error=error.ToString(),state},new JsonSerializerOptions{WriteIndented=true}));
            Application.Current.Shutdown(1);
        }
        finally{PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);CultureInfo.CurrentUICulture=originalCulture;I18NExtension.Culture=originalMarkup;}
    }
    private static async Task Wait(Func<bool> condition)
    {
        var watch=Stopwatch.StartNew();
        while(watch.Elapsed<TimeSpan.FromSeconds(30))
        {
            await Idle();
            if(condition()&&!Application.Current.Windows.OfType<TechnicalDrawingWindow>().Any(d=>d.IsCalculating))return;
            await Task.Delay(20);
        }
        var drawing=Application.Current.Windows.OfType<TechnicalDrawingWindow>().FirstOrDefault();
        throw new TimeoutException($"Drawing command did not commit within 30 seconds; calculating={drawing?.IsCalculating}, status={drawing?.Status.Text}.");
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
