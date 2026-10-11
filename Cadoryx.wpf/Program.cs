namespace Cadoryx.wpf;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if(args is ["--native-drawing-worker",var drawingDirectory])
            return Cadoryx.Kernel.Occt.IsolatedDrawingExecutor.RunWorkerAsync(drawingDirectory).GetAwaiter().GetResult();
        if(args is ["--native-thread-worker",var threadDirectory])
            return Cadoryx.Kernel.Occt.IsolatedMetricThreadExecutor.RunWorkerAsync(threadDirectory).GetAwaiter().GetResult();
        if (args is ["--import-worker", var source, var directory])
            return Services.IO.IsolatedExchangeImporter.RunWorkerAsync(source, directory).GetAwaiter().GetResult();
        var app = new App();app.InitializeComponent();return app.Run();
    }
}
