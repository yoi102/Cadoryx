using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.IO;
using Microsoft.Win32;
using Strings=Cadoryx.Lang.Strings.Strings;

namespace Cadoryx.wpf.Views;

public partial class TechnicalDrawingWindow
{
    private sealed record ContentChoice(Guid Id,string Name,bool Note);
    private sealed record ContentKindChoice(bool Note,string Name);
    private bool renderingTemplate;
    private void RenderTemplateLibrary()
    {
        renderingTemplate=true;
        try
        {
            var selected=(TemplateLibraryChoice.SelectedItem as DrawingTemplateDefinition)?.Id;
            var library=document.Session.Snapshot.DrawingTemplates.Values.OrderBy(t=>t.Name,StringComparer.CurrentCulture).ToArray();
            TemplateLibraryChoice.ItemsSource=library;
            TemplateLibraryChoice.SelectedItem=library.FirstOrDefault(t=>t.Id==selected);
            if(SheetContentKind.Items.Count==0)
            {SheetContentKind.ItemsSource=new[]{new ContentKindChoice(false,Strings.DrawingCustomField),new ContentKindChoice(true,Strings.DrawingManufacturingNote)};SheetContentKind.SelectedIndex=0;}
            var contentId=(SheetContentList.SelectedItem as ContentChoice)?.Id;
            var template=Sheet?.Template;
            var contents=(template?.CustomFields??[]).Select(f=>new ContentChoice(f.Id,f.Caption,false))
                .Concat((template?.Notes??[]).Select(n=>new ContentChoice(n.Id,n.Text.Split('\n')[0],true))).ToArray();
            SheetContentList.ItemsSource=contents;SheetContentList.SelectedItem=contents.FirstOrDefault(c=>c.Id==contentId);
            FrameZonesEnabled.IsChecked=template?.Zones is not null;
            FrameZoneColumns.Text=(template?.Zones?.Columns??8).ToString(CultureInfo.CurrentCulture);
            FrameZoneRows.Text=(template?.Zones?.Rows??6).ToString(CultureInfo.CurrentCulture);
            FrameZonesReverseColumns.IsChecked=template?.Zones?.ReverseColumns==true;
            FrameZonesReverseRows.IsChecked=template?.Zones?.ReverseRows==true;
            SaveTemplateButton.IsEnabled=template is not null;
            AddSheetContentButton.IsEnabled=ApplyFrameZonesButton.IsEnabled=template is not null;
        }
        finally{renderingTemplate=false;}
        UpdateTemplateActions();
    }
    private void UpdateTemplateActions()
    {
        bool selected=TemplateLibraryChoice.SelectedItem is DrawingTemplateDefinition;
        ApplyTemplateButton.IsEnabled=selected&&Sheet is not null;
        ReplaceTemplateButton.IsEnabled=selected&&Sheet?.Template is not null;
        DeleteTemplateButton.IsEnabled=ExportTemplateButton.IsEnabled=selected;
        ApplySheetContentButton.IsEnabled=DeleteSheetContentButton.IsEnabled=SheetContentList.SelectedItem is ContentChoice;
        bool note=(SheetContentKind.SelectedItem as ContentKindChoice)?.Note==true;
        SheetContentCaption.IsEnabled=SheetContentHeight.IsEnabled=!note;
    }
    private void TemplateLibrarySelected(object sender,SelectionChangedEventArgs e)
    {
        if(document is null||renderingTemplate)return;
        if(TemplateLibraryChoice.SelectedItem is DrawingTemplateDefinition entry)TemplateLibraryName.Text=entry.Name;
        UpdateTemplateActions();
    }
    private async void SaveTemplate(object sender,RoutedEventArgs e)
    {
        if(Sheet is not {} sheet)return;var id=Guid.NewGuid();
        await ExecuteAsync(DrawingTemplateCommands.Save(sheet.Id,id,TemplateLibraryName.Text));
        TemplateLibraryChoice.SelectedItem=TemplateLibraryChoice.Items.OfType<DrawingTemplateDefinition>().FirstOrDefault(t=>t.Id==id);
    }
    private async void ReplaceTemplate(object sender,RoutedEventArgs e)
    {if(Sheet is {} sheet&&TemplateLibraryChoice.SelectedItem is DrawingTemplateDefinition entry)await ExecuteAsync(DrawingTemplateCommands.Save(sheet.Id,entry.Id,TemplateLibraryName.Text,true));}
    private async void ApplyLibraryTemplate(object sender,RoutedEventArgs e)
    {if(Sheet is {} sheet&&TemplateLibraryChoice.SelectedItem is DrawingTemplateDefinition entry)await ExecuteAsync(DrawingTemplateCommands.Apply(sheet.Id,entry.Id));}
    private async void DeleteLibraryTemplate(object sender,RoutedEventArgs e)
    {if(TemplateLibraryChoice.SelectedItem is DrawingTemplateDefinition entry)await ExecuteAsync(DrawingTemplateCommands.Delete(entry.Id));}
    private async void ImportTemplate(object sender,RoutedEventArgs e)
    {
        var dialog=new OpenFileDialog{Filter="Cadoryx template|*.cadtemplate",CheckFileExists=true};
        if(dialog.ShowDialog(this)!=true)return;
        try{await ExecuteAsync(DrawingTemplateCommands.Import(DrawingTemplateFile.Read(dialog.FileName)));}
        catch(Exception error){Status.Text=error.Message;}
    }
    private void ExportTemplate(object sender,RoutedEventArgs e)
    {
        if(TemplateLibraryChoice.SelectedItem is not DrawingTemplateDefinition entry)return;
        var dialog=new SaveFileDialog{Filter="Cadoryx template|*.cadtemplate",DefaultExt=".cadtemplate",FileName="Drawing.cadtemplate"};
        if(dialog.ShowDialog(this)!=true)return;
        try{DrawingTemplateFile.Write(entry,dialog.FileName);Status.Text=string.Empty;}
        catch(Exception error){Status.Text=error.Message;}
    }
    private void SheetContentKindChanged(object sender,SelectionChangedEventArgs e)
    {if(document is not null&&!renderingTemplate)UpdateTemplateActions();}
    private void SheetContentSelected(object sender,SelectionChangedEventArgs e)
    {
        if(document is null||renderingTemplate||Sheet?.Template is not {} template)return;
        if(SheetContentList.SelectedItem is ContentChoice content)
        {
            SheetContentKind.SelectedIndex=content.Note?1:0;
            Point2d position;double width,height;string caption,text;
            if(content.Note)
            {var n=template.Notes.Single(n=>n.Id==content.Id);position=n.PositionMm;width=n.WidthMm;height=12;caption="";text=n.Text;}
            else
            {var f=template.CustomFields.Single(f=>f.Id==content.Id);position=f.PositionMm;width=f.WidthMm;height=f.HeightMm;caption=f.Caption;text=f.Value;}
            SheetContentCaption.Text=caption;SheetContentText.Text=text;
            SheetContentX.Text=position.X.ToString(CultureInfo.CurrentCulture);SheetContentY.Text=position.Y.ToString(CultureInfo.CurrentCulture);
            SheetContentWidth.Text=width.ToString(CultureInfo.CurrentCulture);SheetContentHeight.Text=height.ToString(CultureInfo.CurrentCulture);
        }
        UpdateTemplateActions();
    }
    private async Task PutSheetContent(bool replace)
    {
        if(Sheet is not {} sheet)return;
        try
        {
            var selected=SheetContentList.SelectedItem as ContentChoice;
            var id=replace?selected?.Id??throw new CadValidationException("Select sheet content to replace."):Guid.NewGuid();
            bool note=(SheetContentKind.SelectedItem as ContentKindChoice)?.Note==true;
            if(replace&&selected!.Note!=note)throw new CadValidationException("Delete old content before changing its type.");
            var point=new Point2d(Number(SheetContentX),Number(SheetContentY));double width=Number(SheetContentWidth);
            await ExecuteAsync(note?DrawingTemplateCommands.PutNote(sheet.Id,new(id,SheetContentText.Text.Replace("\r\n","\n"),point,width),replace):
                DrawingTemplateCommands.PutField(sheet.Id,new(id,SheetContentCaption.Text,SheetContentText.Text,point,width,Number(SheetContentHeight)),replace));
            SheetContentList.SelectedItem=SheetContentList.Items.OfType<ContentChoice>().FirstOrDefault(c=>c.Id==id);
        }
        catch(Exception error){Status.Text=error.Message;}
    }
    private async void AddSheetContent(object sender,RoutedEventArgs e)=>await PutSheetContent(false);
    private async void ApplySheetContent(object sender,RoutedEventArgs e)=>await PutSheetContent(true);
    private async void DeleteSheetContent(object sender,RoutedEventArgs e)
    {if(Sheet is {} sheet&&SheetContentList.SelectedItem is ContentChoice content)await ExecuteAsync(DrawingTemplateCommands.DeleteContent(sheet.Id,content.Id));}
    private async void ApplyFrameZones(object sender,RoutedEventArgs e)
    {
        if(Sheet is not {} sheet)return;
        try
        {
            int Count(TextBox field){double number=Number(field);if(number!=Math.Truncate(number)||number is <1 or >32)throw new CadValidationException("Zone counts must be whole numbers.");return (int)number;}
            await ExecuteAsync(DrawingTemplateCommands.SetZones(sheet.Id,FrameZonesEnabled.IsChecked==true?
                new(Count(FrameZoneColumns),Count(FrameZoneRows),FrameZonesReverseColumns.IsChecked==true,FrameZonesReverseRows.IsChecked==true):null));
        }
        catch(Exception error){Status.Text=error.Message;}
    }
}
