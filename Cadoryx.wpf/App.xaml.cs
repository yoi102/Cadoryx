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
using System.IO;

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
        var recoveryHost=_serviceProvider.GetRequiredService<RecoveryHost>();
        recoveryHost.Start((MainWindowViewModel)mainWindow.DataContext);
        if(e.Args.Length>=2&&e.Args[0]=="--smoke")
            _=Diagnostics.SmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--sketch-editor-smoke")
            _=Diagnostics.SketchEditorSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=3&&e.Args[0]=="--window-smoke")
            _=Diagnostics.WindowSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1],e.Args[2]);
        else if(e.Args.Length>=2&&e.Args[0] is "--recovery-seed" or "--recovery-verify")
            _=Diagnostics.RecoverySmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[0],e.Args[1]);
        else _=((MainWindowViewModel)mainWindow.DataContext).CheckForRecoveryAsync();
    }
    internal Task StopRecoveryAsync()=>_serviceProvider.GetRequiredService<RecoveryHost>().StopAsync();
    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider.GetRequiredService<RecoveryHost>().Dispose();
        _serviceProvider.GetRequiredService<Cadoryx.Kernel.Abstractions.IRecoveryStore>().Dispose();
        base.OnExit(e);
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
        services.AddSingleton<Cadoryx.Sketching.ISketchConstraintSolver,Cadoryx.Sketching.ManagedSketchConstraintSolver>();
        services.AddSingleton<ISketchEditorHost,Cadoryx.wpf.Views.SketchEditorHost>();
        services.AddSingleton<ILocalFeatureHost,Cadoryx.wpf.Views.LocalFeatureHost>();
        services.AddSingleton<Cadoryx.Editor.ISessionDispatcher,WpfSessionDispatcher>();
        services.AddSingleton<Cadoryx.Kernel.Abstractions.IRecoveryStore>(provider=>
        {
            var args=Environment.GetCommandLineArgs();
            bool smoke=args.Length>=3&&args[1] is "--smoke" or "--recovery-seed" or "--recovery-verify" or "--window-smoke" or "--sketch-editor-smoke";
            string root=smoke?Path.Combine(Path.GetFullPath(args[2]),"recovery"):
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Cadoryx","Recovery");
            return new Cadoryx.IO.CadRecoveryStore(root,provider.GetRequiredService<Cadoryx.Kernel.Abstractions.IDocumentStorage>());
        });
        services.AddSingleton<Cadoryx.Editor.DocumentRecoveryService>();
        services.AddSingleton<RecoveryHost>();
        services.AddSingleton<IRecoveryDialogService,RecoveryDialogService>();
        services.AddSingleton<IDocumentResourcesDialogService,DocumentResourcesDialogService>();
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

