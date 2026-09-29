using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Cadoryx.wpf.Views.Dialogs;
/// <summary>
/// ProgressDialog.xaml 的交互逻辑
/// </summary>
public partial class ProgressDialog : UserControl
{
    public static readonly DependencyProperty ShowCancelButtonProperty = DependencyProperty.Register(
        nameof(ShowCancelButton), typeof(bool), typeof(ProgressDialog), new PropertyMetadata(false));
    public static readonly DependencyProperty CancelCommandProperty = DependencyProperty.Register(
        nameof(CancelCommand), typeof(ICommand), typeof(ProgressDialog));
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(ProgressDialog), new PropertyMetadata(""));
    public bool ShowCancelButton { get => (bool)GetValue(ShowCancelButtonProperty); set => SetValue(ShowCancelButtonProperty, value); }
    public ICommand? CancelCommand { get => (ICommand?)GetValue(CancelCommandProperty); set => SetValue(CancelCommandProperty, value); }
    public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public ProgressDialog()
    {
        InitializeComponent();
    }
}
