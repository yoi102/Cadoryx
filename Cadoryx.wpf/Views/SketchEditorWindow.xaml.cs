using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cadoryx.ViewModels;
using MahApps.Metro.Controls;

namespace Cadoryx.wpf.Views;

public partial class SketchEditorWindow:MetroWindow
{
    private bool closeReady;
    private bool closing;
    private bool syncing;
    public SketchEditorViewModel Editor {get;}
    public SketchEditorWindow(SketchEditorViewModel editor)
    {
        Editor=editor;InitializeComponent();DataContext=editor;
        editor.CloseRequested+=OnCloseRequested;editor.DrawingChanged+=OnDrawingChanged;Closing+=OnClosing;PreviewKeyDown+=OnKey;
        Loaded+=(_,_)=>Canvas.Fit();
    }
    private void OnFit(object sender,RoutedEventArgs e)=>Canvas.Fit();
    private void OnDrawingChanged(object? sender,EventArgs e)
    {
        syncing=true;
        try{EntityList.SelectedItems.Clear();foreach(var item in Editor.Entities.Where(e=>Editor.SelectedEntities.Contains(e.Id)))EntityList.SelectedItems.Add(item);}
        finally{syncing=false;}
    }
    private void OnEntitySelection(object sender,SelectionChangedEventArgs e)
    {
        if(syncing||!IsLoaded)return;
        // Collection rebuild removals must not erase the independent model selection.
        if(e.AddedItems.Count==0)return;
        var ids=EntityList.SelectedItems.Cast<SketchEntityItem>().Select(x=>x.Id).ToArray();Editor.Select(null);foreach(var id in ids)Editor.SelectedEntities.Add(id);
    }
    private async void OnKey(object sender,KeyEventArgs e)
    {
        if(e.Key==Key.Escape){if(!Canvas.CancelGesture())Editor.CancelCommand.Execute(null);e.Handled=true;}
        else if(e.Key==Key.Enter&&Editor.CanEdit){await Editor.PreviewCommand.ExecuteAsync(null);e.Handled=true;}
        else if((Keyboard.Modifiers&ModifierKeys.Control)!=0&&Keyboard.FocusedElement is not TextBox)
        {if(e.Key==Key.Z){Editor.UndoCommand.Execute(null);e.Handled=true;}if(e.Key==Key.Y){Editor.RedoCommand.Execute(null);e.Handled=true;}}
        else if(e.Key==Key.Delete&&Keyboard.FocusedElement==Canvas){Editor.DeleteEntitiesCommand.Execute(null);e.Handled=true;}
    }
    private void OnCloseRequested(object? sender,EventArgs e)=>Close();
    private async void OnClosing(object? sender,CancelEventArgs e)
    {
        if(closeReady)return;e.Cancel=true;if(closing)return;closing=true;Canvas.CancelGesture();
        try{await Editor.DisposeAsync();}
        finally{Editor.CloseRequested-=OnCloseRequested;Editor.DrawingChanged-=OnDrawingChanged;closeReady=true;Close();}
    }
}

public sealed class SketchEditorHost:ISketchEditorHost
{
    public Task ShowAsync(SketchEditorViewModel editor)
    {
        var window=new SketchEditorWindow(editor){Owner=System.Windows.Application.Current.MainWindow};window.ShowDialog();return Task.CompletedTask;
    }
}
