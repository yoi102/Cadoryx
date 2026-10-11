using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Lang.Strings;

namespace Cadoryx.wpf.Views;

public partial class TechnicalDrawingWindow
{
    private DrawingTopologyReference? annotationSource;
    private DrawingTopologyReference? annotationOpposite;
    private bool pickingOppositeAnnotation;
    private bool annotationPreparing;
    private long annotationGeneration;
    private readonly CancellationTokenSource annotationLifetime=new();
    private sealed record AnnotationChoice(int Kind,string Name);
    private sealed record CharacteristicChoice(DrawingGeometricCharacteristic Kind,string Name);
    private sealed record TargetChoice(DrawingToleranceTarget Kind,string Name);
    private sealed record ConditionChoice(DrawingMaterialCondition Kind,string Name);
    private sealed record ManufacturingDatumChoice(DrawingDatumFeatureType Kind,string Name);
    private sealed record AnnotationRow(Guid Id,int Kind,object Value,string Name)
    {public override string ToString()=>Name;}
    private void InitializeAnnotations()
    {
        AnnotationType.ItemsSource=new[]{new AnnotationChoice(0,Strings.DrawingDatumSymbol),new AnnotationChoice(1,Strings.DrawingControlFrame),new AnnotationChoice(2,Strings.DrawingBalloon)};
        AnnotationType.SelectedIndex=0;
        AnnotationType.SelectionChanged+=(_,_)=>ManufacturingDatumPanel.Visibility=AnnotationType.SelectedIndex==0?Visibility.Visible:Visibility.Collapsed;
        ManufacturingDatumType.ItemsSource=new[]{new ManufacturingDatumChoice(DrawingDatumFeatureType.Surface,Strings.DrawingManufacturingSurface),
            new ManufacturingDatumChoice(DrawingDatumFeatureType.CylinderSizeAxis,Strings.DrawingManufacturingSizeAxis),
            new ManufacturingDatumChoice(DrawingDatumFeatureType.PlanarSizeMedianPlane,Strings.DrawingManufacturingMedianPlane)};ManufacturingDatumType.SelectedIndex=0;
        AnnotationDatumKind.ItemsSource=new[]{new DatumChoice(TopologyKind.Face,Strings.DrawingDatumFace),new DatumChoice(TopologyKind.Edge,Strings.DrawingDatumEdge)};AnnotationDatumKind.SelectedIndex=0;
        Characteristic.ItemsSource=Enum.GetValues<DrawingGeometricCharacteristic>().Select(kind=>new CharacteristicChoice(kind,Strings.ResourceManager.GetString("DrawingChar"+kind)??kind.ToString())).ToArray();Characteristic.SelectedIndex=0;
        ToleranceTarget.ItemsSource=new[]{new TargetChoice(DrawingToleranceTarget.Surface,Strings.DrawingTargetSurface),new TargetChoice(DrawingToleranceTarget.Axis,Strings.DrawingTargetAxis),
            new TargetChoice(DrawingToleranceTarget.MedianPlane,Strings.DrawingTargetMedianPlane)};ToleranceTarget.SelectedIndex=0;
        PlanarSizeKind.ItemsSource=new[]{new AnnotationChoice(0,Strings.DrawingExternalThickness),new AnnotationChoice(1,Strings.DrawingInternalWidth)};PlanarSizeKind.SelectedIndex=0;
        ManufacturingDatumType.SelectionChanged+=(_,_)=>UpdatePlanarSizePanel();
        ToleranceTarget.SelectionChanged+=(_,_)=>UpdatePlanarSizePanel();
        AnnotationType.SelectionChanged+=(_,_)=>UpdatePlanarSizePanel();
        PlanarSizeKind.SelectionChanged+=(_,_)=>annotationGeneration++;
        UpdatePlanarSizePanel();
        MaterialCondition.ItemsSource=new[]{new ConditionChoice(DrawingMaterialCondition.None,Strings.DrawingConditionNone),new ConditionChoice(DrawingMaterialCondition.Maximum,Strings.DrawingConditionMaximum),new ConditionChoice(DrawingMaterialCondition.Least,Strings.DrawingConditionLeast)};MaterialCondition.SelectedIndex=0;
        document.DrawingTopologyPicked+=OnAnnotationPicked;
        Closed+=(_,_)=>{annotationGeneration++;annotationLifetime.Cancel();document.DrawingTopologyPicked-=OnAnnotationPicked;};
    }
    private void UpdatePlanarSizePanel()
    {
        if(PlanarSizePanel is null)return;
        bool planar=AnnotationType.SelectedIndex==0?(ManufacturingDatumType.SelectedItem as ManufacturingDatumChoice)?.Kind==DrawingDatumFeatureType.PlanarSizeMedianPlane:
            AnnotationType.SelectedIndex==1&&(ToleranceTarget.SelectedItem as TargetChoice)?.Kind==DrawingToleranceTarget.MedianPlane;
        PlanarSizePanel.Visibility=planar?Visibility.Visible:Visibility.Collapsed;annotationGeneration++;
    }
    private void RenderAnnotations()
    {
        var selected=(AnnotationList.SelectedItem as AnnotationRow)?.Id;var sheet=Sheet;
        AnnotationList.ItemsSource=sheet is null?Array.Empty<AnnotationRow>():
            sheet.DatumSymbols.Select(d=>new AnnotationRow(d.Id,0,d,$"{Strings.DrawingDatumSymbol} {d.Name}"))
            .Concat(sheet.ControlFrames.Select(f=>new AnnotationRow(f.Id,1,f,$"{Strings.ResourceManager.GetString("DrawingChar"+f.Characteristic)} {f.ToleranceMm:G6} mm")))
            .Concat(sheet.Balloons.Select(b=>new AnnotationRow(b.Id,2,b,$"{Strings.DrawingBalloon} {b.ItemNumber} · {b.PartName} × {b.Quantity}"))).ToArray();
        AnnotationList.SelectedItem=AnnotationList.Items.OfType<AnnotationRow>().FirstOrDefault(r=>r.Id==selected);
        ManufacturingStandardRule.Text=sheet?.Standard==DrawingStandard.ASME?Strings.DrawingAsmeSizeRule:Strings.DrawingIsoSizeRule;
    }
    private void AnnotationChanged(object sender,SelectionChangedEventArgs e)
    {
        if(AnnotationList.SelectedItem is not AnnotationRow row)return;AnnotationType.SelectedIndex=row.Kind;
        var (view,source,position)=row.Value switch
        {
            DrawingDatumSymbol d=>(d.ViewId,d.Source,d.PositionMm),DrawingFeatureControlFrame f=>(f.ViewId,f.Source,f.PositionMm),
            DrawingAssemblyBalloon b=>(b.ViewId,b.Source,b.PositionMm),_=>throw new InvalidOperationException()
        };
        ViewList.SelectedItem=Sheet?.Views.FirstOrDefault(v=>v.Id==view);annotationSource=source;
        annotationGeneration++;pickingOppositeAnnotation=false;
        var planar=row.Value switch{DrawingDatumSymbol d=>d.PlanarSizeEvidence,DrawingFeatureControlFrame f=>f.PlanarSizeEvidence,_=>null};
        annotationOpposite=planar?.OppositeSource;OppositeAnnotationEvidence.Text=annotationOpposite is {} opposite?Evidence(opposite):"—";
        PlanarSizeKind.SelectedIndex=planar?.Internal==true?1:0;PlanarSizeEvidence.Text=planar is not null?$"{planar.WidthMm:G6} mm · {Strings.DrawingTargetMedianPlane}":"—";
        AnnotationEvidence.Text=source.HistoryOrigin is not null?Strings.DrawingAttachmentContinued:$"{source.Geometry} · {source.FullTopologyIndex} · {source.Fingerprint[..12]}";
        AnnotationX.Text=position.X.ToString(CultureInfo.CurrentCulture);AnnotationY.Text=position.Y.ToString(CultureInfo.CurrentCulture);
        if(row.Value is DrawingDatumSymbol datum)
        {AnnotationName.Text=datum.Name;ManufacturingDatumType.SelectedItem=ManufacturingDatumType.Items.OfType<ManufacturingDatumChoice>().Single(x=>x.Kind==datum.FeatureType);}
        if(row.Value is DrawingAssemblyBalloon balloon)AnnotationName.Text=balloon.ItemNumber.ToString(CultureInfo.CurrentCulture);
        if(row.Value is DrawingFeatureControlFrame frame)
        {
            Characteristic.SelectedItem=Characteristic.Items.OfType<CharacteristicChoice>().Single(x=>x.Kind==frame.Characteristic);
            AnnotationTolerance.Text=frame.ToleranceMm.ToString("G",CultureInfo.CurrentCulture);
            ToleranceTarget.SelectedItem=ToleranceTarget.Items.OfType<TargetChoice>().Single(x=>x.Kind==frame.Target);
            MaterialCondition.SelectedItem=MaterialCondition.Items.OfType<ConditionChoice>().Single(x=>x.Kind==frame.MaterialCondition);
            DiameterZone.IsChecked=frame.DiameterZone;OrderedDatums.Text=DrawingDatumSystemText.Format(frame,Sheet!.DatumSymbols);
        }
    }
    private void PickAnnotation(object sender,RoutedEventArgs e)
    {
        if(View is null)return;holePicking=false;annotationGeneration++;pickingOppositeAnnotation=false;
        annotationSource=null;annotationOpposite=null;AnnotationEvidence.Text="—";OppositeAnnotationEvidence.Text="—";PlanarSizeEvidence.Text="—";
        document.RequestDrawingTopologyPick(((DatumChoice)AnnotationDatumKind.SelectedItem).Kind);Owner?.Activate();
    }
    private void PickOppositeAnnotation(object sender,RoutedEventArgs e)
    {
        if(View is null||annotationSource is null){Status.Text=Strings.DrawingPlanarSizeRequirements;return;}
        annotationGeneration++;holePicking=false;pickingOppositeAnnotation=true;annotationOpposite=null;OppositeAnnotationEvidence.Text="—";PlanarSizeEvidence.Text="—";
        document.RequestDrawingTopologyPick(TopologyKind.Face);Owner?.Activate();
    }
    private void CancelAnnotationPick(object sender,RoutedEventArgs e)
    {annotationGeneration++;pickingOppositeAnnotation=false;document.CancelDrawingDatumPick();}
    private static string Evidence(DrawingTopologyReference reference)=>reference.HistoryOrigin is not null?Strings.DrawingAttachmentContinued:
        $"{reference.Geometry} · {reference.FullTopologyIndex} · {reference.Fingerprint[..12]}";
    private void OnAnnotationPicked(object? sender,DrawingTopologyReference reference)
    {
        if(holePicking)return;annotationGeneration++;
        if(pickingOppositeAnnotation){annotationOpposite=reference;OppositeAnnotationEvidence.Text=Evidence(reference);pickingOppositeAnnotation=false;}
        else{annotationSource=reference;AnnotationEvidence.Text=Evidence(reference);}
        Status.Text=string.Empty;Activate();
    }
    private async Task StoreAnnotation(bool replace)
    {
        if(annotationPreparing)return;
        annotationPreparing=true;AddAnnotationButton.IsEnabled=false;ApplyAnnotationButton.IsEnabled=false;
        try
        {
        await ExecutePreparedAsync(async token=>
        {
        if(Sheet is not {} sheet||View is not {} view||annotationSource is null)throw new CadValidationException("Select a view and exact attachment.");
        var row=AnnotationList.SelectedItem as AnnotationRow;int kind=((AnnotationChoice)AnnotationType.SelectedItem).Kind;
        if(replace&&(row is null||row.Kind!=kind))throw new CadValidationException("Select an annotation of the same type to edit.");
        Guid id=replace?row!.Id:Guid.NewGuid();var position=new Point2d(Number(AnnotationX),Number(AnnotationY));ICadDocumentCommand command;
        long generation=annotationGeneration;var firstFace=annotationSource;var opposite=annotationOpposite;
        bool planar=kind==0?((ManufacturingDatumChoice)ManufacturingDatumType.SelectedItem).Kind==DrawingDatumFeatureType.PlanarSizeMedianPlane:
            kind==1&&((TargetChoice)ToleranceTarget.SelectedItem).Kind==DrawingToleranceTarget.MedianPlane;
        DrawingPlanarSizeEvidence? proof=null;
        if(planar)
        {
            if(opposite is null)throw new CadValidationException(Strings.DrawingPlanarSizeRequirements);
            proof=await document.InspectDrawingPlanarSizeAsync(firstFace,opposite,PlanarSizeKind.SelectedIndex==1,token);
            if(annotationLifetime.IsCancellationRequested||generation!=annotationGeneration||firstFace!=annotationSource||opposite!=annotationOpposite||
                Sheet?.Id!=sheet.Id||View?.Id!=view.Id)throw new OperationCanceledException();
        }
        if(kind==0)command=DrawingAnnotationCommands.PutDatum(sheet.Id,new DrawingDatumSymbol(id,view.Id,AnnotationName.Text,annotationSource,position)
            {FeatureType=((ManufacturingDatumChoice)ManufacturingDatumType.SelectedItem).Kind,PlanarSizeEvidence=proof},replace);
        else if(kind==2)command=DrawingAnnotationCommands.PutBalloon(sheet.Id,new(id,view.Id,annotationSource,position,int.Parse(AnnotationName.Text,CultureInfo.CurrentCulture)),replace);
        else
        {
            var datums=DrawingDatumSystemText.Parse(OrderedDatums.Text,sheet.DatumSymbols);
            command=DrawingAnnotationCommands.PutControlFrame(sheet.Id,new DrawingFeatureControlFrame(id,view.Id,annotationSource,position,
                ((CharacteristicChoice)Characteristic.SelectedItem).Kind,Number(AnnotationTolerance),((TargetChoice)ToleranceTarget.SelectedItem).Kind,
                DiameterZone.IsChecked==true,((ConditionChoice)MaterialCondition.SelectedItem).Kind){DatumSystem=datums,PlanarSizeEvidence=proof},replace);
        }
        return command;
        });
        }
        finally{annotationPreparing=false;if(!closed){AddAnnotationButton.IsEnabled=true;ApplyAnnotationButton.IsEnabled=true;}}
    }
    private async void AddAnnotation(object sender,RoutedEventArgs e)
    {try{await StoreAnnotation(false);}catch(Exception error){if(!closed)Status.Text=error.Message;}}
    private async void ApplyAnnotation(object sender,RoutedEventArgs e)
    {try{await StoreAnnotation(true);}catch(Exception error){if(!closed)Status.Text=error.Message;}}
    private async void MoveAnnotation(object sender,RoutedEventArgs e)
    {try{if(Sheet is {} sheet&&AnnotationList.SelectedItem is AnnotationRow row)await ExecuteAsync(DrawingAnnotationCommands.Move(sheet.Id,row.Id,new(Number(AnnotationX),Number(AnnotationY))));}catch(Exception error){Status.Text=error.Message;}}
    private async void RenameAnnotationDatum(object sender,RoutedEventArgs e)
    {if(Sheet is {} sheet&&AnnotationList.SelectedItem is AnnotationRow {Kind:0} row)await ExecuteAsync(DrawingAnnotationCommands.RenameDatum(sheet.Id,row.Id,AnnotationName.Text));}
    private async void DeleteAnnotation(object sender,RoutedEventArgs e)
    {if(Sheet is {} sheet&&AnnotationList.SelectedItem is AnnotationRow row)await ExecuteAsync(DrawingAnnotationCommands.Delete(sheet.Id,row.Id));}
    private async void PlacePartsTable(object sender,RoutedEventArgs e)
    {try{if(Sheet is {} sheet)await ExecuteAsync(DrawingAnnotationCommands.SetPartsTable(sheet.Id,new(Number(PartsTableX),Number(PartsTableY))));}catch(Exception error){Status.Text=error.Message;}}
    private async void RemovePartsTable(object sender,RoutedEventArgs e)
    {if(Sheet is {} sheet)await ExecuteAsync(DrawingAnnotationCommands.SetPartsTable(sheet.Id,null));}
}
