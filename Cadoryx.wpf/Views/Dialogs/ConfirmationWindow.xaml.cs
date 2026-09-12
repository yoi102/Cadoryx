using System.Windows;
using MahApps.Metro.Controls;
namespace Cadoryx.wpf.Views.Dialogs;

public enum CadConfirmationResult { Cancel, Accept, Discard }
public partial class ConfirmationWindow : MetroWindow
{
    public CadConfirmationResult Result {get;private set;}
    public ConfirmationWindow(string title,string message,string acceptLabel,string? discardLabel=null)
    {
        InitializeComponent();Title=title;MessageText.Text=message;AcceptButton.Content=acceptLabel;
        DiscardButton.Content=discardLabel;DiscardButton.Visibility=discardLabel is null?Visibility.Collapsed:Visibility.Visible;
    }
    public static CadConfirmationResult Ask(Window owner,string title,string message,string acceptLabel,string? discardLabel=null)
    {
        var dialog=new ConfirmationWindow(title,message,acceptLabel,discardLabel){Owner=owner};dialog.ShowDialog();return dialog.Result;
    }
    private void AcceptClicked(object sender,RoutedEventArgs e){Result=CadConfirmationResult.Accept;DialogResult=true;}
    private void DiscardClicked(object sender,RoutedEventArgs e){Result=CadConfirmationResult.Discard;DialogResult=true;}
}
