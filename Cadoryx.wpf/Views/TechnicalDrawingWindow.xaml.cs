using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.IO;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Controls;
using Microsoft.Win32;
using OcctSharp;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.wpf.Views;

public partial class TechnicalDrawingWindow
{
    private readonly CadDocumentViewModel document;
    private AssemblyDatumReference? first,second;
    private bool pickingFirst;
    private sealed record ViewChoice(DrawingViewKind Kind,string Name);
    private sealed record MeasureChoiceItem(DrawingMeasureKind Kind,string Name);
    private sealed record DatumChoice(TopologyKind Kind,string Name);
    private sealed record UnitChoiceItem(LengthUnit Unit,string Name);
    public TechnicalDrawingWindow(CadDocumentViewModel document)
    {
        InitializeComponent();this.document=document;
        PaperHost.LayoutTransform=new ScaleTransform(ZoomSlider.Value,ZoomSlider.Value);
        var strings=Cadoryx.Lang.Strings.Strings.ResourceManager;
        KindChoice.ItemsSource=Enum.GetValues<DrawingViewKind>().Select(kind=>new ViewChoice(kind,
            strings.GetString("DrawingView"+kind)??kind.ToString())).ToArray();KindChoice.SelectedIndex=0;
        MeasureChoice.ItemsSource=Enum.GetValues<DrawingMeasureKind>().Select(kind=>new MeasureChoiceItem(kind,
            strings.GetString("DrawingMeasure"+kind)??kind.ToString())).ToArray();MeasureChoice.SelectedIndex=0;
        DatumKindChoice.ItemsSource=new[]{new DatumChoice(TopologyKind.Face,strings.GetString("DrawingDatumFace")??"Face"),
            new DatumChoice(TopologyKind.Edge,strings.GetString("DrawingDatumEdge")??"Edge")};DatumKindChoice.SelectedIndex=0;
        UnitChoice.ItemsSource=new[]{new UnitChoiceItem(LengthUnit.Millimeter,"mm"),
            new UnitChoiceItem(LengthUnit.Centimeter,"cm"),new UnitChoiceItem(LengthUnit.Meter,"m"),
            new UnitChoiceItem(LengthUnit.Inch,"in")};UnitChoice.DisplayMemberPath="Name";
        UnitChoice.SelectedIndex=0;
        StandardChoice.ItemsSource=Enum.GetValues<DrawingStandard>();StandardChoice.SelectedIndex=0;
        document.Session.Changed+=OnDocumentChanged;
        document.Detaching+=OnDocumentDetaching;
        document.DrawingDatumPicked+=OnDatumPicked;
        document.DrawingDatumPickFailed+=OnDatumFailed;
        Closed+=(_,_)=>
        {
            document.CancelDrawingDatumPick();
            document.Session.Changed-=OnDocumentChanged;
            document.Detaching-=OnDocumentDetaching;
            document.DrawingDatumPicked-=OnDatumPicked;
            document.DrawingDatumPickFailed-=OnDatumFailed;
        };
        RenderBook();
    }
    private TechnicalDrawingSheet? Sheet=>SheetList.SelectedItem as TechnicalDrawingSheet;
    private TechnicalDrawingView? View=>ViewList.SelectedItem as TechnicalDrawingView;
    private TechnicalDrawingDimension? Dimension=>DimensionList.SelectedItem as TechnicalDrawingDimension;
    private void OnDocumentDetaching(object? sender,EventArgs e)=>Close();
    private void OnDocumentChanged(object? sender,DocumentChangeSet change)=>Dispatcher.Invoke(RenderBook);
    private void RenderBook()
    {
        var sheetId=Sheet?.Id;var viewId=View?.Id;var dimensionId=Dimension?.Id;
        var sheets=document.Session.Snapshot.DrawingSheets.Values.OrderBy(s=>s.Name,StringComparer.CurrentCulture).ToArray();
        SheetList.ItemsSource=sheets;
        SheetList.SelectedItem=sheets.FirstOrDefault(s=>s.Id==sheetId)??sheets.FirstOrDefault();
        if(Sheet is {} sheet)
        {
            ViewList.ItemsSource=sheet.Views;
            ViewList.SelectedItem=sheet.Views.FirstOrDefault(v=>v.Id==viewId);
            DimensionList.ItemsSource=sheet.Dimensions;
            DimensionList.SelectedItem=sheet.Dimensions.FirstOrDefault(d=>d.Id==dimensionId);
            ShowPage(sheet,Array.FindIndex(sheets,s=>s.Id==sheet.Id)+1,sheets.Length);
            Status.Text=sheet.Views.FirstOrDefault(v=>v.StaleReason is not null)?.StaleReason??
                sheet.Dimensions.FirstOrDefault(d=>d.StaleReason is not null)?.StaleReason??string.Empty;
        }
        else{ViewList.ItemsSource=null;DimensionList.ItemsSource=null;PaperHost.Child=null;}
        UpdateActions();
    }
    private void UpdateActions()
    {
        AddViewButton.IsEnabled=Sheet is not null;
        ApplySheetButton.IsEnabled=Sheet is not null;
        PickFirstButton.IsEnabled=View is not null;
        PickSecondButton.IsEnabled=View is not null;
        AddDimensionButton.IsEnabled=View is not null;
    }
    private void SheetChanged(object sender,SelectionChangedEventArgs e)
    {
        if(Sheet is not {} sheet){PaperHost.Child=null;return;}
        if(SheetList.IsKeyboardFocusWithin||SheetList.IsMouseOver)InspectorTabs.SelectedIndex=0;
        SheetName.Text=sheet.Name;SheetTitle.Text=sheet.Title;SheetAuthor.Text=sheet.Author??"";
        UnitChoice.SelectedItem=UnitChoice.Items.OfType<UnitChoiceItem>().First(item=>item.Unit==sheet.Unit);
        StandardChoice.SelectedItem=sheet.Standard;
        PaperWidth.Text=sheet.WidthMm.ToString(CultureInfo.CurrentCulture);
        PaperHeight.Text=sheet.HeightMm.ToString(CultureInfo.CurrentCulture);
        ViewList.ItemsSource=sheet.Views;DimensionList.ItemsSource=sheet.Dimensions;
        if(sheet.Views.IsEmpty)
        {
            ViewX.Text=(sheet.WidthMm/2).ToString(CultureInfo.CurrentCulture);
            ViewY.Text=(sheet.HeightMm/2).ToString(CultureInfo.CurrentCulture);
            ViewScale.Text="1";
        }
        if(sheet.Dimensions.IsEmpty)
        {
            DimensionX.Text=(sheet.WidthMm/2).ToString(CultureInfo.CurrentCulture);
            DimensionY.Text=(sheet.HeightMm/2).ToString(CultureInfo.CurrentCulture);
        }
        var sheets=document.Session.Snapshot.DrawingSheets.Values.OrderBy(s=>s.Name,StringComparer.CurrentCulture).ToArray();
        ShowPage(sheet,Math.Max(1,Array.FindIndex(sheets,s=>s.Id==sheet.Id)+1),sheets.Length);
        UpdateActions();
    }
    private void ViewChanged(object sender,SelectionChangedEventArgs e)
    {
        if(View is not {} view)return;
        if(ViewList.IsKeyboardFocusWithin||ViewList.IsMouseOver)InspectorTabs.SelectedIndex=1;
        ViewX.Text=view.CenterMm.X.ToString(CultureInfo.CurrentCulture);
        ViewY.Text=view.CenterMm.Y.ToString(CultureInfo.CurrentCulture);
        ViewScale.Text=view.Scale.ToString(CultureInfo.CurrentCulture);
        KindChoice.SelectedItem=KindChoice.Items.OfType<ViewChoice>().First(choice=>choice.Kind==view.Kind);
        if(view.DetailCenter is {} focus)
        {
            DetailX.Text=focus.X.ToString(CultureInfo.CurrentCulture);
            DetailY.Text=focus.Y.ToString(CultureInfo.CurrentCulture);
            DetailRadius.Text=view.DetailRadius.ToString(CultureInfo.CurrentCulture);
        }
        UpdateActions();
    }
    private void KindChanged(object sender,SelectionChangedEventArgs e)
    {
        if(SectionOptions is null)return;
        SectionOptions.Visibility=(KindChoice.SelectedItem as ViewChoice)?.Kind==DrawingViewKind.Section?
            Visibility.Visible:Visibility.Collapsed;
        DetailOptions.Visibility=(KindChoice.SelectedItem as ViewChoice)?.Kind==DrawingViewKind.Detail?
            Visibility.Visible:Visibility.Collapsed;
    }
    private void MeasureChanged(object sender,SelectionChangedEventArgs e)
    {
        if(PickSecondButton is null||SecondEvidence is null)return;
        var needsSecond=(MeasureChoice.SelectedItem as MeasureChoiceItem)?.Kind is
            DrawingMeasureKind.Length or DrawingMeasureKind.Angle;
        PickSecondButton.Visibility=needsSecond?Visibility.Visible:Visibility.Collapsed;
        SecondEvidence.Visibility=PickSecondButton.Visibility;
        if(!needsSecond){second=null;SecondEvidence.Text="—";}
    }
    private void DimensionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(Dimension is not {} dimension)return;
        if(DimensionList.IsKeyboardFocusWithin||DimensionList.IsMouseOver)InspectorTabs.SelectedIndex=2;
        first=dimension.First;second=dimension.Second;
        FirstEvidence.Text=Evidence(first);SecondEvidence.Text=Evidence(second);
        DimensionX.Text=dimension.TextPositionMm.X.ToString(CultureInfo.CurrentCulture);
        DimensionY.Text=dimension.TextPositionMm.Y.ToString(CultureInfo.CurrentCulture);
        DimensionColor.Text=$"#{dimension.Argb:X8}";
        MeasureChoice.SelectedItem=MeasureChoice.Items.OfType<MeasureChoiceItem>().First(choice=>choice.Kind==dimension.Kind);
        ToleranceUpper.Text=dimension.UpperTolerance.ToString(CultureInfo.CurrentCulture);
        ToleranceLower.Text=dimension.LowerTolerance.ToString(CultureInfo.CurrentCulture);
    }
    private string Evidence(AssemblyDatumReference? datum)
    {
        if(datum is null)return "—";
        var instance=document.Session.Snapshot.EnumerateOccurrences().FirstOrDefault(o=>o.Path.Equals(datum.Path));
        return $"{instance?.Name??datum.Path.Slots[^1].Value.ToString("N")[..8]} · {datum.Geometry} · #{datum.FullTopologyIndex}";
    }
    private static double Number(TextBox box)=>double.TryParse(box.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out var n)?n:
        throw new CadValidationException("Enter a valid number.");
    private uint ColorValue()=>uint.TryParse(DimensionColor.Text.Trim().TrimStart('#'),NumberStyles.HexNumber,
        CultureInfo.InvariantCulture,out var value)?value:throw new CadValidationException("Enter an eight-digit ARGB color.");
    private void ShowPage(TechnicalDrawingSheet sheet,int number,int count)
    {
        var canvas=new DrawingSheetCanvas(DrawingPageLayout.Create(sheet,number,count));
        canvas.PaperClicked+=OnPaperClicked;PaperHost.Child=canvas;
    }
    private async void OnPaperClicked(object? sender,Point2d point)
    {
        DimensionX.Text=point.X.ToString("0.##",CultureInfo.CurrentCulture);
        DimensionY.Text=point.Y.ToString("0.##",CultureInfo.CurrentCulture);
        if(Sheet is {} sheet&&Dimension is {} dimension)
            await ExecuteAsync(TechnicalDrawingCommands.MoveDimension(sheet.Id,dimension.Id,point));
    }
    private async Task ExecuteAsync(ICadDocumentCommand command)
    {
        try{IsEnabled=false;Status.Text="…";await document.Session.ExecuteAsync(command);RenderBook();}
        catch(Exception error){Status.Text=error.Message;}
        finally{IsEnabled=true;}
    }
    private async void AddSheet(object sender,RoutedEventArgs e)
    {
        InspectorTabs.SelectedIndex=0;
        await ExecuteAsync(TechnicalDrawingCommands.AddSheet(
            TechnicalDrawingSheet.A4Landscape($"Sheet {document.Session.Snapshot.DrawingSheets.Count+1}")));
    }
    private async void DeleteSheet(object sender,RoutedEventArgs e)
    {if(Sheet is {} sheet)await ExecuteAsync(TechnicalDrawingCommands.DeleteSheet(sheet.Id));}
    private async void ApplySheet(object sender,RoutedEventArgs e)
    {
        try{if(Sheet is {} sheet)await ExecuteAsync(TechnicalDrawingCommands.EditSheet(sheet.Id,SheetName.Text,
            Number(PaperWidth),Number(PaperHeight),((UnitChoiceItem)UnitChoice.SelectedItem).Unit,SheetTitle.Text,SheetAuthor.Text,
            (DrawingStandard)StandardChoice.SelectedItem));}
        catch(Exception error){Status.Text=error.Message;}
    }
    private async void AddView(object sender,RoutedEventArgs e)
    {
        if(Sheet is not {} sheet)return;
        InspectorTabs.SelectedIndex=1;
        try
        {
            var kind=((ViewChoice)KindChoice.SelectedItem).Kind;
            var center=new Point2d(string.IsNullOrWhiteSpace(ViewX.Text)?sheet.WidthMm/2:Number(ViewX),
                string.IsNullOrWhiteSpace(ViewY.Text)?sheet.HeightMm/2:Number(ViewY));
            var scale=string.IsNullOrWhiteSpace(ViewScale.Text)?1:Number(ViewScale);
            Guid? parent=kind==DrawingViewKind.Detail||LinkParentCheck.IsChecked==true?
                View?.Id??throw new CadValidationException("Select a parent drawing view."):null;
            Vector3d? normal=kind==DrawingViewKind.Section?
                new Vector3d(Number(NormalX),Number(NormalY),Number(NormalZ)).Normalized():null;
            await ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),
                $"{kind} {sheet.Views.Length+1}",kind,center,scale,document.Selection.Occurrence,
                parentView:parent,sectionNormal:normal,sectionOffsetMm:kind==DrawingViewKind.Section?Number(SectionOffset):0,
                detailCenter:kind==DrawingViewKind.Detail?new(Number(DetailX),Number(DetailY)):null,
                detailRadius:kind==DrawingViewKind.Detail?Number(DetailRadius):0));
        }
        catch(Exception error){Status.Text=error.Message;}
    }
    private async void EditView(object sender,RoutedEventArgs e)
    {
        try{if(Sheet is {} sheet&&View is {} view)await ExecuteAsync(TechnicalDrawingCommands.EditView(
            sheet.Id,view.Id,new(Number(ViewX),Number(ViewY)),Number(ViewScale)));}
        catch(Exception error){Status.Text=error.Message;}
    }
    private async void ReselectView(object sender,RoutedEventArgs e)
    {if(Sheet is {} sheet&&View is {} view)await ExecuteAsync(TechnicalDrawingCommands.ReselectView(sheet.Id,view.Id,document.Selection.Occurrence));}
    private async void DeleteView(object sender,RoutedEventArgs e)
    {if(Sheet is {} sheet&&View is {} view)await ExecuteAsync(TechnicalDrawingCommands.DeleteView(sheet.Id,view.Id));}
    private async void RefreshViews(object sender,RoutedEventArgs e)=>await ExecuteAsync(TechnicalDrawingCommands.Refresh());
    private void PickFirst(object sender,RoutedEventArgs e)=>Pick(true);
    private void PickSecond(object sender,RoutedEventArgs e)=>Pick(false);
    private void Pick(bool isFirst)
    {
        InspectorTabs.SelectedIndex=2;
        pickingFirst=isFirst;Status.Text=Cadoryx.Lang.Strings.Strings.ResourceManager.GetString("DrawingStatusPick")??"Pick a face or edge.";
        document.RequestDrawingDatumPick(((DatumChoice)DatumKindChoice.SelectedItem).Kind);
        Owner?.Activate();
    }
    private void OnDatumPicked(object? sender,AssemblyDatumReference datum)
    {
        if(pickingFirst){first=datum;FirstEvidence.Text=Evidence(datum);}
        else{second=datum;SecondEvidence.Text=Evidence(datum);}
        Status.Text=string.Empty;Activate();
    }
    private void OnDatumFailed(object? sender,string message){Status.Text=message;Activate();}
    private async Task StoreDimension(bool replace)
    {
        if(Sheet is not {} sheet||View is not {} view||first is null)throw new CadValidationException("Select a view and first datum.");
        var kind=((MeasureChoiceItem)MeasureChoice.SelectedItem).Kind;
        var dimension=new TechnicalDrawingDimension(replace?Dimension?.Id??throw new CadValidationException("Select a dimension."):Guid.NewGuid(),
            view.Id,kind,first,second,new(Number(DimensionX),Number(DimensionY)),0,ColorValue());
        await ExecuteAsync(replace?TechnicalDrawingCommands.ReselectDimension(sheet.Id,dimension):
            TechnicalDrawingCommands.AddDimension(sheet.Id,dimension));
    }
    private async void AddDimension(object sender,RoutedEventArgs e)
    {InspectorTabs.SelectedIndex=2;try{await StoreDimension(false);}catch(Exception error){Status.Text=error.Message;}}
    private async void ReselectDimension(object sender,RoutedEventArgs e)
    {try{await StoreDimension(true);}catch(Exception error){Status.Text=error.Message;}}
    private async void DeleteDimension(object sender,RoutedEventArgs e)
    {if(Sheet is {} sheet&&Dimension is {} dimension)await ExecuteAsync(TechnicalDrawingCommands.DeleteDimension(sheet.Id,dimension.Id));}
    private async void ApplyDimension(object sender,RoutedEventArgs e)
    {
        try{if(Sheet is {} sheet&&Dimension is {} dimension)
            await ExecuteAsync(TechnicalDrawingCommands.MoveDimension(sheet.Id,dimension.Id,
                new(Number(DimensionX),Number(DimensionY)),ColorValue()));}
        catch(Exception error){Status.Text=error.Message;}
    }
    private async void ApplyTolerance(object sender,RoutedEventArgs e)
    {
        try{if(Sheet is {} sheet&&Dimension is {} dimension)
            await ExecuteAsync(TechnicalDrawingCommands.SetDimensionTolerance(sheet.Id,dimension.Id,
                Number(ToleranceUpper),Number(ToleranceLower)));}
        catch(Exception error){Status.Text=error.Message;}
    }
    private static void EnsureCurrent(DocumentSnapshot snapshot)
    {
        if(snapshot.DrawingSheets.Values.SelectMany(s=>s.Views).Any(v=>v.StaleReason is not null)||
           snapshot.DrawingSheets.Values.SelectMany(s=>s.Dimensions).Any(d=>d.StaleReason is not null))
            throw new CadValidationException("Refresh or reselect stale drawing references before delivery.");
    }
    private void ExportPdf(object sender,RoutedEventArgs e)
    {
        try
        {
            var snapshot=document.Session.Snapshot;EnsureCurrent(snapshot);
            var picker=new SaveFileDialog{Filter="PDF|*.pdf",DefaultExt=".pdf",AddExtension=true,
                FileName=snapshot.Name+".pdf",OverwritePrompt=true};
            if(picker.ShowDialog(this)!=true)return;
            TechnicalDrawingPdf.Write(snapshot,picker.FileName);Status.Text=Path.GetFileName(picker.FileName);
        }
        catch(Exception error){Status.Text=error.Message;}
    }
    private void Print(object sender,RoutedEventArgs e)
    {
        try
        {
            var snapshot=document.Session.Snapshot;EnsureCurrent(snapshot);
            var dialog=new PrintDialog();if(dialog.ShowDialog()!=true)return;
            var sheets=snapshot.DrawingSheets.Values.OrderBy(s=>s.Name,StringComparer.Ordinal).ThenBy(s=>s.Id).ToArray();
            var printDocument=new FixedDocument();
            for(int index=0;index<sheets.Length;index++)
            {
                var page=DrawingPageLayout.Create(sheets[index],index+1,sheets.Length);
                var fixedPage=new FixedPage{Width=page.WidthMm*DrawingSheetCanvas.PixelsPerMm,
                    Height=page.HeightMm*DrawingSheetCanvas.PixelsPerMm};
                fixedPage.Children.Add(new DrawingSheetCanvas(page));
                var content=new PageContent();((System.Windows.Markup.IAddChild)content).AddChild(fixedPage);
                printDocument.Pages.Add(content);
            }
            dialog.PrintDocument(printDocument.DocumentPaginator,snapshot.Name);
        }
        catch(Exception error){Status.Text=error.Message;}
    }
    private void ZoomChanged(object sender,RoutedPropertyChangedEventArgs<double> e)
    {
        if(PaperHost is not null)PaperHost.LayoutTransform=new ScaleTransform(e.NewValue,e.NewValue);
    }
}
