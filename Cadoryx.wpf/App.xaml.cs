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
        else if(e.Args.Length>=2&&e.Args[0]=="--b3-sketch-smoke")
            _=Diagnostics.SketchEditorSmokeRunner.RunB3Async(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--b4-sketch-smoke")
            _=Diagnostics.SketchEditorSmokeRunner.RunB4Async(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--b5-sketch-smoke")
            _=Diagnostics.SketchEditorSmokeRunner.RunB5Async(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--b6-sketch-smoke")
            _=Diagnostics.SketchEditorSmokeRunner.RunB6Async(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--b7-sketch-smoke")
            _=Diagnostics.SketchEditorSmokeRunner.RunB7Async(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--b8-revolve-smoke")
            _=Diagnostics.CurveRevolveSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--b9-curve-editor-smoke")
            _=Diagnostics.ExactCurveEditorSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--b10-region-offset-smoke")
            _=Diagnostics.RegionOffsetSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0] is "--b17-miter-smoke" or "--b17-round-smoke" or "--b17-bevel-smoke")
            _=Diagnostics.RegionOffsetSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1],
                e.Args[0]=="--b17-round-smoke"?Cadoryx.Editor.SketchOffsetJoin.Round:
                e.Args[0]=="--b17-bevel-smoke"?Cadoryx.Editor.SketchOffsetJoin.Bevel:Cadoryx.Editor.SketchOffsetJoin.Miter,true);
        else if(e.Args.Length>=2&&e.Args[0]=="--b11-native-projection-smoke")
            _=Diagnostics.SketchNativeProjectionSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--b11-endpoint-smoke")
            _=Diagnostics.SketchExactEndpointSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--b11-parameter-smoke")
            _=Diagnostics.SketchCurveParameterSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--b11-periodic-smoke")
            _=Diagnostics.SketchPeriodicIntervalSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--b12-contact-smoke")
            _=Diagnostics.SketchCircularContactSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--b14-finite-contact-smoke")
            _=Diagnostics.SketchFiniteContactSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--b15-mixed-continuity-smoke")
            _=Diagnostics.SketchMixedFiniteContactSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0] is "--b16-sliding-a-smoke" or "--b16-sliding-b-smoke" or "--b16-sliding-both-smoke")
            _=Diagnostics.SketchSlidingContactSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1],
                e.Args[0]=="--b16-sliding-a-smoke"?Cadoryx.Db.FiniteContactParameterMode.SolveA:
                e.Args[0]=="--b16-sliding-b-smoke"?Cadoryx.Db.FiniteContactParameterMode.SolveB:Cadoryx.Db.FiniteContactParameterMode.SolveBoth);
        else if(e.Args.Length>=2&&e.Args[0]=="--b13-curvature-smoke")
            _=Diagnostics.SketchEndpointCurvatureSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--b18-expression-author-smoke")
            _=Diagnostics.SketchExpressionAuthorSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--b18-expression-consumers-smoke")
            _=Diagnostics.SketchExpressionConsumerSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=3&&e.Args[0]=="--b18-curved-consumers-smoke")
            _=Diagnostics.SketchExpressionCurvedConsumerSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1],e.Args[2]);
        else if(e.Args.Length>=3&&e.Args[0]=="--b18-curved-results-smoke")
            _=Diagnostics.SketchExpressionCurvedConsumerSmokeRunner.RunResultsAsync(mainWindow,_serviceProvider,e.Args[1],e.Args[2]);
        else if(e.Args.Length>=2&&e.Args[0]=="--b18-expression-edit-smoke")
            _=Diagnostics.SketchExpressionEditSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--b18-region-author-smoke")
            _=Diagnostics.SketchRegionAuthorSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--b18-region-reselection-smoke")
            _=Diagnostics.SketchRegionReselectionSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--e1-history-smoke")
            _=Diagnostics.FeatureTimelineSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--c1-parameter-smoke")
            _=Diagnostics.ParameterEditorSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--c2-datum-smoke")
            _=Diagnostics.DatumEditorSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--c3-repetition-smoke")
            _=Diagnostics.RepetitionEditorSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--c3-assembly-smoke")
            _=Diagnostics.AssemblyRepetitionEditorSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--c4-hole-smoke")
            _=Diagnostics.HoleEditorSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--c5-thread-smoke")
            _=Diagnostics.MetricThreadHoleSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--d1-extrusion-smoke")
            _=Diagnostics.ExtrusionEditorSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--d5-surface-extrusion-smoke")
            _=Diagnostics.FiniteSurfaceExtrusionSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--d3-face-feature-smoke")
            _=Diagnostics.FaceFeatureEditorSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--d4-solid-offset-smoke")
            _=Diagnostics.SolidOffsetSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--e3-contour-smoke")
            _=Diagnostics.ContourLocalSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--e4-face-edit-smoke")
            _=Diagnostics.PlanarFaceEditingSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--d2-guided-smoke")
            _=Diagnostics.GuidedEditorSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=3&&e.Args[0]=="--window-smoke")
            _=Diagnostics.WindowSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1],e.Args[2]);
        else if(e.Args.Length>=2&&e.Args[0]=="--radial-smoke")
            _=Diagnostics.WindowSmokeRunner.RunRadialAsync(mainWindow,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--m10-window-smoke")
            _=Diagnostics.M10AssemblyWindowSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--e2-assembly-solve-smoke")
            _=Diagnostics.AssemblySolveSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=3&&e.Args[0]=="--a4-drawing-cancel-smoke")
            _=Diagnostics.DrawingCancellationSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1],e.Args[2]);
        else if(e.Args.Length>=2&&e.Args[0]=="--m11-window-smoke")
            _=Diagnostics.M11DrawingWindowSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--f3c-hole-table-smoke")
            _=Diagnostics.DrawingHoleTableSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--f3e-typed-datum-smoke")
            _=Diagnostics.DrawingTypedDatumSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
        else if(e.Args.Length>=2&&e.Args[0]=="--f3f-attachment-history-smoke")
            _=Diagnostics.DrawingTypedDatumSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1],continueHistory:true);
        else if(e.Args.Length>=2&&e.Args[0]=="--f3g-composed-drilling-smoke")
            _=Diagnostics.DrawingTypedDatumSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1],continueHistory:true,composedDrilling:true);
        else if(e.Args.Length>=2&&e.Args[0]=="--f3h-planar-size-smoke")
            _=Diagnostics.DrawingPlanarSizeSmokeRunner.RunAsync(mainWindow,_serviceProvider,e.Args[1]);
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
        services.AddSingleton<Cadoryx.Kernel.Abstractions.IGeometryKernel>(_=>new Cadoryx.Kernel.Occt.OcctGeometryKernel(
            new Cadoryx.Kernel.Occt.IsolatedMetricThreadExecutor(Path.Combine(AppContext.BaseDirectory,"Cadoryx.wpf.exe")),
            new Cadoryx.Kernel.Occt.IsolatedDrawingExecutor(Path.Combine(AppContext.BaseDirectory,"Cadoryx.wpf.exe")),
            Cadoryx.Kernel.Abstractions.GuidedDocumentEvidencePolicy.PreferCurrentOccurrences));
        services.AddSingleton<Cadoryx.Kernel.Abstractions.IDocumentStorage>(p=>new Cadoryx.IO.CadDocumentStorage(deferGeometryAssets:true,
            datumResolver:(Cadoryx.Kernel.Abstractions.IDatumGeometryResolver)p.GetRequiredService<Cadoryx.Kernel.Abstractions.IGeometryKernel>()));
        services.AddSingleton<Cadoryx.Kernel.Abstractions.IExchangeImportService,Cadoryx.wpf.Services.IO.IsolatedExchangeImporter>();
        services.AddSingleton<Cadoryx.Sketching.ISketchConstraintSolver>(p=>new Cadoryx.Sketching.ManagedSketchConstraintSolver(
            (Cadoryx.Kernel.Abstractions.IExactSketchExpressionRegions)p.GetRequiredService<Cadoryx.Kernel.Abstractions.IGeometryKernel>()));
        services.AddSingleton<ISketchEditorHost,Cadoryx.wpf.Views.SketchEditorHost>();
        services.AddSingleton<IParameterEditorHost,Cadoryx.wpf.Views.ParameterEditorHost>();
        services.AddSingleton<IDatumEditorHost,Cadoryx.wpf.Views.DatumEditorHost>();
        services.AddSingleton<IRepetitionEditorHost,Cadoryx.wpf.Views.RepetitionEditorHost>();
        services.AddSingleton<IAssemblyRepetitionEditorHost,Cadoryx.wpf.Views.AssemblyRepetitionEditorHost>();
        services.AddSingleton<IHoleEditorHost,Cadoryx.wpf.Views.HoleEditorHost>();
        services.AddSingleton<IExtrusionEditorHost,Cadoryx.wpf.Views.ExtrusionEditorHost>();
        services.AddSingleton<IGuidedEditorHost,Cadoryx.wpf.Views.GuidedEditorHost>();
        services.AddSingleton<IFaceFeatureEditorHost,Cadoryx.wpf.Views.FaceFeatureEditorHost>();
        services.AddSingleton<IContourLocalEditorHost,Cadoryx.wpf.Views.ContourLocalEditorHost>();
        services.AddSingleton<ILocalFeatureHost,Cadoryx.wpf.Views.LocalFeatureHost>();
        services.AddSingleton<IHistoryQueryHost,Cadoryx.wpf.Views.HistoryQueryHost>();
        services.AddSingleton<Cadoryx.ViewModels.Settings.IDocumentSettingsHost,Cadoryx.wpf.Views.Settings.DocumentSettingsHost>();
        services.AddSingleton<Cadoryx.Editor.ISessionDispatcher,WpfSessionDispatcher>();
        services.AddSingleton<Cadoryx.Kernel.Abstractions.IRecoveryStore>(provider=>
        {
            var args=Environment.GetCommandLineArgs();
            string? diagnosticOutput=Diagnostics.DiagnosticPaths.OutputDirectory(args);
            string root=diagnosticOutput is not null?Path.Combine(diagnosticOutput,"recovery"):
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Cadoryx","Recovery");
            return new Cadoryx.IO.CadRecoveryStore(root,provider.GetRequiredService<Cadoryx.Kernel.Abstractions.IDocumentStorage>());
        });
        services.AddSingleton<Cadoryx.Editor.DocumentRecoveryService>();
        services.AddSingleton<RecoveryHost>();
        services.AddSingleton<IRecoveryDialogService,RecoveryDialogService>();
        services.AddSingleton<IDocumentResourcesDialogService,DocumentResourcesDialogService>();
        services.AddSingleton<Cadoryx.Editor.CadWorkspace>();
        services.AddSingleton<ICadFileDialogs,CadFileDialogs>();
        services.AddSingleton<Cadoryx.Kernel.Abstractions.IDocumentDeliveryService,Cadoryx.IO.DocumentDeliveryService>();
        services.AddSingleton<Cadoryx.Kernel.Abstractions.IDocumentFileService,Cadoryx.IO.DocumentFileService>();
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
