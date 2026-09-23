using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cadoryx.ViewModels;

namespace Cadoryx.wpf.Views;

public partial class MainStatusBarView
{
    public MainStatusBarView()
    {
        InitializeComponent();
    }

    private async void GridVisibleClicked(object sender,RoutedEventArgs e)
    {
        if(DataContext is MainWindowViewModel main && main.ActiveDocument is {} doc)
            await ApplyAsync(main,doc.SetGridAsync(visible:((CheckBox)sender).IsChecked==true));
    }

    private async void GridSnapClicked(object sender,RoutedEventArgs e)
    {
        if(DataContext is MainWindowViewModel main && main.ActiveDocument is {} doc)
            await ApplyAsync(main,doc.SetGridAsync(snap:((CheckBox)sender).IsChecked==true));
    }

    private async void GridSpacingLostFocus(object sender,KeyboardFocusChangedEventArgs e)=>await CommitSpacingAsync();

    private async void GridSpacingKeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key!=Key.Enter)return;
        await CommitSpacingAsync();
        e.Handled=true;
    }

    private async Task CommitSpacingAsync()
    {
        if(DataContext is not MainWindowViewModel main || main.ActiveDocument is not {} doc ||
           GridSpacingInput.Value is not double spacing || spacing==doc.GridSpacingMm)return;
        await ApplyAsync(main,doc.SetGridAsync(spacingMm:spacing));
    }

    private static async Task ApplyAsync(MainWindowViewModel main,Task task)
    {
        try { await task; }
        catch(Exception ex) { main.Report(ex); }
    }
}
