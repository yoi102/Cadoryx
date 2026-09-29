namespace Cadoryx.wpf;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--import-worker", var source, var directory])
            return Services.IO.IsolatedExchangeImporter.RunWorkerAsync(source, directory).GetAwaiter().GetResult();
        var app = new App();app.InitializeComponent();return app.Run();
    }
}
