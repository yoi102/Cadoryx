using DocumentSnapshot = Cadoryx.Db.DocumentSnapshot;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Rendering;
using Cadoryx.Rendering.Occt;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.wpf.Controls;
using Cadoryx.wpf.Views;
using OcctSharp;

internal static class Program
{
    private static readonly string[] Losses = ["native-presentation-loss", "native-owner-disposed"];
    private static readonly string[] Phases = ["completed", "progressive"];
    private static readonly string[] Routes = ["recover", "close-before-dispatch", "cleanup-callback-fails"];
    private static readonly int[] PaneCounts = [1, 2];
    private static object? Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(target);
    private static int Counter(ViewportPane pane, string name) => (int)typeof(ViewportPane).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pane)!;
    private static int Hosts() => (int)typeof(OcctViewportHost).GetProperty("LiveCount", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void Wait(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.Elapsed.TotalSeconds >= 30) throw new TimeoutException("Owned WPF recovery readiness exceeded30s; this does not preempt a native call.");
            Pump();Thread.Sleep(1);
        }
        Pump();
    }

    [STAThread]
    public static int Main(string[] args)
    {
        Require(args.Length == 1 && !Directory.Exists(args[0]), "Fresh output directory required.");
        Require(Process.GetProcessesByName("testhost").Length == 0 && Process.GetProcessesByName("Cadoryx.wpf").Length == 0, "Existing native consumers must be preserved.");
        Directory.CreateDirectory(args[0]);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var rows = new List<object>();bool passed = true;
        foreach(string loss in Losses)
        foreach(string phase in Phases)
        foreach(int paneCount in PaneCounts)
        foreach(string route in Routes)
        {
            var (row, success) = Run(args[0], loss, phase, paneCount, route);
            rows.Add(row);passed &= success;
        }
        using var process = Process.GetCurrentProcess();
        var nativeDirectory = Path.Combine(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), "occt");
        var native = process.Modules.Cast<ProcessModule>().Where(m => m.FileName is { } path && Path.GetDirectoryName(path) == nativeDirectory)
            .Select(m => new { name = m.ModuleName, path = m.FileName, sha256 = Hash(m.FileName!) }).ToArray();
        File.WriteAllText(Path.Combine(args[0], "result.json"), JsonSerializer.Serialize(new
        {
            state = passed ? "PASS_OWNED_WPF_AUTO_RECOVERY24_PENDING_INDEPENDENT_AUDIT" : "FAIL_OWNED_WPF_AUTO_RECOVERY24",
            cases = rows, native, desktopInputStarted = false, physicalGpuRemoval = false,
            actualAbsentOrInvalidContext = "NOT_RUN", mainShellStatusBar = "NOT_RUN",
            scope = "Real ViewportPane/HwndHost messages and native owner/presentation-data loss in private offscreen nonactivating windows. No OS keyboard/mouse input, physical GPU removal, or forced native context release. Observations precede fixture fallback."
        }, new JsonSerializerOptions { WriteIndented = true }));
        app.Shutdown();return passed ? 0 : 1;
    }

    private static (object, bool) Run(string output, string loss, string phase, int paneCount, string route)
    {
        string name = loss + "-" + phase + "-" + paneCount + "-" + route;
        string directory = Path.Combine(output, name);Directory.CreateDirectory(directory);
        var assets = new MemoryAssetStore();var reopenedAssets = new MemoryAssetStore();
        var kernel = new OcctGeometryKernel();var storage = new CadDocumentStorage();var log = new CadMessageLog();
        CadDocumentSession? session = null, reopened = null;
        CadDocumentViewModel? vm = null;Window? window = null;
        var panes = new List<ViewportPane>();var oldHosts = new List<OcctViewportHost>();
        var oldViewports = new List<OcctViewport>();var images = new List<object>();var checks = new List<string>();
        Exception? failure = null;object? observation = null;bool passed = false;
        try
        {
            session = new(DocumentSnapshot.Create("Owned automatic WPF recovery"), assets, kernel, new InlineSessionDispatcher());
            session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(20, 10, 5, RigidTransform3d.Identity), "Seed")).GetAwaiter().GetResult();
            session.SaveAsync(storage, Path.Combine(directory, "before.cadoryx")).GetAwaiter().GetResult();
            vm = new(session, kernel, log);var grid = new Grid();
            for(int i = 0; i < paneCount; i++)
            {
                grid.ColumnDefinitions.Add(new());var pane = new ViewportPane { DataContext = vm, IsSecondary = i == 1 };
                Grid.SetColumn(pane, i);grid.Children.Add(pane);panes.Add(pane);
            }
            window = new() { Width = 480 * paneCount, Height = 320, Left = -32000, Top = -32000,
                WindowStartupLocation = WindowStartupLocation.Manual, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, ShowActivated = false, ShowInTaskbar = false, Content = grid };
            Application.Current.MainWindow = window;window.Show();
            Wait(() => panes.All(p => Field(p, "host") is OcctViewportHost h && h.Viewport?.VisibleBodyCount == 1));
            foreach(var pane in panes)
            {
                var host = (OcctViewportHost)Field(pane, "host")!;oldHosts.Add(host);oldViewports.Add(host.Viewport!);
                Require(host.Viewport!.HasObservedValidGraphicsContext, "A real valid context must be observed before injected loss.");
            }
            var failedHost = oldHosts[0];var failedViewport = oldViewports[0];nint failedHandle = failedHost.Handle;
            var before = session.Snapshot;long generation = session.Generation;var history = session.HistoryUsage;
            int assetCount = assets.Count;long assetBytes = assets.SizeBytes;
            var path = session.FilePath;bool dirty = session.IsDirty;var camera = failedViewport.CaptureCamera();
            string beforeImage = Path.Combine(directory, "before.png");failedViewport.SaveScreenshot(beforeImage);
            images.Add(new { kind = "before", path = beforeImage, sha256 = Hash(beforeImage) });
            string? otherImage = null;
            if(paneCount == 2)
            {
                otherImage = Path.Combine(directory, "other-before.png");oldViewports[1].SaveScreenshot(otherImage);
                images.Add(new { kind = "other-before", path = otherImage, sha256 = Hash(otherImage) });
            }
            if(phase == "progressive")
            {
                var scene = vm.Scene;failedViewport.SetScene(new(scene.DocumentId, scene.StateId, []));
                failedViewport.BeginProgressiveScene(scene);failedViewport.AppendProgressiveScene(1, TimeSpan.FromMilliseconds(50), false);
            }
            if(route == "cleanup-callback-fails")
                failedHost.Destroying += (_, _) => throw new InvalidOperationException("Owned recovery teardown callback fault");
            var owner = (OcctViewer)Field(failedViewport, "viewer")!;
            if(loss == "native-owner-disposed") owner.Dispose();
            else owner.InvalidateGpuResources();
            Require(failedViewport.TryObserveRecoverableGraphicsFault(out var health), "Actual native loss must be observable.");
            Require(loss == "native-owner-disposed" ? health.OwnerDisposed : (health.NativeHealth & ViewerRenderingHealth.PresentationDataLost) != 0, "Wrong native loss observation.");
            // Both actual WM_PAINT failures are delivered through the host's
            // existing Error event. The fixture never invokes private recovery.
            _ = SendMessage(failedHandle, 15, 0, 0);_ = SendMessage(failedHandle, 15, 0, 0);
            if(route == "close-before-dispatch")
            {
                window.Close();Pump();
                Require(Counter(panes[0], "AutomaticGraphicsRecoveryAttempts") == 0, "Closed attachment still attempted recovery.");
                Require(Hosts() == 0 && panes.All(p => Field(p, "host") is null), "Close did not clear real hosts.");
            }
            else if(route == "cleanup-callback-fails")
            {
                Wait(() => Counter(panes[0], "AutomaticGraphicsRecoveryAttempts") == 1 && Field(panes[0], "host") is null);
                Require(Counter(panes[0], "AutomaticGraphicsRecoveryCompleted") == 0, "Cleanup failure was hidden by a replacement.");
                Require(Hosts() == paneCount - 1, "Failed retirement retained a native host.");
            }
            else
            {
                Wait(() => Counter(panes[0], "AutomaticGraphicsRecoveryCompleted") == 1);
                var replacement = (OcctViewportHost)Field(panes[0], "host")!;
                Require(!ReferenceEquals(replacement, failedHost) && replacement.Handle != failedHandle && !IsWindow(failedHandle), "Exact failed native host was not retired.");
                Require(replacement.Viewport is { VisibleBodyCount: 1 } && !replacement.Viewport.TryObserveRecoverableGraphicsFault(out _), "Replacement did not display a healthy complete scene.");
                Require(Counter(panes[0], "AutomaticGraphicsRecoveryAttempts") == 1 && Hosts() == paneCount, "Duplicate failure notifications caused extra recovery.");
                string afterImage = Path.Combine(directory, "after.png");replacement.Viewport!.SaveScreenshot(afterImage);
                images.Add(new { kind = "after", path = afterImage, sha256 = Hash(afterImage) });
                Require(File.ReadAllBytes(beforeImage).AsSpan().SequenceEqual(File.ReadAllBytes(afterImage)), "Recovered full frame/camera differs.");
                Require(vm.Camera == camera, "Recovery did not retain the exact cached camera.");
                checks.Add("coalesced_exact_host_replaced_current_snapshot_camera_and_complete_frame");
            }
            Require(failedViewport.VisibleBodyCount == 0 && failedViewport.NativeGeometryCount == 0 && failedViewport.NativeSourceCount == 0 && failedViewport.NativeXdeContextCount == 0, "Retired viewer resources did not drain.");
            Require(ReferenceEquals(before, session.Snapshot) && session.Generation == generation && session.HistoryUsage == history &&
                session.FilePath == path && session.IsDirty == dirty && assets.Count == assetCount && assets.SizeBytes == assetBytes,
                "Graphics failure changed model/history/saved state/asset ownership.");
            checks.Add("exact_document_history_generation_file_and_saved_state_preserved_without_command_replay");
            if(paneCount == 2 && route != "close-before-dispatch")
            {
                Require(ReferenceEquals(Field(panes[1], "host"), oldHosts[1]) && Counter(panes[1], "AutomaticGraphicsRecoveryAttempts") == 0 &&
                    !oldViewports[1].TryObserveRecoverableGraphicsFault(out _), "Other pane ownership or health changed.");
                string afterOther = Path.Combine(directory, "other-after.png");oldViewports[1].SaveScreenshot(afterOther);
                Require(File.ReadAllBytes(otherImage!).AsSpan().SequenceEqual(File.ReadAllBytes(afterOther)), "Other pane full frame changed.");
                images.Add(new { kind = "other-after", path = afterOther, sha256 = Hash(afterOther) });
                checks.Add("other_real_pane_owner_and_complete_frame_unchanged");
            }
            // Snapshot this state before any fixture cleanup, then verify real
            // authoring and production storage on this same preserved session.
            observation = new { health, attempts = Counter(panes[0], "AutomaticGraphicsRecoveryAttempts"),
                completed = Counter(panes[0], "AutomaticGraphicsRecoveryCompleted"), hosts = Hosts(),
                oldHandleStillWindow = IsWindow(failedHandle), oldVisible = failedViewport.VisibleBodyCount,
                oldGeometry = failedViewport.NativeGeometryCount, oldSource = failedViewport.NativeSourceCount,
                oldXde = failedViewport.NativeXdeContextCount, messages = log.Entries.Select(x => x.Text).ToArray() };
            session.ExecuteAsync(new AddBodyCommand(new CylinderRecipe(3, 5, RigidTransform3d.Translate(30, 0, 0)), "After graphics failure")).GetAwaiter().GetResult();
            var edited = session.Snapshot;Require(edited.Bodies.Count == 2, "Post-failure native authoring failed.");
            session.UndoAsync().GetAwaiter().GetResult();Require(ReferenceEquals(before, session.Snapshot), "Post-failure exact undo failed.");
            session.RedoAsync().GetAwaiter().GetResult();Require(ReferenceEquals(edited, session.Snapshot), "Post-failure exact redo failed.");
            string saved = Path.Combine(directory, "after.cadoryx");session.SaveAsync(storage, saved).GetAwaiter().GetResult();
            using(var loaded = storage.LoadAsync(saved, reopenedAssets).GetAwaiter().GetResult())
                reopened = new(loaded.Snapshot, reopenedAssets, kernel, new InlineSessionDispatcher(), saved);
            Require(reopened.Snapshot.Id == edited.Id && reopened.Snapshot.StateId == edited.StateId &&
                CadScene.FromDocument(edited).Items.SequenceEqual(CadScene.FromDocument(reopened.Snapshot).Items), "Production save/reopen changed the complete scene.");
            checks.Add("same_session_native_authoring_exact_undo_redo_and_production_save_reopen");
            passed = true;
        }
        catch(Exception error) { failure = error; }
        finally
        {
            // Fallback cannot change a case's previously recorded observations.
            try { vm?.Detach(); } catch(Exception error) { failure ??= error; }
            try { window?.Close(); } catch(Exception error) { failure ??= error; }
            foreach(var viewport in oldViewports)
                try { viewport.Dispose(); } catch(Exception error) { failure ??= error; }
            try { if(reopened is not null) reopened.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch(Exception error) { failure ??= error; }
            try { if(session is not null) session.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch(Exception error) { failure ??= error; }
            Pump();
        }
        passed &= failure is null && Hosts() == 0 && assets.Count == 0 && assets.SizeBytes == 0 && reopenedAssets.Count == 0 && reopenedAssets.SizeBytes == 0;
        return (new { name, loss, phase, paneCount, route, passed, failure = failure?.ToString(), checks, images, observation,
            finalHosts = Hosts(), finalAssets = new { assets.Count, assets.SizeBytes },
            reopenedAssets = new { reopenedAssets.Count, reopenedAssets.SizeBytes }, emergencyCleanupExcludedFromOutcome = true }, passed);
    }
    [DllImport("user32.dll")]private static extern nint SendMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool IsWindow(nint hwnd);
}
