using System.Windows;
using MaterialDesignThemes.Wpf;
namespace Cadoryx.wpf.Views.Dialogs;

public static class ConfirmationDialogResult
{
    public const string Accept = "Accept";
    public const string Discard = "Discard";
}

public partial class ConfirmationDialog
{
    public ConfirmationDialog(string title,string message,string acceptLabel,string? discardLabel=null)
    {
        InitializeComponent();MessageText.Text=message;AcceptButton.Content=acceptLabel;
        DiscardButton.Content=discardLabel;DiscardButton.Visibility=discardLabel is null?Visibility.Collapsed:Visibility.Visible;
    }
    private void AcceptClicked(object sender,RoutedEventArgs e)
        => DialogHost.CloseDialogCommand.Execute(ConfirmationDialogResult.Accept, this);
    private void DiscardClicked(object sender,RoutedEventArgs e)
        => DialogHost.CloseDialogCommand.Execute(ConfirmationDialogResult.Discard, this);
}
