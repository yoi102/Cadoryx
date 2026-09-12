using Antelcat.I18N.WPF;
using AvalonDock;
using AvalonDock.DependencyInjection;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Services.Platform.Settings;
using Cadoryx.ViewModels.Toolboxes;
using Cadoryx.wpf.Services.Application;
using Cadoryx.wpf.Services.Dialogs;
using Cadoryx.wpf.Services.Toolboxes;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;

namespace Cadoryx.wpf;
/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private IServiceProvider _serviceProvider;

    public App()
    {
        string lang = System.Globalization.CultureInfo.CurrentCulture.Name;
        var culture = new System.Globalization.CultureInfo(lang);
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        I18NExtension.Culture = culture;


        var services = new ServiceCollection();
        ConfigureServices(services);

        _serviceProvider = services.BuildServiceProvider();
        Ioc.Default.ConfigureServices(_serviceProvider);
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        this.MainWindow = mainWindow;

        mainWindow.Show();
        if(e.Args.Length>=2&&e.Args[0]=="--smoke")
            _=Diagnostics.SmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
    }



    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<MainWindow>();
        services.AddMessagePipe();
        services.AddViewModels();


        services.AddSingleton<IApplicationCultureService, ApplicationCultureService>()
          .AddSingleton<IApplicationThemeService, ApplicationThemeService>();
        services.AddSingleton<IApplicationSettingsStore, JsonApplicationSettingsStore>();
        services.AddSingleton<ICadMessageLog, CadMessageLog>();
        services.AddSingleton<Cadoryx.Kernel.Abstractions.IAssetStore,Cadoryx.Kernel.Abstractions.MemoryAssetStore>();
        services.AddSingleton<Cadoryx.Kernel.Abstractions.IGeometryKernel,Cadoryx.Kernel.Occt.OcctGeometryKernel>();
        services.AddSingleton<Cadoryx.Kernel.Abstractions.IDocumentStorage,Cadoryx.IO.CadDocumentStorage>();
        services.AddSingleton<Cadoryx.Editor.ISessionDispatcher,WpfSessionDispatcher>();
        services.AddSingleton<Cadoryx.Editor.CadWorkspace>();
        services.AddSingleton<ICadFileDialogs,CadFileDialogs>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(provider => provider.GetRequiredService<DialogService>());
        services.AddSingleton<IApplicationSettingsDialogService>(provider => provider.GetRequiredService<DialogService>());
        services.AddSingleton<ToolboxLayoutPersistenceService>();
        services.AddSingleton<Cadoryx.ViewModels.Services.Platform.IToolboxIconProvider, ToolboxIconProvider>();


        services.AddDockLayoutService(configure: dock =>
        {
            dock.ConfigureToggleDock(opts =>
            {
                opts.ButtonSize = 28;
                opts.DefaultDockWidth = 280;
                opts.DefaultDockHeight = 220;
                opts.LayoutPriority = nameof(DockLayoutPriority.BottomFullWidth);
            });

            // Register toolboxes — order determines the sidebar button order.
            dock.AddToolbox<ModelTreeToolboxViewModel>();
            dock.AddToolbox<PropertiesToolboxViewModel>();
            dock.AddToolbox<ModelingToolboxViewModel>();
            dock.AddToolbox<MessagesToolboxViewModel>();

        });







    }
}

