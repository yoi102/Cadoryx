using Antelcat.I18N.WPF;
using AvalonDock;
using AvalonDock.DependencyInjection;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Services.Platform.Settings;
using Cadoryx.ViewModels.Toolboxes;
using Cadoryx.CommandLine;
using Cadoryx.Agent;
using Cadoryx.Agent.Codex;
using Cadoryx.AI.Contracts;
using Cadoryx.AI.LmStudio;
using Cadoryx.wpf.Services.Application;
using Cadoryx.wpf.Services.Dialogs;
using Cadoryx.wpf.Services.Toolboxes;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using System.IO;
using System.Net.Http;

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
        if(e.Args.Length>=3&&e.Args[0]=="--m6-benchmark")
            _=Diagnostics.M6BenchmarkRunner.RunAsync(mainWindow,e.Args[1],e.Args[2],e.Args.Length>=4&&int.TryParse(e.Args[3],out var cycles)?cycles:20);
        else if(e.Args.Length>=3&&e.Args[0]=="--m14-open-benchmark")
            _=Diagnostics.M14OpenBenchmarkRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1],e.Args[2]);
        else if(e.Args.Length>=2&&e.Args[0]=="--smoke")
            _=Diagnostics.SmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--sketch-editor-smoke")
            _=Diagnostics.SketchEditorSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=3&&e.Args[0]=="--window-smoke")
            _=Diagnostics.WindowSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1],e.Args[2]);
        else if(e.Args.Length>=2&&e.Args[0]=="--m10-window-smoke")
            _=Diagnostics.M10AssemblyWindowSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--m11-window-smoke")
            _=Diagnostics.M11DrawingWindowSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0] is "--recovery-seed" or "--recovery-verify")
            _=Diagnostics.RecoverySmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[0],e.Args[1]);
        else _=((MainWindowViewModel)mainWindow.DataContext).CheckForRecoveryAsync();
    }
    internal async Task StopRecoveryAsync()
    {
        await _serviceProvider.GetRequiredService<RecoveryHost>().StopAsync();
        await _serviceProvider.GetRequiredService<Services.Toasts.CadNotificationService>().StopAsync();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        (_serviceProvider.GetService<ICodexAgentClient>() as IDisposable)?.Dispose();
        (_serviceProvider.GetService<HttpClient>())?.Dispose();
        _serviceProvider.GetRequiredService<Services.Toasts.CadNotificationService>().Dispose();
        _serviceProvider.GetRequiredService<RecoveryHost>().Dispose();
        _serviceProvider.GetRequiredService<Cadoryx.Kernel.Abstractions.IRecoveryStore>().Dispose();
        (_serviceProvider.GetRequiredService<Cadoryx.Kernel.Abstractions.IAssetStore>() as IDisposable)?.Dispose();
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
        services.AddSingleton<Services.Toasts.CadNotificationService>();
        services.AddSingleton<ICadNotificationService>(p=>p.GetRequiredService<Services.Toasts.CadNotificationService>());
        services.AddSingleton<Cadoryx.Kernel.Abstractions.IAssetStore>(_=>
            Environment.GetEnvironmentVariable("CADORYX_ASSET_STORE")=="memory"
                ?new Cadoryx.Kernel.Abstractions.MemoryAssetStore()
                :new Cadoryx.Kernel.Abstractions.DiskAssetStore(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Cadoryx","AssetCache")));
        services.AddSingleton<Cadoryx.Kernel.Abstractions.IGeometryKernel,Cadoryx.Kernel.Occt.OcctGeometryKernel>();
        services.AddSingleton<Cadoryx.Kernel.Abstractions.IDocumentStorage>(_=>new Cadoryx.IO.CadDocumentStorage(deferGeometryAssets:true));
        services.AddSingleton<Cadoryx.Kernel.Abstractions.IExchangeImportService,Cadoryx.wpf.Services.IO.IsolatedExchangeImporter>();
        services.AddSingleton<Cadoryx.Sketching.ISketchConstraintSolver,Cadoryx.Sketching.ManagedSketchConstraintSolver>();
        services.AddSingleton<ISketchEditorHost,Cadoryx.wpf.Views.SketchEditorHost>();
        services.AddSingleton<ILocalFeatureHost,Cadoryx.wpf.Views.LocalFeatureHost>();
        services.AddSingleton<IHistoryQueryHost,Cadoryx.wpf.Views.HistoryQueryHost>();
        services.AddSingleton<Cadoryx.ViewModels.Settings.IDocumentSettingsHost,Cadoryx.wpf.Views.Settings.DocumentSettingsHost>();
        services.AddSingleton<Cadoryx.Editor.ISessionDispatcher,WpfSessionDispatcher>();
        services.AddSingleton<Cadoryx.Kernel.Abstractions.IRecoveryStore>(provider=>
        {
            var args=Environment.GetCommandLineArgs();
            bool smoke=args.Length>=3&&args[1] is "--smoke" or "--recovery-seed" or "--recovery-verify" or "--window-smoke" or "--sketch-editor-smoke" or "--m11-window-smoke";
            string root=smoke?Path.Combine(Path.GetFullPath(args[2]),"recovery"):
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Cadoryx","Recovery");
            if(args.Length>=4&&args[1]=="--m6-benchmark")root=Path.Combine(Path.GetFullPath(args[3]),"recovery");
            return new Cadoryx.IO.CadRecoveryStore(root,provider.GetRequiredService<Cadoryx.Kernel.Abstractions.IDocumentStorage>());
        });
        services.AddSingleton<Cadoryx.Editor.DocumentRecoveryService>();
        services.AddSingleton<RecoveryHost>();
        services.AddSingleton<IRecoveryDialogService,RecoveryDialogService>();
        services.AddSingleton<IDocumentResourcesDialogService,DocumentResourcesDialogService>();
        services.AddSingleton<Cadoryx.Editor.CadWorkspace>();
        services.AddSingleton<ICadFileDialogs,CadFileDialogs>();
        services.AddSingleton<Cadoryx.Kernel.Abstractions.IDocumentDeliveryService,Cadoryx.IO.DocumentDeliveryService>();
        services.AddSingleton<Cadoryx.ViewModels.IDeliveryHost,Cadoryx.wpf.Views.DeliveryHost>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(provider => provider.GetRequiredService<DialogService>());
        services.AddSingleton<IApplicationSettingsDialogService>(provider => provider.GetRequiredService<DialogService>());
        services.AddSingleton<ToolboxLayoutPersistenceService>();
        services.AddSingleton<ICadCommandLineService, CadCommandLineService>();
        services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromMinutes(10) });
        services.AddSingleton<IAiChatClient, LmStudioChatClient>();
        services.AddSingleton<IAgentRunner, AgentRunner>();
        services.AddSingleton<ICodexAgentClient, CodexAppServerClient>();
        services.AddSingleton<IAiAssistantSettingsStore, JsonAiAssistantSettingsStore>();
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
            dock.AddToolbox<CommandLineToolboxViewModel>();
            dock.AddToolbox<AiAssistantToolboxViewModel>();

        });







    }
}

