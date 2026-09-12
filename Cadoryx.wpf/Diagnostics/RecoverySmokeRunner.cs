using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform;
using Cadoryx.wpf.Controls;
using Cadoryx.wpf.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using MaterialDesignThemes.Wpf;

namespace Cadoryx.wpf.Diagnostics;

/// <summary>Two real desktop processes, with an external forced termination between them.</summary>
internal static class RecoverySmokeRunner
{
    internal static async Task RunAsync(MainWindow window, IServiceProvider services, string mode, string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        bool seed = mode == "--recovery-seed";
        using var bindingOutput = new StreamWriter(Path.Combine(output, seed ? "seed-bindings.log" : "bindings.log"));
        using var listener = new TextWriterTraceListener(bindingOutput);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            await Idle();
            var vm = (MainWindowViewModel)window.DataContext;
            var storage = services.GetRequiredService<IDocumentStorage>();
            var store = services.GetRequiredService<IRecoveryStore>();
            var recovery = services.GetRequiredService<DocumentRecoveryService>();
            var first = vm.ActiveDocument ?? throw new InvalidOperationException("No startup document.");
            string original = Path.Combine(output, "original.cadoryx");
            if (seed)
            {
                await first.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10, 20, 30, RigidTransform3d.Identity), "Saved box"));
                await first.Session.SaveAsync(storage, original);
                await first.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(5, 6, 7, RigidTransform3d.Translate(20, 0, 0)), "Unsaved box"));
                await first.Session.ExecuteAsync(new EditDocumentCommand("Rename", d => d with { Name = "恢复测试 · 未保存修改" }));
                var layer=new CadLayer(LayerId.New(),"Recovered layer",0xFFED9A4B);
                var material=new CadMaterial(MaterialId.New(),"Recovered alloy",2.7e-6);
                await first.Session.ExecuteAsync(ResourceCommands.AddLayer(layer));
                await first.Session.ExecuteAsync(ResourceCommands.AddMaterial(material));
                var body=first.Session.Snapshot.Bodies.Values.Single(b=>b.Name=="Unsaved box");
                await first.Session.ExecuteAsync(DocumentEdits.SetBodyProperties(body.Id,body.Name,new(ByLayer:true),true,layer.Id,material.Id));
                var occurrence=first.Session.Snapshot.EnumerateOccurrences().Single();
                await first.Session.ExecuteAsync(DocumentEdits.MoveOccurrence(occurrence.Path,RigidTransform3d.Translate(3,4,5)));
                var expected = Expected.Capture(first.Session.Snapshot, await HashFile(original));
                await File.WriteAllTextAsync(Path.Combine(output, "expected.json"), JsonSerializer.Serialize(expected, CadJson.Options));
                // Deliberately do not invoke CheckpointAsync: this must exercise the production 30-second timer.
                string directory = Path.Combine(output, "recovery", ((CadRecoveryStore)store).RunId.ToString("N"), first.Session.SessionId.ToString("N"));
                var deadline = Stopwatch.StartNew();
                while (!Directory.Exists(directory) || !Directory.EnumerateFiles(directory, "*.json").Any())
                {
                    if (deadline.Elapsed > TimeSpan.FromSeconds(45)) throw new TimeoutException("The production timer did not create a checkpoint.");
                    await Task.Delay(100);
                }
                await recovery.DrainAsync();
                Require(first.Session.IsDirty && first.Session.FilePath == original, "Checkpoint changed the savepoint.");
                listener.Flush(); bindingOutput.Flush();
                await File.WriteAllTextAsync(Path.Combine(output, "seed.ready"), $"PASS: timer checkpoint; terminate PID {Environment.ProcessId} without closing documents.");
                return; // Keep the window alive; verify.ps1 kills only this process.
            }

            var expectedState = JsonSerializer.Deserialize<Expected>(await File.ReadAllTextAsync(Path.Combine(output, "expected.json")), CadJson.Options)!;
            await first.Session.SaveAsync(storage, Path.Combine(output, "startup.cadoryx"));
            await vm.CheckForRecoveryAsync(); await Idle();
            var center = Find<RecoveryDialog>(window) ?? throw new InvalidOperationException("Recovery dialog was not shown.");
            var centerVm = (RecoveryCenterViewModel)center.DataContext;
            if (centerVm.RefreshCommand.ExecutionTask is {} refresh) await refresh;
            Require(centerVm.Entries.Count == 1, "The crashed process was not discovered exactly once.");
            SaveVisual(center, Path.Combine(output, "recovery-center.png"));
            var priorCulture = System.Globalization.CultureInfo.GetCultureInfo(vm.CurrentCultureLCID);
            try
            {
                foreach (string culture in new[] { "zh-CN", "ja-JP" })
                {
                    Antelcat.I18N.WPF.I18NExtension.Culture = System.Globalization.CultureInfo.GetCultureInfo(culture);
                    await Idle(); SaveVisual(center, Path.Combine(output, $"recovery-center-{culture}.png"));
                }
            }
            finally { Antelcat.I18N.WPF.I18NExtension.Culture = priorCulture; }
            await centerVm.RestoreCommand.ExecuteAsync(null); await Idle();
            var restored = vm.ActiveDocument ?? throw new InvalidOperationException("No restored dock document.");
            Require(!ReferenceEquals(first, restored), "Recovery did not attach a new dock document.");
            Require(restored.Session.FilePath is null && restored.Session.IsDirty, "Recovered document is not an unsaved copy.");
            Require(restored.Session.RecoveryOriginPath == original, "Original path was lost.");
            var actual = Expected.Capture(restored.Session.Snapshot, await HashFile(original));
            Require(JsonSerializer.Serialize(actual, CadJson.Options) == JsonSerializer.Serialize(expectedState, CadJson.Options), "Restored IDs/assets/state differ or the original file was changed.");
            Require(centerVm.Entries.Count == 0, "The transferred source still appears in recovery.");
            DialogHost.GetDialogSession(ViewServiceIdentifiers.RootDialogHost)?.Close(); await Idle();
            var host = Find<OcctViewportHost>(window) ?? throw new InvalidOperationException("No recovered viewport host.");
            var viewport = host.Viewport ?? throw new InvalidOperationException("Recovered native viewer did not initialize.");
            viewport.FitAll(); viewport.SaveScreenshot(Path.Combine(output, "restored-viewport.png"));
            SaveVisual(window, Path.Combine(output, "restored-shell.png"));
            await restored.Session.SaveAsync(storage, Path.Combine(output, "recovered-copy.cadoryx"));
            await recovery.CheckpointAsync(restored.Session);
            Require(await HashFile(original) == expectedState.OriginalHash, "Save copy overwrote the original.");
            Require(await vm.CloseAllAsync(), "Normal document close failed.");
            await ((App)System.Windows.Application.Current).StopRecoveryAsync();
            Require((await store.ScanAsync()).Entries.Count == 0, "Closed documents left recoverable entries.");
            Require(!Directory.EnumerateFiles(Path.Combine(output, "recovery"), "*.json", SearchOption.AllDirectories).Any(), "Normal close left checkpoint metadata.");
            Require(((MemoryAssetStore)services.GetRequiredService<IAssetStore>()).Count == 0, "Recovered assets leaked after close.");
            listener.Flush(); bindingOutput.Flush();
            await File.WriteAllTextAsync(Path.Combine(output, "result.txt"), "PASS: production 30-second timer, forced process termination, startup recovery center, restore command, exact IDs/state/assets, unsaved copy, unchanged original, real native viewport, save copy, normal-close cleanup, zero remaining assets.");
            window.CloseAfterSmoke();
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(output, seed ? "seed-result.txt" : "result.txt"), "FAIL: " + ex);
            System.Windows.Application.Current.Shutdown(1);
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); }
    }

    private sealed record Expected(string DocumentId, string StateId, string Name, string[] Bodies, string[] Assets, string OriginalHash,
        string[] Layers,string[] Materials,string[] Placements)
    {
        public static Expected Capture(DocumentSnapshot snapshot, string hash) => new(snapshot.Id.ToString(), snapshot.StateId.ToString(), snapshot.Name,
            snapshot.Bodies.Values.OrderBy(b=>b.Id.Value).Select(b=>JsonSerializer.Serialize(b,CadJson.Options)).ToArray(),
            snapshot.ReferencedAssets().Select(id => id.ToString()).Order().ToArray(), hash,
            snapshot.Layers.Values.OrderBy(l=>l.Id.Value).Select(l=>JsonSerializer.Serialize(l,CadJson.Options)).ToArray(),
            snapshot.Materials.Values.OrderBy(m=>m.Id.Value).Select(m=>JsonSerializer.Serialize(m,CadJson.Options)).ToArray(),
            snapshot.EnumerateOccurrences().Select(o=>o.Path+" "+JsonSerializer.Serialize(o.WorldTransform,CadJson.Options)).Order().ToArray());
    }
    private static async Task<string> HashFile(string path) => Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Idle()
    { await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(200); }
    private static void SaveVisual(FrameworkElement window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T found) return found;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) if (Find<T>(VisualTreeHelper.GetChild(root, i)) is {} child) return child;
        return null;
    }
}
