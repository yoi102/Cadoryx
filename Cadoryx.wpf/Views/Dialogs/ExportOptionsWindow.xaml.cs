using System.IO;
using System.Windows;
using Cadoryx.ViewModels.Services.Platform;
namespace Cadoryx.wpf.Views.Dialogs;
public partial class ExportOptionsWindow:Window
{
    private readonly string path;
    public CadExportRequest? Request {get;private set;}
    public ExportOptionsWindow(string path)
    {
        InitializeComponent();this.path=path;bool stl=Path.GetExtension(path).Equals(".stl",StringComparison.OrdinalIgnoreCase);
        FormatText.Text=stl?"STL 网格":Path.GetExtension(path).ToUpperInvariant().TrimStart('.')+" 模型";
        SemanticsText.Text=stl?"按毫米坐标导出三角网格。STL 不包含单位标记、颜色、装配关系或特征历史。偏差越小，文件通常越大。":"导出几何、放置、名称和整体颜色。Cadoryx 参数历史及部分源文件标注、子形状样式不会写入交换文件。";
        StlSettings.Visibility=stl?Visibility.Visible:Visibility.Collapsed;
    }
    private void ExportClicked(object sender,RoutedEventArgs e)
    {
        if(LinearInput.Value is not {} linear||AngleInput.Value is not {} angle)return;
        Request=new(path,linear,angle*Math.PI/180,BinaryCheck.IsChecked==true,VisibleCheck.IsChecked==true);DialogResult=true;
    }
}
