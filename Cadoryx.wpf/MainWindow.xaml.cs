using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using AvalonDock;
using AvalonDock.DependencyInjection;
using AvalonDock.Themes;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Events;
using Cadoryx.ViewModels.Services.Platform;
using Cadoryx.ViewModels.Services.Platform.Settings;
using Cadoryx.wpf.Services.Application;
using CommunityToolkit.Mvvm.DependencyInjection;
using MessagePipe;

namespace Cadoryx.wpf;

public partial class MainWindow
{
    internal void CloseAfterSmoke(){_allowWindowClose=true;Close();}
    private readonly MainWindowViewModel _viewModel;
    private readonly ToolboxLayoutPersistenceService _toolboxLayoutPersistence;
    private readonly DispatcherTimer _toolboxLayoutSaveTimer;
    private readonly IDisposable _themeChangedSubscription;
    private bool _isExitConfirmationRunning;
    private bool _isToolboxLayoutPersistenceActive;
    private bool _allowWindowClose;

    public MainWindow(
        MainWindowViewModel viewModel,
        ISubscriber<ThemeChangedEvent> subscriber,
        IApplicationThemeService applicationThemeService,
        ToggleDockOptions dockOptions,
        ToolboxLayoutPersistenceService toolboxLayoutPersistence)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _toolboxLayoutPersistence = toolboxLayoutPersistence;
        DataContext = _viewModel;

        _toolboxLayoutSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(600)
        };
        _toolboxLayoutSaveTimer.Tick += OnToolboxLayoutSaveTimerTick;
        _viewModel.LayoutService.AnchorableStateChanged += OnAnchorableStateChanged;
        dockManager.LayoutChanged += OnDockLayoutChanged;
        dockManager.ContentDocked += OnDockLayoutChanged;
        dockManager.ContentFloated += OnDockLayoutChanged;
        dockManager.ActiveContentChanged+=OnActiveContentChanged;
        dockManager.DocumentClosing+=OnDocumentClosing;

        dockManager.ButtonSize = dockOptions.ButtonSize;
        dockManager.DefaultDockWidth = dockOptions.DefaultDockWidth;
        dockManager.DefaultDockHeight = dockOptions.DefaultDockHeight;
        dockManager.ShowHeaderMinimizeButton = dockOptions.ShowHeaderMinimizeButton;
        dockManager.ShowHeaderOptionsButton = dockOptions.ShowHeaderOptionsButton;

        if (Enum.TryParse<DockLayoutPriority>(dockOptions.LayoutPriority, out var priority))
            dockManager.LayoutPriority = priority;

        _themeChangedSubscription = subscriber.Subscribe(OnThemeChanged);
        Loaded += (_, _) => OnWindowLoaded(applicationThemeService);
        Deactivated += OnWindowDeactivated;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
    }

    private void OnThemeChanged(ThemeChangedEvent message)
    {
        dockManager.Theme = message.IsDark
            ? new ArcDarkTheme()
            : new ArcLightTheme();
    }

    private void OnWindowLoaded(IApplicationThemeService applicationThemeService)
    {
        if (!applicationThemeService.IsDarkTheme)
            dockManager.Theme = new ArcLightTheme();

        _toolboxLayoutPersistence.Restore(
            dockManager,
            _viewModel.LayoutService.Anchorables);
        _isToolboxLayoutPersistenceActive = true;
        if(_viewModel.Documents.Count==0)_viewModel.NewCommand.Execute(null);
    }

    private void OnAnchorableStateChanged(object? sender, EventArgs e)
        => ScheduleToolboxLayoutSave();

    private void OnDockLayoutChanged(object? sender, EventArgs e)
        => ScheduleToolboxLayoutSave();

    private void OnToolboxLayoutSaveTimerTick(object? sender, EventArgs e)
    {
        _toolboxLayoutSaveTimer.Stop();
        PersistToolboxLayout();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (_isToolboxLayoutPersistenceActive && !_allowWindowClose)
            PersistToolboxLayout();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowWindowClose)
            return;

        e.Cancel = true;
        _toolboxLayoutSaveTimer.Stop();
        PersistToolboxLayout();

        if (_isExitConfirmationRunning)
            return;

        _isExitConfirmationRunning = true;
        HandleExitConfirmationAsync();
    }

    private async void HandleExitConfirmationAsync()
    {
        try
        {
            var dialog = Ioc.Default.GetRequiredService<IDialogService>();
            if (!await dialog.ShowExitConfirmation())
                return;
            if(!await _viewModel.CloseAllAsync())return;

            _allowWindowClose = true;
            Closing -= OnWindowClosing;
            Close();
        }
        catch(Exception ex){_viewModel.Report(ex);}
        finally
        {
            _isExitConfirmationRunning = false;
        }
    }

    private void PersistToolboxLayout()
    {
        if (IsLoaded)
            _toolboxLayoutPersistence.Save(dockManager, _viewModel.LayoutService.Anchorables);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _allowWindowClose = true;
        _isToolboxLayoutPersistenceActive = false;
        _toolboxLayoutSaveTimer.Stop();
        _toolboxLayoutSaveTimer.Tick -= OnToolboxLayoutSaveTimerTick;
        _viewModel.LayoutService.AnchorableStateChanged -= OnAnchorableStateChanged;
        dockManager.LayoutChanged -= OnDockLayoutChanged;
        dockManager.ContentDocked -= OnDockLayoutChanged;
        dockManager.ContentFloated -= OnDockLayoutChanged;
        dockManager.ActiveContentChanged-=OnActiveContentChanged;
        dockManager.DocumentClosing-=OnDocumentClosing;
        Deactivated -= OnWindowDeactivated;
        Closing -= OnWindowClosing;
        Closed -= OnWindowClosed;
        _themeChangedSubscription.Dispose();
    }

    private void ScheduleToolboxLayoutSave()
    {
        if (!_isToolboxLayoutPersistenceActive || _allowWindowClose)
            return;

        _toolboxLayoutSaveTimer.Stop();
        _toolboxLayoutSaveTimer.Start();
    }

    private void IconClicked(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://github.com/yoi102/Cadoryx")
        {
            UseShellExecute = true
        });
    }
    private void OnDialogOpened(object? sender,MaterialDesignThemes.Wpf.DialogOpenedEventArgs e)
        =>Controls.OcctViewportHost.SuspendAll(true);
    private void OnDialogClosed(object? sender,MaterialDesignThemes.Wpf.DialogClosedEventArgs e)
        =>Controls.OcctViewportHost.SuspendAll(false);
    private void OnActiveContentChanged(object? sender,EventArgs e)
    {if(dockManager.ActiveContent is CadDocumentViewModel doc)_viewModel.ActiveDocument=doc;}
    private void OnDocumentClosing(object? sender,DocumentClosingEventArgs e)
    {
        if(e.Document.Content is not CadDocumentViewModel doc)return;
        e.Cancel=true;Dispatcher.BeginInvoke(()=>doc.OnClose());
    }
}
