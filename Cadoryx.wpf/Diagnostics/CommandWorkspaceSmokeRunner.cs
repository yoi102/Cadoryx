using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Views.Toolboxes;
using Microsoft.Extensions.DependencyInjection;

namespace Cadoryx.wpf.Diagnostics;

internal static partial class CommandAndAgentSmokeRunner
{
    private static async Task CheckWorkspaceAuthoring(MainWindow window, MainWindowViewModel workspace, IServiceProvider services, string output)
    {
        var original = workspace.ActiveDocument ?? throw new InvalidOperationException("Workspace smoke has no initial document.");
        var originalSnapshot = original.Session.Snapshot;
        var model = workspace.CommandLine;
        var view = Find<CommandLineToolboxView>(window) ?? throw new InvalidOperationException("Actual terminal view is missing.");
        if (!ReferenceEquals(view.DataContext, model)) throw new InvalidOperationException("Terminal is not bound to the production workspace toolbox.");
        var receipts = new List<JsonElement>();
        await Run("WORKSPACE CONTRACT"); await Run("WORKSPACE LIST");
        var beforeCount = workspace.Documents.Count;
        var created = await Run("WORKSPACE {\"action\":\"new\",\"name\":\"Terminal workspace part\"}");
        receipts.Add(created);
        var authored = workspace.ActiveDocument ?? throw new InvalidOperationException("New document did not become active.");
        if (ReferenceEquals(authored, original) || workspace.Documents.Count != beforeCount + 1 || model.ExecuteCommand.IsCancellationRequested)
            throw new InvalidOperationException("Workspace new cancelled its own command or failed to attach.");
        await Run("BOX WorkspaceBox 2 3 4", false);
        if (authored.Session.Snapshot.Bodies.Count != 1 || !ReferenceEquals(originalSnapshot, original.Session.Snapshot))
            throw new InvalidOperationException("Next command did not edit only the newly active document.");
        var authoredPath = Path.GetFullPath(Path.Combine(output, "command-workspace-authored.cadoryx"));
        await Run("FILE " + JsonSerializer.Serialize(new { action = "save", path = authoredPath }));
        receipts.Add(await Activate(original));
        receipts.Add(await Run("WORKSPACE " + JsonSerializer.Serialize(new { action = "open", path = authoredPath })));
        if (!ReferenceEquals(workspace.ActiveDocument, authored) || workspace.Documents.Count != beforeCount + 1)
            throw new InvalidOperationException("Reopening native path did not activate its existing session.");
        var assets = new MemoryAssetStore(); var kernel = services.GetRequiredService<IGeometryKernel>();
        var storage = services.GetRequiredService<IDocumentStorage>();
        var sourcePath = Path.GetFullPath(Path.Combine(output, "command-workspace-source.cadoryx"));
        DocumentSnapshot source;
        await using (var session = new CadDocumentSession(DocumentSnapshot.Create("Workspace read source"), assets, kernel, services.GetRequiredService<ISessionDispatcher>()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(3, 4, 5, RigidTransform3d.Identity), "Read box"));
            source = session.Snapshot; await session.SaveAsync(storage, sourcePath);
        }
        if (assets.Count != 0) throw new InvalidOperationException("Workspace input fixture retained resources.");
        receipts.Add(await Run("WORKSPACE " + JsonSerializer.Serialize(new { action = "open", path = sourcePath })));
        var opened = workspace.ActiveDocument!;
        if (workspace.Documents.Count != beforeCount + 2 || opened.Session.Snapshot.Id != source.Id || opened.Session.Snapshot.StateId != source.StateId || opened.Session.IsDirty)
            throw new InvalidOperationException("Real terminal open did not preserve native author identity/state.");
        var list = await Run("WORKSPACE LIST"); receipts.Add(list);
        view.OutputList.SelectedIndex = model.Entries.Count - 1; await Idle(); view.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(output, "command-workspace.png"))) encoder.Save(stream);
        await Activate(original);
        if (!ReferenceEquals(workspace.ActiveDocument, original) || !ReferenceEquals(originalSnapshot, original.Session.Snapshot))
            throw new InvalidOperationException("Original author document changed during workspace tool smoke.");
        await File.WriteAllTextAsync(Path.Combine(output, "command-workspace-result.json"), JsonSerializer.Serialize(new { passed = true,
            input = "Actual production terminal TextBox binding and Execute Automation Invoke, real workspace/dock/native storage",
            receipts, addedSessions = 2, originalStatePreserved = true, nextCommandModelsActiveDocument = true,
            selfTransitionDidNotCancel = true, physicalPointer = "NOT RUN", providerRequest = "NOT RUN" }));

        Task<JsonElement> Activate(CadDocumentViewModel doc) => Run("WORKSPACE " + JsonSerializer.Serialize(new { action = "activate",
            target = new { sessionId = doc.Session.SessionId, documentId = doc.Session.Snapshot.Id.ToString(), stateId = doc.Session.Snapshot.StateId.ToString() } }));
        async Task<JsonElement> Run(string command, bool json = true)
        {
            var input = Find<TextBox>(view) ?? throw new InvalidOperationException("Terminal input missing.");
            input.SetCurrentValue(TextBox.TextProperty, command); input.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            var button = Find<Button>(view, b => ReferenceEquals(b.Command, model.ExecuteCommand)) ?? throw new InvalidOperationException("Terminal Execute missing.");
            if (!button.IsEnabled) throw new InvalidOperationException("Terminal Execute disabled.");
            var peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
            var invoke = peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider ?? throw new InvalidOperationException("Terminal Execute has no Invoke provider.");
            var count = model.Entries.Count; invoke.Invoke(); await Idle();
            await (model.ExecuteCommand.ExecutionTask ?? throw new InvalidOperationException("Terminal execution did not start.")).WaitAsync(TimeSpan.FromSeconds(60)); await Idle();
            var added = model.Entries.Skip(count).ToArray();
            if (added.Any(e => e.Kind == "Error")) throw new InvalidOperationException("Workspace terminal failed: " + string.Join("; ", added.Select(e => e.Text)));
            if (!json) return default;
            using var parsed = JsonDocument.Parse(added.LastOrDefault(e => e.Kind == "Output")?.Text ?? throw new InvalidOperationException("Terminal receipt missing."));
            if (!parsed.RootElement.GetProperty("success").GetBoolean()) throw new InvalidOperationException("Workspace receipt failed.");
            return parsed.RootElement.Clone();
        }
    }
}
