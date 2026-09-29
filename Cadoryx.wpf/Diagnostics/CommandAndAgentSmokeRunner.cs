using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Views.Toolboxes;
using AvalonDock.Core;

namespace Cadoryx.wpf.Diagnostics;

internal static class CommandAndAgentSmokeRunner
{
    internal static async Task RunAsync(MainWindow window, MainWindowViewModel workspace, string output)
    {
        var document = workspace.ActiveDocument ?? throw new InvalidOperationException("No active document for command smoke.");
        workspace.LayoutService.ShowAnchorable(workspace.CommandLine);
        await Idle();
        if (Find<CommandLineToolboxView>(window) is not { IsVisible: true })
            throw new InvalidOperationException("Command toolbox is not visible.");
        workspace.CommandLine.CommandText = "STATUS";
        await workspace.CommandLine.ExecuteCommand.ExecuteAsync(null);
        if (!workspace.CommandLine.Entries.Any(x => x.Kind == "Output" && x.Text.Contains(document.Session.Snapshot.Name)))
            throw new InvalidOperationException("Command toolbox did not route STATUS to the active document.");
        workspace.CommandLine.CommandText = "TOOL BOX";
        await workspace.CommandLine.ExecuteCommand.ExecuteAsync(null);
        if (!document.IsViewportConstructing)
            throw new InvalidOperationException("TOOL BOX did not start mouse-driven construction.");
        workspace.CommandLine.CommandText = "CANCEL";
        await workspace.CommandLine.ExecuteCommand.ExecuteAsync(null);
        if (document.IsViewportConstructing)
            throw new InvalidOperationException("CANCEL did not stop mouse-driven construction.");
        workspace.LayoutService.ShowAnchorable(workspace.AiAssistant);
        await Idle();
        if (Find<AiAssistantToolboxView>(window) is not { IsVisible: true })
            throw new InvalidOperationException("AI assistant toolbox is not visible.");
        if (!workspace.AiAssistant.Messages.Any(x => x.Content.Contains(document.Session.Snapshot.Name)))
            throw new InvalidOperationException("AI assistant did not bind the active document.");
        var settingsWindow = new AiAssistantSettingsWindow { Owner = window, DataContext = workspace.AiAssistant };
        settingsWindow.Show(); await Idle();
        if (!settingsWindow.IsVisible) throw new InvalidOperationException("AI settings window did not open.");
        settingsWindow.UpdateLayout();
        var settingsBitmap = new RenderTargetBitmap((int)settingsWindow.ActualWidth, (int)settingsWindow.ActualHeight,
            96, 96, PixelFormats.Pbgra32);
        settingsBitmap.Render(settingsWindow);
        var settingsEncoder = new PngBitmapEncoder(); settingsEncoder.Frames.Add(BitmapFrame.Create(settingsBitmap));
        using (var stream = File.Create(Path.Combine(output, "command-agent-settings.png"))) settingsEncoder.Save(stream);
        var originalProvider = workspace.AiAssistant.Provider;
        var originalMessage = workspace.AiAssistant.Messages[0];
        workspace.AiAssistant.Provider = originalProvider == Cadoryx.AI.Contracts.AiAssistantProvider.Codex
            ? Cadoryx.AI.Contracts.AiAssistantProvider.LmStudio
            : Cadoryx.AI.Contracts.AiAssistantProvider.Codex;
        settingsWindow.Close(); await Idle();
        if (workspace.AiAssistant.Provider != originalProvider ||
            !ReferenceEquals(workspace.AiAssistant.Messages[0], originalMessage))
            throw new InvalidOperationException("Cancelling AI settings changed the active conversation.");
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(output, "command-agent-toolboxes.png"))) encoder.Save(stream);
        await File.WriteAllTextAsync(Path.Combine(output, "command-agent-result.txt"),
            "PASS: command toolbox dispatch, AI toolbox docking, active-document context, no provider request.");
    }
    private static async Task Idle()
    {
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(100);
    }
    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) return item;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (Find<T>(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }
}
