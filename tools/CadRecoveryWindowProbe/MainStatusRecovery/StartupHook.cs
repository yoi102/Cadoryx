using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Lang.Strings;
using Cadoryx.Rendering.Occt;
using Cadoryx.ViewModels;
using Cadoryx.wpf;
using Cadoryx.wpf.Controls;
using Cadoryx.wpf.Views;
using OcctSharp;

// Private acceptance fixture: actual production MainWindow, status binding,
// native viewer loss, host Error messages and existing recovery code.
// No private recovery method, status setter or OS mouse/keyboard input is used.
public static class StartupHook
{
    private static readonly string[] Cultures = ["en-US", "zh-CN", "ja-JP"];
    private static readonly string[] Losses = ["native-presentation-loss", "native-owner-disposed"];
    private static readonly string[] Routes = ["recover", "detach-before-dispatch", "cleanup-callback-fails"];
    private static object? Field(object owner, string name) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(owner);
    private static int Counter(ViewportPane pane, string name) => (int)typeof(ViewportPane).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pane)!;
    private static int Hosts() => (int)typeof(OcctViewportHost).GetProperty("LiveCount", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static IEnumerable<T> Children<T>(DependencyObject owner) where T : DependencyObject
    {
        if (owner is T item) yield return item;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(owner); index++)
            foreach (var child in Children<T>(VisualTreeHelper.GetChild(owner, index))) yield return child;
    }
    private static async Task Wait(Func<bool> ready)
    {
        var watch = Stopwatch.StartNew();
        while (!ready())
        {
            Require(watch.Elapsed.TotalSeconds < 30, "Original30s WPF readiness deadline exceeded; native calls are not preempted.");
            await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(1);
        }
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
    }
    public static void Initialize()
    {
        string[] args = Environment.GetCommandLineArgs();
        if (args.Contains("--import-worker")) return;
        Require(args.Length >= 3 && args[1] == "--a5-recovery-status-smoke", "Explicit private main status acceptance arguments required.");
        _ = Task.Run(async () =>
        {
            var clock = Stopwatch.StartNew();
            while (Application.Current is null)
            {
                Require(clock.Elapsed.TotalSeconds < 60, "Actual WPF Application required within60seconds.");
                await Task.Delay(50);
            }
            _ = Application.Current.Dispatcher.BeginInvoke(new Action(async () => await RunAsync(Path.GetFullPath(args[2]))));
        });
    }
    private static void SaveShell(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);encoder.Save(stream);
    }
    private static async Task RunAsync(string output)
    {
        var rows = new List<object>();var transitions = new List<object>();
        Exception? error = null;bool passed = true;
        IAssetStoreStatistics? finalStatistics = null;
        var oldCulture = CultureInfo.CurrentUICulture;
        try
        {
            Require(Directory.Exists(output) && !File.Exists(Path.Combine(output, "result.json")), "Fresh owned output directory required.");
            await Wait(() => Application.Current.MainWindow is MainWindow { IsLoaded: true });
            var window = (MainWindow)Application.Current.MainWindow;
            var main = (MainWindowViewModel)window.DataContext;
            var services = (IServiceProvider)Field(Application.Current, "_serviceProvider")!;
            var assets = (IAssetStore)services.GetService(typeof(IAssetStore))!;
            Require(assets is MemoryAssetStore, "Only private in-memory assets permitted.");
            var statistics = (IAssetStoreStatistics)assets;
            finalStatistics = statistics;
            var storage = (IDocumentStorage)services.GetService(typeof(IDocumentStorage))!;
            var recovery = services.GetService(typeof(IRecoveryStore))!;
            Require(Path.GetFullPath((string)Field(recovery, "root")!).Equals(Path.Combine(output, "recovery"), StringComparison.OrdinalIgnoreCase), "Actual production private recovery root required; never read user recovery.");
            var statusView = Children<MainStatusBarView>(window).Single();
            var status = Children<TextBlock>(statusView).Single(block => BindingOperations.GetBinding(block, TextBlock.TextProperty)?.Path.Path == nameof(MainWindowViewModel.StatusText));
            foreach (string culture in Cultures)
            foreach (string loss in Losses)
            foreach (string route in Routes)
            foreach (int failedPane in new[] { 0, 1 })
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                string name = culture + "-" + loss + "-" + route + "-" + failedPane;
                string directory = Path.Combine(output, name);Directory.CreateDirectory(directory);
                var images = new List<object>();var checks = new List<string>();Exception? failure = null;
                main.NewCommand.Execute(null);
                var document = main.ActiveDocument!;var session = document.Session;
                await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(20, 10, 5, RigidTransform3d.Identity), "Status recovery seed"));
                await session.SaveAsync(storage, Path.Combine(directory, "before.cadoryx"));
                document.Review.SplitView = true;
                await Wait(() => Children<ViewportPane>(window).Count(p => ReferenceEquals(p.DataContext, document) && Field(p, "host") is OcctViewportHost { Viewport.VisibleBodyCount: 1 }) == 2);
                document.Review.ActivePane = failedPane;
                var panes = Children<ViewportPane>(window).Where(p => ReferenceEquals(p.DataContext, document)).OrderBy(p => p.IsSecondary).ToArray();
                var pane = panes[failedPane];var other = panes[1 - failedPane];
                var host = (OcctViewportHost)Field(pane, "host")!;var viewport = host.Viewport!;nint handle = host.Handle;
                var otherHost = (OcctViewportHost)Field(other, "host")!;var otherViewport = otherHost.Viewport!;
                Require(viewport.HasObservedValidGraphicsContext && otherViewport.HasObservedValidGraphicsContext, "Both actual production viewports need previously valid contexts.");
                var before = session.Snapshot;long generation = session.Generation;var history = session.HistoryUsage;
                bool dirty = session.IsDirty;var file = session.FilePath;int assetCount = statistics.Count;long bytes = statistics.SizeBytes;
                string beforePng = Path.Combine(directory, "before.png"), otherPng = Path.Combine(directory, "other-before.png");
                viewport.SaveScreenshot(beforePng);otherViewport.SaveScreenshot(otherPng);
                images.Add(new { kind = "before", path = beforePng, sha256 = Hash(beforePng) });
                images.Add(new { kind = "other-before", path = otherPng, sha256 = Hash(otherPng) });
                string readyText = main.StatusText;
                var caseTransitions = new List<object>();
                PropertyChangedEventHandler observe = (_, args) =>
                {
                    if (args.PropertyName == nameof(MainWindowViewModel.StatusText))
                        caseTransitions.Add(new { value = main.StatusText, boundTextAtEvent = status.Text, time = Stopwatch.GetTimestamp() });
                };
                main.PropertyChanged += observe;
                try
                {
                    if (route == "cleanup-callback-fails") host.Destroying += (_, _) => throw new InvalidOperationException("Owned main shell retirement callback failure");
                    var owner = (OcctViewer)Field(viewport, "viewer")!;
                    if (loss == "native-owner-disposed") owner.Dispose();else owner.InvalidateGpuResources();
                    Require(viewport.TryObserveRecoverableGraphicsFault(out var observed), "Actual native loss must be observed before paint.");
                    Require(loss == "native-owner-disposed" ? observed.OwnerDisposed : (observed.NativeHealth & ViewerRenderingHealth.PresentationDataLost) != 0, "Unexpected native loss evidence.");
                    _ = SendMessage(handle, 15, 0, 0);_ = SendMessage(handle, 15, 0, 0);
                    if (route == "detach-before-dispatch")
                    {
                        document.Detach();
                        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                        Require(Counter(pane, "AutomaticGraphicsRecoveryAttempts") == 0, "Detached actual document attempted recovery.");
                        Require(Field(pane, "host") is null && Field(other, "host") is null, "Actual detach retained pane hosts.");
                        Require(main.StatusText == readyText, "Canceled recovery changed the existing main shell status.");
                    }
                    else
                    {
                        await Wait(() => Counter(pane, "AutomaticGraphicsRecoveryAttempts") == 1 && (route == "recover" ? Counter(pane, "AutomaticGraphicsRecoveryCompleted") == 1 : Field(pane, "host") is null));
                        string expected = route == "recover" ? Strings.Ready : Strings.ResourceManager.GetString("ViewportRecoveryFailed")!;
                        Require(main.StatusText == expected && status.Text == expected, "Actual production status binding did not clear or report the localized failure.");
                        Require(caseTransitions.Any(row => (string)row.GetType().GetProperty("value")!.GetValue(row)! == Strings.ResourceManager.GetString("ViewportRecovering")), "Actual main shell did not report recovery beginning.");
                        Require(Counter(other, "AutomaticGraphicsRecoveryAttempts") == 0 && ReferenceEquals(Field(other, "host"), otherHost), "Other actual pane restarted or changed owner.");
                        string otherAfter = Path.Combine(directory, "other-after.png");otherViewport.SaveScreenshot(otherAfter);
                        Require(File.ReadAllBytes(otherPng).AsSpan().SequenceEqual(File.ReadAllBytes(otherAfter)), "Other actual full frame changed.");
                        images.Add(new { kind = "other-after", path = otherAfter, sha256 = Hash(otherAfter) });
                        if (route == "recover")
                        {
                            var replacement = (OcctViewportHost)Field(pane, "host")!;
                            var replacementViewport = replacement.Viewport ?? throw new InvalidOperationException("Actual replacement viewer is missing.");
                            Require(replacement.Handle != handle && !IsWindow(handle) && !replacementViewport.TryObserveRecoverableGraphicsFault(out _), "Exact failed host was not replaced with a healthy actual viewer.");
                            string after = Path.Combine(directory, "after.png");replacementViewport.SaveScreenshot(after);
                            Require(File.ReadAllBytes(beforePng).AsSpan().SequenceEqual(File.ReadAllBytes(after)), "Recovered actual full frame/camera changed.");
                            images.Add(new { kind = "after", path = after, sha256 = Hash(after) });
                        }
                        else Require(Counter(pane, "AutomaticGraphicsRecoveryCompleted") == 0, "Retirement failure was hidden by a replacement.");
                    }
                    Require(ReferenceEquals(before, session.Snapshot) && session.Generation == generation && session.HistoryUsage == history && session.IsDirty == dirty && session.FilePath == file && statistics.Count == assetCount && statistics.SizeBytes == bytes, "Graphics/status recovery changed actual document/history/ownership.");
                    Require(viewport.VisibleBodyCount == 0 && viewport.NativeGeometryCount == 0 && viewport.NativeSourceCount == 0 && viewport.NativeXdeContextCount == 0, "Failed original native resources did not drain.");
                    string shell = Path.Combine(directory, "main-shell.png");SaveShell(window, shell);
                    images.Add(new { kind = "complete-wpf-shell-final-status", path = shell, sha256 = Hash(shell) });
                    checks.Add("actual_main_status_binding_localized_ready_or_failure_without_command_replay");
                    checks.Add("actual_native_loss_coalescing_exact_host_and_other_pane_complete_frame");
                    checks.Add("original_document_history_saved_state_and_asset_ownership_retained");
                    await session.ExecuteAsync(new AddBodyCommand(new CylinderRecipe(3, 5, RigidTransform3d.Translate(30, 0, 0)), "After main shell recovery"));
                    var edited = session.Snapshot;await session.UndoAsync();Require(ReferenceEquals(before, session.Snapshot), "Post-recovery exact undo failed.");
                    await session.RedoAsync();Require(ReferenceEquals(edited, session.Snapshot), "Post-recovery exact redo failed.");
                    string saved = Path.Combine(directory, "after.cadoryx");await session.SaveAsync(storage, saved);
                    var reopenedAssets = new MemoryAssetStore();
                    using (var reopened = await storage.LoadAsync(saved, reopenedAssets))
                        Require(reopened.Snapshot.StateId == edited.StateId && reopened.Snapshot.Id == edited.Id && Cadoryx.Rendering.CadScene.FromDocument(reopened.Snapshot).Items.SequenceEqual(Cadoryx.Rendering.CadScene.FromDocument(edited).Items), "Actual production save/reopen changed the scene.");
                    Require(reopenedAssets.Count == 0 && reopenedAssets.SizeBytes == 0, "Reopened assets did not drain.");
                    checks.Add("same_actual_session_native_edit_undo_redo_and_production_save_reopen");
                }
                catch (Exception exception) { failure = exception;passed = false; }
                finally { main.PropertyChanged -= observe; }
                rows.Add(new { name, culture, loss, route, failedPane, passed = failure is null, failure = failure?.ToString(), checks, images, finalStatus = main.StatusText, boundStatusText = status.Text, attempts = Counter(pane, "AutomaticGraphicsRecoveryAttempts"), completed = Counter(pane, "AutomaticGraphicsRecoveryCompleted") });
                transitions.Add(new { name, observedChanges = caseTransitions });
                Require(!session.IsDirty, "Failed case requires diagnosis before any unsaved document can be closed.");
                Require(await main.CloseAllAsync(), "Actual saved documents did not close.");
                await Wait(() => Hosts() == 0 && statistics.Count == 0 && statistics.SizeBytes == 0);
                if (failure is not null) goto RecordedCases;
            }
            RecordedCases:;
        }
        catch (Exception exception) { error = exception;passed = false;File.WriteAllText(Path.Combine(output, "failure.txt"), exception.ToString()); }
        finally
        {
            CultureInfo.CurrentUICulture = oldCulture;
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { state = passed && error is null && rows.Count == 36 ? "PASS_ACTUAL_MAIN_STATUS_RECOVERY36_PENDING_INDEPENDENT_AUDIT" : "FAIL_ACTUAL_MAIN_STATUS_RECOVERY36", cases = rows, transitions, planned = 36, actual = rows.Count, failure = error?.ToString(), finalHosts = Hosts(), finalAssets = finalStatistics is null ? null : new { finalStatistics.Count, finalStatistics.SizeBytes }, observationsBeforeFixtureFallback = true, mainShellStatusBar = "ACTUAL_PRODUCTION_BINDING_AND_FINAL_FULL_WPF_SHELL", beginningStatusEvidence = "Property change and bound text observation; no claim that every short recovery message reached physical scanout.", actualAbsentOrInvalidContext = "NOT_RUN", physicalGpuRemoval = false, desktopInputStarted = false }, new JsonSerializerOptions { WriteIndented = true }));
            if (Application.Current.MainWindow is MainWindow window)
                typeof(MainWindow).GetMethod("CloseAfterSmoke", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            Application.Current.Shutdown(passed && error is null ? 0 : 1);
        }
    }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nuint parameter, nint value);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
}
