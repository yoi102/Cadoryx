using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Lang.Strings;

namespace Cadoryx.wpf.Views;

public partial class TechnicalDrawingWindow
{
    private DrawingTopologyReference? holeSource;private bool holePicking;
    private readonly CancellationTokenSource holeLifetime=new();
    private sealed record HoleCoordinateChoice(Guid? Id,string Name);
    private bool holeChoiceRefresh;
    private DrawingHoleTable? HoleTable=>HoleTableList.SelectedItem as DrawingHoleTable;
    private DrawingHoleRow? HoleRow=>HoleRowList.SelectedItem as DrawingHoleRow;
    private void InitializeHoles()
    {
        document.DrawingTopologyPicked+=OnHolePicked;
        Closed+=(_,_)=>{holeLifetime.Cancel();document.DrawingTopologyPicked-=OnHolePicked;};
        RefreshHoleDatumChoices();
    }
    private void RenderHoles()
    {
        var table=HoleTable?.Id;var row=HoleRow?.Id;
        HoleTableList.ItemsSource=Sheet?.HoleTables.ToArray()??[];
        HoleTableList.SelectedItem=HoleTableList.Items.OfType<DrawingHoleTable>().FirstOrDefault(t=>t.Id==table);
        HoleRowList.ItemsSource=HoleTable?.Rows.ToArray()??[];
        HoleRowList.SelectedItem=HoleRowList.Items.OfType<DrawingHoleRow>().FirstOrDefault(r=>r.Id==row);
        RefreshHoleDatumChoices();
    }
    private void HoleTableChanged(object sender,SelectionChangedEventArgs e)
    {
        HoleRowList.ItemsSource=HoleTable?.Rows.ToArray()??[];
        if(HoleTable is not {} table)return;
        ViewList.SelectedItem=Sheet?.Views.SingleOrDefault(v=>v.Id==table.ViewId);
        HoleTableName.Text=table.Name;HoleTableX.Text=Text(table.PositionMm.X);HoleTableY.Text=Text(table.PositionMm.Y);
        HoleOriginX.Text=Text(table.LocalFrame.Translation.X);HoleOriginY.Text=Text(table.LocalFrame.Translation.Y);HoleOriginZ.Text=Text(table.LocalFrame.Translation.Z);
        var angles=table.LocalFrame.Rotation.ToEulerDegrees();HoleAxisX.Text=Text(angles.X);HoleAxisY.Text=Text(angles.Y);HoleAxisZ.Text=Text(angles.Z);
        RefreshHoleDatumChoices(table.CoordinateBinding);
    }
    private void HoleRowChanged(object sender,SelectionChangedEventArgs e)
    {
        if(HoleRow is not {} row)return;holeSource=row.Evidence.Source;HoleNumber.Text=row.Number.ToString(CultureInfo.CurrentCulture);
        var head=row.Evidence.Head switch{HoleHead.Counterbore=>Strings.HoleCounterbore,HoleHead.Countersink=>Strings.HoleCountersink,_=>Strings.HolePlain};
        HoleEvidence.Text=row.StaleReason??$"{row.Evidence.DiameterMm:G6} mm · {row.Evidence.DepthMm:G6} mm · {head}";
    }
    private static string Text(double value)=>value.ToString("G17",CultureInfo.CurrentCulture);
    private RigidTransform3d ReadHoleFrame()=>ReadHoleCoordinateBinding() is not null?HoleTable?.LocalFrame??RigidTransform3d.Identity:new(new(Number(HoleOriginX),Number(HoleOriginY),Number(HoleOriginZ)),
        Quaterniond.FromEulerDegrees(Number(HoleAxisX),Number(HoleAxisY),Number(HoleAxisZ)));
    private DrawingHoleCoordinateBinding? ReadHoleCoordinateBinding()=>
        (HoleCoordinatePlaneChoice.SelectedItem as HoleCoordinateChoice)?.Id is {} plane?
        new(plane,(HoleCoordinateOriginChoice.SelectedItem as HoleCoordinateChoice)?.Id,
            (HoleCoordinateAxisChoice.SelectedItem as HoleCoordinateChoice)?.Id,HoleCoordinateReverseX.IsChecked==true):null;
    private void RefreshHoleDatumChoices(DrawingHoleCoordinateBinding? selected=null)
    {
        if(HoleCoordinatePlaneChoice is null)return;
        if(HoleTable is {} active)selected=active.CoordinateBinding;else selected??=ReadHoleCoordinateBinding();
        var snapshot=document.Session.Snapshot;
        var path=HoleTable?.CoordinatePath??View?.Sources.FirstOrDefault()?.Path;
        var owner=snapshot.EnumerateOccurrences().FirstOrDefault(o=>o.Path.Equals(path))?.DefinitionId;
        void Fill(ComboBox choice,IEnumerable<HoleCoordinateChoice> values,string none,Guid? id)
        {
            var items=new List<HoleCoordinateChoice>{new(null,none)};items.AddRange(values.OrderBy(v=>v.Name,StringComparer.CurrentCulture));
            if(id is {} missing&&!items.Any(v=>v.Id==missing))items.Add(new(missing,Strings.ReferenceStale+" · "+missing));
            choice.ItemsSource=items;choice.SelectedItem=items.Single(v=>v.Id==id);
        }
        holeChoiceRefresh=true;
        try
        {
            Fill(HoleCoordinatePlaneChoice,snapshot.Datums.Planes.Values.Where(p=>p.PartId==owner).Select(p=>new HoleCoordinateChoice(p.Id,p.Name)),Strings.DrawingHoleNumericFrame,selected?.Plane);
            Fill(HoleCoordinateOriginChoice,snapshot.Datums.Points.Values.Where(p=>p.PartId==owner).Select(p=>new HoleCoordinateChoice(p.Id,p.Name)),Strings.DrawingHolePlaneOrigin,selected?.OriginPoint);
            Fill(HoleCoordinateAxisChoice,snapshot.Datums.Axes.Values.Where(p=>p.PartId==owner).Select(p=>new HoleCoordinateChoice(p.Id,p.Name)),Strings.DrawingHolePlaneX,selected?.XAxis);
            HoleCoordinateReverseX.IsChecked=selected?.ReverseX??false;
        }
        finally{holeChoiceRefresh=false;}
        UpdateHoleCoordinateMode();
    }
    private void HoleCoordinatePlaneChanged(object sender,SelectionChangedEventArgs e)
    {
        if(holeChoiceRefresh||HoleCoordinateOriginChoice is null)return;
        if(ReadHoleCoordinateBinding() is null)
        {HoleCoordinateOriginChoice.SelectedIndex=0;HoleCoordinateAxisChoice.SelectedIndex=0;HoleCoordinateReverseX.IsChecked=false;}
        UpdateHoleCoordinateMode();
    }
    private void UpdateHoleCoordinateMode()
    {
        bool named=ReadHoleCoordinateBinding() is not null;
        HoleCoordinateOriginChoice.IsEnabled=HoleCoordinateAxisChoice.IsEnabled=HoleCoordinateReverseX.IsEnabled=named;
        foreach(var box in new[]{HoleOriginX,HoleOriginY,HoleOriginZ,HoleAxisX,HoleAxisY,HoleAxisZ})box.IsEnabled=!named;
    }
    private void PickHole(object sender,RoutedEventArgs e)
    {
        if(View is null)return;annotationGeneration++;holePicking=true;holeSource=null;HoleEvidence.Text="—";
        document.RequestDrawingTopologyPick(TopologyKind.Face);Owner?.Activate();
    }
    private void CancelHolePick(object sender,RoutedEventArgs e)
    {holePicking=false;document.CancelDrawingDatumPick();}
    private void OnHolePicked(object? sender,DrawingTopologyReference reference)
    {
        if(!holePicking)return;holePicking=false;holeSource=reference;
        HoleEvidence.Text=reference.Geometry==DrawingAttachmentGeometry.CylinderFace?Strings.DrawingHoleSelected:Strings.DrawingHolePickShaft;
        Activate();
    }
    private async void CreateHoleTable(object sender,RoutedEventArgs e)
    {
        try
        {
            if(Sheet is not {} sheet||View is not {} view)throw new CadValidationException(Strings.DrawingHoleChooseView);
            var source=view.Sources.First();var occurrence=document.Session.Snapshot.EnumerateOccurrences().Single(o=>o.Path.Equals(source.Path));
            var table=new DrawingHoleTable(Guid.NewGuid(),view.Id,HoleTableName.Text,source.Path,occurrence.DefinitionId,
                ReadHoleFrame(),new(Number(HoleTableX),Number(HoleTableY))){CoordinateBinding=ReadHoleCoordinateBinding()};
            await ExecuteAsync(DrawingHoleTableCommands.Put(sheet.Id,table));
            HoleTableList.SelectedItem=Sheet?.HoleTables.Single(t=>t.Id==table.Id);
        }
        catch(Exception error){if(IsLoaded)Status.Text=error.Message;}
    }
    private async void ApplyHoleTable(object sender,RoutedEventArgs e)
    {
        try{if(Sheet is {} sheet&&HoleTable is {} table)
            await ExecuteAsync(DrawingHoleTableCommands.Put(sheet.Id,table with{Name=HoleTableName.Text,CoordinateBinding=ReadHoleCoordinateBinding(),LocalFrame=ReadHoleFrame(),
                PositionMm=new(Number(HoleTableX),Number(HoleTableY))},true));}
        catch(Exception error){if(IsLoaded)Status.Text=error.Message;}
    }
    private async Task StoreHoleRow(bool replace)
    {
        if(Sheet is not {} sheet||HoleTable is not {} table||holeSource is not {} source)
            throw new CadValidationException(Strings.DrawingHolePickShaft);
        if(replace&&HoleRow is null)throw new CadValidationException(Strings.DrawingHoleChooseRow);
        var selected=HoleRow;int number=int.Parse(HoleNumber.Text,CultureInfo.CurrentCulture);
        var evidence=await document.InspectDrawingHoleAsync(source,holeLifetime.Token);
        if(holeLifetime.IsCancellationRequested||!IsLoaded||Sheet?.Id!=sheet.Id||HoleTable?.Id!=table.Id||holeSource!=source)
            throw new OperationCanceledException();
        var row=new DrawingHoleRow(replace?selected!.Id:Guid.NewGuid(),number,evidence,table.Coordinates(document.Session.Snapshot,evidence));
        var rows=replace?table.Rows.SetItem(Array.FindIndex(table.Rows.ToArray(),r=>r.Id==row.Id),row):table.Rows.Add(row);
        await ExecuteAsync(DrawingHoleTableCommands.Put(sheet.Id,table with{Rows=rows},true));
        HoleRowList.SelectedItem=HoleTable?.Rows.Single(r=>r.Id==row.Id);
        HoleNumber.Text=(number+1).ToString(CultureInfo.CurrentCulture);
    }
    private async void AddHoleRow(object sender,RoutedEventArgs e)
    {try{await StoreHoleRow(false);}catch(OperationCanceledException){}catch(Exception error){if(IsLoaded)Status.Text=error.Message;}}
    private async void ReselectHoleRow(object sender,RoutedEventArgs e)
    {try{await StoreHoleRow(true);}catch(OperationCanceledException){}catch(Exception error){if(IsLoaded)Status.Text=error.Message;}}
    private async void DeleteHoleRow(object sender,RoutedEventArgs e)
    {if(Sheet is {} sheet&&HoleTable is {} table&&HoleRow is {} row)await ExecuteAsync(DrawingHoleTableCommands.DeleteRow(sheet.Id,table.Id,row.Id));}
    private async void DeleteHoleTable(object sender,RoutedEventArgs e)
    {if(Sheet is {} sheet&&HoleTable is {} table)await ExecuteAsync(DrawingHoleTableCommands.Delete(sheet.Id,table.Id));}
}
