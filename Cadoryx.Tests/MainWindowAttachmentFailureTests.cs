using System.Reflection;
using AvalonDock.Core;
using AvalonDock.Mvvm;
using Cadoryx.Agent;
using Cadoryx.Agent.Codex;
using Cadoryx.AI.Contracts;
using Cadoryx.CommandLine;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Settings;
using Cadoryx.ViewModels.Services.Platform;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Services.Platform.Settings;
using Cadoryx.ViewModels.Toolboxes;
using CommunityToolkit.Mvvm.Input;
using Xunit;

namespace Cadoryx.Tests;

public sealed class MainWindowAttachmentFailureTests
{
    [Theory]
    [InlineData("new", "open")]
    [InlineData("new", "activate")]
    [InlineData("native", "open")]
    [InlineData("native", "activate")]
    [InlineData("exchange", "open")]
    [InlineData("exchange", "activate")]
    [InlineData("new", "collection")]
    [InlineData("native", "collection")]
    [InlineData("exchange", "collection")]
    public async Task FailedProductionAttachmentPreservesOriginalActiveDocumentAndReleasesCandidate(string entry, string failure)
    {
        await using var f = new Fixture();
        await New(f.Main); var first = f.Main.ActiveDocument!;
        await New(f.Main); var last = f.Main.ActiveDocument!;
        Assert.NotSame(first, last); f.Main.ActiveDocument = first;
        var original = first.Session.Snapshot; var history = first.Session.HistoryUsage;
        var sessions = f.Workspace.Documents.ToArray();
        f.FailNext = failure;
        if (entry == "new") await New(f.Main);
        else await f.Main.OpenPathAsync(await f.Input(entry));

        Assert.Equal("Test dock " + failure + " failure", f.Main.StatusText);
        Assert.Contains(f.Log.Entries, e => e.Level == CadMessageLevel.Error && e.Text == f.Main.StatusText);
        Assert.Equal(new[] { first, last }, f.Main.Documents.ToArray());
        Assert.Equal(sessions, f.Workspace.Documents.ToArray());
        Assert.Same(first, f.Main.ActiveDocument); Assert.True(first.IsActive); Assert.False(last.IsActive);
        Assert.Same(original, first.Session.Snapshot); Assert.Equal(history, first.Session.HistoryUsage);
        Assert.False(first.IsDetached); Assert.False(last.IsDetached);
        Assert.False(first.Session.IsClosing); Assert.False(last.Session.IsClosing);
        Assert.False(f.Main.IsBusy); Assert.False(f.Main.IsOpening); Assert.Equal(0, f.Assets.Count);
        Assert.Equal(0, f.Kernel.Calls); Assert.Equal(1, f.ClosedCandidates);
        Assert.Equal(entry == "exchange" ? 1 : 0, f.ImportCalls);

        // A failed attempt must also release command/busy state for a later successful attachment.
        await New(f.Main);
        Assert.Equal(3, f.Main.Documents.Count); Assert.Equal(3, f.Workspace.Documents.Count);
        Assert.NotSame(first, f.Main.ActiveDocument); Assert.NotSame(last, f.Main.ActiveDocument);
        Assert.Same(original, first.Session.Snapshot); Assert.Equal(history, first.Session.HistoryUsage);
        Assert.Equal(0, f.Kernel.Calls); Assert.Equal(0, f.Assets.Count);
    }

    [Theory]
    [InlineData("new", "open")]
    [InlineData("new", "activate")]
    [InlineData("start_tool", "open")]
    [InlineData("start_tool", "activate")]
    [InlineData("sketch", "open")]
    [InlineData("sketch", "activate")]
    public async Task ImplicitNewFailureFromEmptyWorkspaceIsReportedWithoutStartingAConstructor(string entry, string failure)
    {
        await using var f = new Fixture(); f.FailNext = failure;
        if (entry == "new") await New(f.Main);
        else
        {
            IRelayCommand command = entry == "start_tool" ? f.Main.StartToolCommand : f.Main.NewSketchCommand;
            var parameter = entry == "start_tool" ? "Box" : null;
            if (command is IAsyncRelayCommand asyncCommand) await asyncCommand.ExecuteAsync(parameter);
            else command.Execute(parameter);
        }
        Assert.Equal("Test dock " + failure + " failure", f.Main.StatusText);
        Assert.Empty(f.Main.Documents); Assert.Empty(f.Workspace.Documents); Assert.Null(f.Main.ActiveDocument);
        Assert.False(f.Main.IsBusy); Assert.False(f.Main.IsOpening); Assert.Equal(0, f.Kernel.Calls);
        Assert.Equal(0, f.Assets.Count); Assert.Equal(1, f.ClosedCandidates);
        await New(f.Main); Assert.Single(f.Main.Documents); Assert.Single(f.Workspace.Documents);
        Assert.NotNull(f.Main.ActiveDocument); Assert.False(f.Main.ActiveDocument.IsViewportConstructing);
    }

    private static async Task New(MainWindowViewModel main)
    {
        // Exercise the public generated command in both the old synchronous and repaired async versions.
        if (main.NewCommand is IAsyncRelayCommand command) await command.ExecuteAsync(null);
        else main.NewCommand.Execute(null);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal NoGeometryKernel Kernel { get; } = new();
        internal MemoryAssetStore Assets { get; } = new();
        internal CadWorkspace Workspace { get; }
        internal CadMessageLog Log { get; } = new();
        internal MainWindowViewModel Main { get; }
        internal string? FailNext;
        internal int ClosedCandidates, ImportCalls;
        private object? activeDockable;
        private readonly TestFiles files = new();
        private readonly CadDocumentStorage storage = new();
        private readonly Dictionary<Type, object> anchorables;
        private readonly AiAssistantToolboxViewModel assistant;
        internal Fixture()
        {
            Workspace = new(Kernel, Assets, new InlineSessionDispatcher());
            var icons = new Icons(); var commands = new CadCommandLineService();
            assistant = new(Proxy<IAiAssistantSettingsStore>((m, _) => m.Name == "Load" ? new AiAssistantSettings() : null),
                Unused<IAgentRunner>(), Unused<IAiChatClient>(),
                Proxy<ICodexAgentClient>((m, _) => m.Name == "ResetConversationAsync" ? Task.CompletedTask : throw new InvalidOperationException("Unexpected AI call")),
                commands, icons);
            anchorables = new()
            {
                [typeof(ModelTreeToolboxViewModel)] = new ModelTreeToolboxViewModel(icons),
                [typeof(PropertiesToolboxViewModel)] = new PropertiesToolboxViewModel(icons),
                [typeof(ModelingToolboxViewModel)] = new ModelingToolboxViewModel(icons),
                [typeof(CommandLineToolboxViewModel)] = new CommandLineToolboxViewModel(commands, icons, Log),
                [typeof(AiAssistantToolboxViewModel)] = assistant,
                [typeof(MessagesToolboxViewModel)] = new MessagesToolboxViewModel(Log, icons),
            };
            var dock = Proxy<IDockLayoutService>((method, args) =>
            {
                if (method.Name == "get_Anchorables")
                {
                    var rows = Array.CreateInstance(method.ReturnType.GetGenericArguments().Single(), anchorables.Count);
                    int index = 0; foreach (var item in anchorables.Values) rows.SetValue(item, index++);
                    return rows;
                }
                if (method.Name == "GetAnchorable") return anchorables[method.GetGenericArguments().Single()];
                if (method.Name == "get_ActiveDockable") return activeDockable;
                if (method.Name == "set_ActiveDockable")
                {
                    if (FailNext == "activate") { FailNext = null; throw new InvalidOperationException("Test dock activate failure"); }
                    activeDockable = args![0]; return null;
                }
                if (method.Name == "OpenDocument")
                {
                    if (FailNext == "open") { FailNext = null; throw new InvalidOperationException("Test dock open failure"); }
                    return null;
                }
                if (method.Name == "CloseDocument") { ClosedCandidates++; return null; }
                throw new InvalidOperationException("Unexpected dock operation: " + method.Name);
            });
            var notifications = Proxy<ICadNotificationService>((method, args) => method.Name switch
            {
                "SetAnchor" => null,
                "RunWithProgressAsync" => ((Func<CancellationToken, Task>)args![0]!)((CancellationToken)args[2]!),
                _ => throw new InvalidOperationException("Unexpected notification operation"),
            });
            var dialogs = Proxy<IDialogService>((method, args) => method.Name == "RunWithProgressAsync"
                ? ((Func<CancellationToken, Task>)args![0]!)(default)
                : throw new InvalidOperationException("Unexpected dialog operation"));
            var importer = Proxy<IExchangeImportService>((method, args) =>
            {
                Assert.Equal("ImportAsync", method.Name); ImportCalls++;
                // A serialized empty author fixture exercises importer dispatch and leases without OCCT.
                return storage.LoadAsync((string)args![0]!, (IAssetStore)args[1]!, (CancellationToken)args[2]!);
            });
            Main = new(dock, null!,
                Proxy<IApplicationCultureService>((_, _) => null), Proxy<IApplicationThemeService>((_, _) => null),
                Proxy<IApplicationSettingsStore>((m, _) => m.Name == "Load" ? new CadoryxApplicationSettings() : null),
                dialogs, Workspace, Kernel, Assets, importer, storage, Unused<ICadFileDialogs>(), Log, notifications,
                new DocumentRecoveryService(Unused<IRecoveryStore>()), Unused<IRecoveryStore>(), Unused<IRecoveryDialogService>(),
                Unused<IDocumentResourcesDialogService>(), Unused<ISketchEditorHost>(), null!, Unused<ILocalFeatureHost>(),
                Unused<IHistoryQueryHost>(), Unused<IDocumentSettingsHost>(), Unused<IDocumentDeliveryService>(), Unused<IDeliveryHost>());
            Main.Documents.CollectionChanged += (_, change) =>
            {
                if (change.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && FailNext == "collection")
                { FailNext = null; throw new InvalidOperationException("Test dock collection failure"); }
            };
        }
        internal async Task<string> Input(string entry)
        {
            var path = files.PathFor(entry == "native" ? "loaded.cadoryx" : "imported.step");
            using (var asset = Assets.Stage([19, 23, 29]))
            {
                var snapshot = DocumentSnapshot.Create("Loaded author") with { Extensions = [new("vendor.attachment", 1, asset.Id, "vendor")] };
                await storage.SaveAsync(snapshot, Assets, path);
            }
            Assert.Equal(0, Assets.Count); return path;
        }
        public async ValueTask DisposeAsync()
        {
            ((ModelTreeToolboxViewModel)anchorables[typeof(ModelTreeToolboxViewModel)]).Bind(null);
            ((PropertiesToolboxViewModel)anchorables[typeof(PropertiesToolboxViewModel)]).Bind(null);
            assistant.Dispose();
            foreach (var document in Main.Documents) document.Detach();
            await Workspace.DisposeAsync(); files.Dispose();
            Assert.Equal(0, Assets.Count); Assert.Equal(0, Kernel.Calls);
        }
    }
    private static T Unused<T>() where T : class => Proxy<T>((m, _) => throw new InvalidOperationException("Unexpected service call: " + m.Name));
    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    { var value = DispatchProxy.Create<T, ServiceProxy>(); ((ServiceProxy)(object)value).Handler = handler; return value; }
    public class ServiceProxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }
    private sealed class Icons : IToolboxIconProvider
    { public object ModelTree { get; } = new(); public object Properties { get; } = new(); public object Modeling { get; } = new(); public object Messages { get; } = new(); }
    private sealed class NoGeometryKernel : IGeometryKernel
    {
        internal int Calls;
        public string Version => "No native code in main-window attachment tests";
        public bool Supports(GeometryRecipe recipe) => false;
        public Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe, IAssetStore assets, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Unexpected geometry evaluation"); }
        public Task<LoadedDocument> ImportAsync(string path, IAssetStore assets, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Unexpected native import"); }
        public Task ExportAsync(DocumentSnapshot snapshot, IAssetStore assets, string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CadExportReport> ExportAsync(DocumentSnapshot snapshot, IAssetStore assets, string path, CadExportOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
