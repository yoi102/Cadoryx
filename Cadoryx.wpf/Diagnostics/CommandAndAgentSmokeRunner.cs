using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Views.Toolboxes;
using AvalonDock.Core;
using Cadoryx.CommandLine;
using Cadoryx.ViewModels.Toolboxes;
using Cadoryx.ViewModels.Services.Platform;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Microsoft.Extensions.DependencyInjection;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace Cadoryx.wpf.Diagnostics;

internal static partial class CommandAndAgentSmokeRunner
{
    internal static async Task RunAsync(MainWindow window, MainWindowViewModel workspace, IServiceProvider services, string output)
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
        workspace.FitViewCommand.Execute(null);
        if (!workspace.CommandLine.Entries.Any(x => x.Kind == "Action" && x.Text == "Fit view"))
            throw new InvalidOperationException("Ribbon view command was not printed in the terminal.");
        workspace.CommandLine.CommandText = "LAYERS";
        await workspace.CommandLine.ExecuteCommand.ExecuteAsync(null);
        if (!workspace.CommandLine.Entries.Any(x => x.Kind == "Output" && x.Text.Contains("Default")))
            throw new InvalidOperationException("LAYERS did not query the active document.");
        var originalSpacing = document.GridSpacingMm;
        var changedSpacing = originalSpacing == 12.5 ? 15 : 12.5;
        workspace.CommandLine.CommandText = "GRID SPACING " + changedSpacing.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await workspace.CommandLine.ExecuteCommand.ExecuteAsync(null);
        if (document.GridSpacingMm != changedSpacing)
            throw new InvalidOperationException("GRID did not update the active document.");
        workspace.CommandLine.CommandText = "UNDO";
        await workspace.CommandLine.ExecuteCommand.ExecuteAsync(null);
        if (document.GridSpacingMm != originalSpacing)
            throw new InvalidOperationException("UNDO did not restore the original grid state.");
        workspace.CommandLine.CommandText = "DISPLAY WIREFRAME";
        await workspace.CommandLine.ExecuteCommand.ExecuteAsync(null);
        if (document.CurrentDisplayMode != Cadoryx.Rendering.CadDisplayMode.Wireframe)
            throw new InvalidOperationException("DISPLAY did not switch the active viewport.");
        workspace.CommandLine.CommandText = "DISPLAY SHADED";
        await workspace.CommandLine.ExecuteCommand.ExecuteAsync(null);
        var item = document.Scene.Items.FirstOrDefault() ??
            throw new InvalidOperationException("No geometry available for command visibility smoke.");
        {
            var previousSelection = document.Selection.Items;
            document.Selection.Replace([new Cadoryx.Editor.SelectionTarget(item.Path, item.BodyId, item.Geometry.Revision)]);
            workspace.CommandLine.CommandText = "HIDE";
            await workspace.CommandLine.ExecuteCommand.ExecuteAsync(null);
            if (!document.Review.IsFiltered)
                throw new InvalidOperationException("HIDE did not change review visibility.");
            workspace.CommandLine.CommandText = "SHOWALL";
            await workspace.CommandLine.ExecuteCommand.ExecuteAsync(null);
            if (document.Review.IsFiltered)
                throw new InvalidOperationException("SHOWALL did not restore review visibility.");
            document.Selection.Replace(previousSelection);
        }
        workspace.CommandLine.CommandText = "TOOL BOX";
        await workspace.CommandLine.ExecuteCommand.ExecuteAsync(null);
        if (!document.IsViewportConstructing)
            throw new InvalidOperationException("TOOL BOX did not start mouse-driven construction.");
        workspace.CommandLine.CommandText = "CANCEL";
        await workspace.CommandLine.ExecuteCommand.ExecuteAsync(null);
        if (document.IsViewportConstructing)
            throw new InvalidOperationException("CANCEL did not stop mouse-driven construction.");
        await CheckExecutionCancel(window,document,services,output,workspace.CurrentCultureLCID);
        await CheckParameterAuthoring(window,services,output);
        await CheckModelAuthoring(window,services,output);
        await CheckFileOutputs(window,services,output);
        await CheckDrawingAuthors(window,services,output);
        await CheckWorkspaceAuthoring(window,workspace,services,output);
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
    private static async Task CheckExecutionCancel(MainWindow window,CadDocumentViewModel document,IServiceProvider services,string output,int cultureLcid)
    {
        var view=Find<CommandLineToolboxView>(window)??throw new InvalidOperationException("Command view missing.");
        var originalModel=view.DataContext;var originalCulture=System.Globalization.CultureInfo.GetCultureInfo(cultureLcid);
        var delayed=new CancellationProbe(services.GetRequiredService<ICadCommandLineService>());
        var model=new CommandLineToolboxViewModel(delayed,services.GetRequiredService<IToolboxIconProvider>(),new CadMessageLog());
        var before=document.Session.Snapshot;Task? pending=null;
        try
        {
            model.Bind(document);view.DataContext=model;model.CommandText="__smoke_wait";
            pending=model.ExecuteCommand.ExecuteAsync(null);await delayed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));await Idle();
            var button=Find<Button>(view,b=>ReferenceEquals(b.Command,model.ExecuteCancelCommand))??throw new InvalidOperationException("Execution cancel button is not bound.");
            if(!button.IsEnabled||!model.IsRunning)throw new InvalidOperationException("Pending execution cannot be cancelled from the terminal.");
            view.OutputList.SelectedIndex=1;await Idle();
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})
            {
                Antelcat.I18N.WPF.I18NExtension.Culture=System.Globalization.CultureInfo.GetCultureInfo(language);await Idle();view.UpdateLayout();
                var expectedName=language switch{"zh-CN"=>"取消","ja-JP"=>"キャンセル",_=>"Cancel"};
                if(System.Windows.Automation.AutomationProperties.GetName(button)!=expectedName||button.ToolTip?.ToString()!=expectedName)
                    throw new InvalidOperationException("Execution cancel accessible name or tooltip did not follow "+language+".");
                var bitmap=new RenderTargetBitmap((int)view.ActualWidth,(int)view.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(view);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file=File.Create(Path.Combine(output,"command-cancel-"+language+".png"));encoder.Save(file);
            }
            var peer=UIElementAutomationPeer.CreatePeerForElement(button)??new ButtonAutomationPeer(button);
            if(peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)throw new InvalidOperationException("Cancel button has no Invoke provider.");
            invoke.Invoke();await pending.WaitAsync(TimeSpan.FromSeconds(10));await Idle();
            if(model.IsRunning||button.IsEnabled||!ReferenceEquals(before,document.Session.Snapshot)||
               !model.Entries.Any(e=>e.Kind=="Error"&&e.Text=="Command cancelled."))
                throw new InvalidOperationException("Execution cancel did not finish without a document edit.");
            await File.WriteAllTextAsync(Path.Combine(output,"command-cancel-result.json"),System.Text.Json.JsonSerializer.Serialize(new
            {passed=true,input="Actual WPF cancel binding and Automation Invoke with a controlled async executor; native command cancellation is separately covered by real-kernel tests",stateId=before.StateId.ToString(),languages=new[]{"en-US","zh-CN","ja-JP"}}));
        }
        finally
        {
            model.ExecuteCommand.Cancel();if(pending is not null)await pending;model.Bind(null);
            view.DataContext=originalModel;Antelcat.I18N.WPF.I18NExtension.Culture=originalCulture;
        }
    }
    private sealed class CancellationProbe(ICadCommandLineService inner):ICadCommandLineService
    {
        public TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<CadCommandDescriptor> Commands=>inner.Commands;
        public IReadOnlyList<string> Complete(string prefix,int limit=12)=>inner.Complete(prefix,limit);
        public async Task<CadCommandResult> ExecuteAsync(string input,ICadCommandContext? context,CancellationToken cancellationToken=default)
        {
            context?.ValidateInvocation();Entered.TrySetResult();
            try{await Task.Delay(Timeout.Infinite,cancellationToken);throw new InvalidOperationException("Probe completed without cancellation.");}
            catch(OperationCanceledException){return new(false,"Command cancelled.",IsCancelled:true);}
        }
    }

    private static async Task CheckParameterAuthoring(MainWindow window, IServiceProvider services, string output)
    {
        var view = Find<CommandLineToolboxView>(window) ?? throw new InvalidOperationException("Command view missing.");
        var originalModel = view.DataContext;
        var assets = new Cadoryx.Kernel.Abstractions.MemoryAssetStore();
        var kernel = services.GetRequiredService<Cadoryx.Kernel.Abstractions.IGeometryKernel>();
        await using var session = new Cadoryx.Editor.CadDocumentSession(Cadoryx.Db.DocumentSnapshot.Create("Terminal parameters"),
            assets, kernel, services.GetRequiredService<Cadoryx.Editor.ISessionDispatcher>());
        var messages = new CadMessageLog();
        var document = new CadDocumentViewModel(session, kernel, messages);
        var model = new CommandLineToolboxViewModel(services.GetRequiredService<ICadCommandLineService>(),
            services.GetRequiredService<IToolboxIconProvider>(), messages);
        try
        {
            model.Bind(document); view.DataContext = model; await Idle();
            await Run("BOX Driven 2 3 4");
            var created = await Run("PARAM SET NEW width LENGTH \"6 mm\"");
            using var parsed = System.Text.Json.JsonDocument.Parse(created);
            var parameterId = parsed.RootElement.GetProperty("id").GetString();
            var feature = session.Snapshot.Features.Values.Single();
            var before = session.Snapshot;
            await Run($"PARAM BIND {parameterId} Feature {feature.Id} X");
            var after = session.Snapshot;
            if (Math.Abs(after.Features[feature.Id].Result.VolumeMm3 - 72) > 1e-7)
                throw new InvalidOperationException("Actual terminal authoring did not rebuild the native box.");
            await Run("UNDO"); if (!ReferenceEquals(before, session.Snapshot)) throw new InvalidOperationException("Parameter binding did not undo as one transaction.");
            await Run("REDO"); if (!ReferenceEquals(after, session.Snapshot)) throw new InvalidOperationException("Parameter binding did not redo as one transaction.");
            model.ClearCommand.Execute(null);
            var inspection = await Run("PARAMETERS 0 1");
            using var inspected = System.Text.Json.JsonDocument.Parse(inspection);
            if (inspected.RootElement.GetProperty("items")[0].GetProperty("value").GetDouble() != 6 ||
                inspected.RootElement.GetProperty("items")[0].GetProperty("unit").GetString() != "mm")
                throw new InvalidOperationException("Terminal parameter query lost the actual value or unit.");
            view.OutputList.SelectedIndex=model.Entries.Count-1;
            await Idle(); view.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(output, "command-parameters.png"))) encoder.Save(file);
            await Run("TARGETS 0 3"); await Run($"TIMELINE {feature.PartId} 0 1");
            var author=session.Snapshot;
            await Run($"HISTORY ROLLBACK {feature.PartId} 0");
            if(!ReferenceEquals(author,session.Snapshot)||!session.IsRolledBack||session.DisplaySnapshot.Bodies.Count!=0)
                throw new InvalidOperationException("Actual terminal history rollback changed the author state or exposed a future output.");
            await Run("HISTORY END");
            if(!ReferenceEquals(author,session.Snapshot)||session.IsRolledBack)throw new InvalidOperationException("Actual terminal did not return to the author history end.");
            await Run($"HISTORY SUPPRESS {feature.Id} {session.Snapshot.Features[feature.Id].Result.Revision}");
            if(!session.Snapshot.Features[feature.Id].IsSuppressed||session.Snapshot.Bodies.ContainsKey(feature.OutputBodyId))
                throw new InvalidOperationException("Actual terminal suppression did not remove the native result.");
            await Run($"HISTORY RESTORE {feature.Id} {session.Snapshot.Features[feature.Id].Result.Revision}");
            if(session.Snapshot.Features[feature.Id].IsSuppressed||Math.Abs(session.Snapshot.Features[feature.Id].Result.VolumeMm3-72)>1e-7)
                throw new InvalidOperationException("Actual terminal restoration did not recompute the native result.");
            var datumCreated=await Run("DATUM "+System.Text.Json.JsonSerializer.Serialize(new
            {action="set_plane",partId=feature.PartId.ToString(),name="Terminal plane",author=new{kind="Offset",primary=new{kind="Basis",basis="XY"},value=2}}));
            using var datumParsed=System.Text.Json.JsonDocument.Parse(datumCreated);var datumId=datumParsed.RootElement.GetProperty("id").GetGuid();
            var draft=new Cadoryx.Editor.SketchDraft(Cadoryx.Db.CadSketch.Create(feature.PartId,"Terminal sketch",Cadoryx.Db.RigidTransform3d.Identity));draft.AddRectangle(new(0,0),new(4,3));
            await session.ExecuteAsync(new Cadoryx.Commands.UpsertSketchCommand(draft.Value,new Cadoryx.Sketching.ManagedSketchConstraintSolver()));
            var sketch=session.Snapshot.Sketches[draft.Value.Id];
            await Run($"DATUM BIND_SKETCH {sketch.Id} {sketch.Revision:D} {datumId:D} {session.Snapshot.Datums.Planes[datumId].Revision:D}");
            var occurrence=session.Snapshot.EnumerateOccurrences().Single(o=>o.DefinitionId==feature.PartId);
            await Run($"DATUM BIND_WORKPLANE {datumId:D} {session.Snapshot.Datums.Planes[datumId].Revision:D} {occurrence.Path}");
            var datumBefore=session.Snapshot;
            await Run("DATUM "+System.Text.Json.JsonSerializer.Serialize(new
            {action="set_plane",id=datumId,revision=session.Snapshot.Datums.Planes[datumId].Revision,partId=feature.PartId.ToString(),name="Terminal plane",
                author=new{kind="Offset",primary=new{kind="Basis",basis="XY"},value=5}}));
            var datumAfter=session.Snapshot;
            if(Math.Abs(datumAfter.Sketches[sketch.Id].Plane.Translation.Z-5)>1e-9||Math.Abs(datumAfter.Settings.WorkPlane.Origin.Z-5)>1e-9)
                throw new InvalidOperationException("Actual terminal datum edit did not update sketch/workplane associations.");
            await Run("UNDO");if(!ReferenceEquals(datumBefore,session.Snapshot))throw new InvalidOperationException("Terminal datum edit did not undo atomically.");
            await Run("REDO");if(!ReferenceEquals(datumAfter,session.Snapshot))throw new InvalidOperationException("Terminal datum edit did not redo atomically.");
            model.ClearCommand.Execute(null);await Run("DATUM_BINDINGS 0 2");view.OutputList.SelectedIndex=model.Entries.Count-1;await Idle();view.UpdateLayout();
            bitmap=new RenderTargetBitmap((int)view.ActualWidth,(int)view.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(view);
            encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(output,"command-datums.png")))encoder.Save(file);
            var sources=await Run($"DATUM SOURCES {feature.Id} {session.Snapshot.Features[feature.Id].Result.Revision} 0 50");
            using var sourceParsed=System.Text.Json.JsonDocument.Parse(sources);
            if(sourceParsed.RootElement.GetProperty("items").EnumerateArray().Count(i=>i.TryGetProperty("geometry",out var geometry)&&geometry.GetProperty("kind").GetString()=="Plane")!=6)
                throw new InvalidOperationException("Terminal datum source query did not expose the six original native Box planes.");
            sketch=session.Snapshot.Sketches[sketch.Id];var workplane=session.Snapshot.Settings.WorkPlane;
            await Run($"DATUM UNBIND_SKETCH {sketch.Id} {sketch.Revision:D} {datumId:D} {session.Snapshot.Datums.Planes[datumId].Revision:D}");
            await Run($"DATUM UNBIND_WORKPLANE {datumId:D} {session.Snapshot.Datums.Planes[datumId].Revision:D} {occurrence.Path}");
            if(session.Snapshot.Datums.SketchBindings.Count!=0||session.Snapshot.Datums.WorkPlane is not null||session.Snapshot.Settings.WorkPlane!=workplane||session.Snapshot.Sketches[sketch.Id].Plane!=sketch.Plane)
                throw new InvalidOperationException("Terminal datum detach did not retain the last applied plane.");
            await File.WriteAllTextAsync(Path.Combine(output,"command-datums-result.json"),System.Text.Json.JsonSerializer.Serialize(new
            {passed=true,input="Actual terminal TextBox binding and Execute Automation Invoke",datumId,sketchId=sketch.Id.ToString(),partLocalSketchZ=5,worldWorkplaneZ=5,
                originalNativePlanes=6,undoRedo=true,detachRetainsLastPlane=true,physicalPointer="NOT RUN",providerRequest="NOT RUN"}));
            await session.SaveAsync(services.GetRequiredService<Cadoryx.Kernel.Abstractions.IDocumentStorage>(), Path.Combine(output, "command-parameters.cadoryx"));
            await File.WriteAllTextAsync(Path.Combine(output, "command-parameters-result.json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                passed = true, input = "Actual terminal TextBox binding and WPF Automation Invoke of Execute button; production commands and OCCT kernel",
                nativeVolumeMm3 = after.Features[feature.Id].Result.VolumeMm3,
                documentId = session.Snapshot.Id.ToString(), stateId = session.Snapshot.StateId.ToString(), parameterId,
                undoRedo = true, historyPreviewAndSuppression = true, datumAuthoringAndAssociations=true, selectedTranscriptRows = "Programmatic WPF ListBox selection for visual contrast inspection",
                physicalPointer = "NOT RUN", providerRequest = "NOT RUN"
            }));
        }
        finally
        {
            model.ExecuteCommand.Cancel();
            if (model.ExecuteCommand.ExecutionTask is { } pending) await pending;
            model.Bind(null); document.Detach(); view.DataContext = originalModel;
            await session.DisposeAsync();
            if (assets.Count != 0) throw new InvalidOperationException("Terminal parameter smoke retained native assets.");
        }

        async Task<string> Run(string command)
        {
            var input = Find<TextBox>(view) ?? throw new InvalidOperationException("Actual terminal TextBox is missing.");
            input.SetCurrentValue(TextBox.TextProperty, command);
            input.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            var button = Find<Button>(view, b => ReferenceEquals(b.Command, model.ExecuteCommand)) ??
                throw new InvalidOperationException("Actual terminal Execute button is not bound.");
            if (!button.IsEnabled) throw new InvalidOperationException("Actual terminal Execute button is disabled.");
            var peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
            if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke) throw new InvalidOperationException("Terminal Execute has no Invoke provider.");
            var previous = model.Entries.Count;
            invoke.Invoke(); await Idle();
            await (model.ExecuteCommand.ExecutionTask ?? throw new InvalidOperationException("Terminal did not start execution.")).WaitAsync(TimeSpan.FromSeconds(30));
            await Idle(); // The async command's CanExecute notification reaches the actual Button on the dispatcher.
            var entries = model.Entries.Skip(previous).ToArray();
            if (entries.Any(e => e.Kind == "Error")) throw new InvalidOperationException("Terminal failed: " + string.Join("; ", entries.Select(e => e.Text)));
            return entries.LastOrDefault(e => e.Kind == "Output")?.Text ?? throw new InvalidOperationException("Terminal produced no output.");
        }
    }
    private static T? Find<T>(DependencyObject root,Func<T,bool>? predicate=null) where T : DependencyObject
    {
        if (root is T item&&(predicate is null||predicate(item))) return item;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (Find<T>(VisualTreeHelper.GetChild(root, i),predicate) is { } child) return child;
        return null;
    }
}
