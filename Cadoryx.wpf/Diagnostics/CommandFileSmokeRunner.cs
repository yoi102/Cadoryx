using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cadoryx.Commands;
using Cadoryx.CommandLine;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Toolboxes;
using Cadoryx.wpf.Views.Toolboxes;
using Microsoft.Extensions.DependencyInjection;

namespace Cadoryx.wpf.Diagnostics;

internal static partial class CommandAndAgentSmokeRunner
{
    private static async Task CheckFileOutputs(MainWindow window,IServiceProvider services,string output)
    {
        var view=Find<CommandLineToolboxView>(window)??throw new InvalidOperationException("Actual command view is missing.");var original=view.DataContext;
        var assets=new MemoryAssetStore();var kernel=services.GetRequiredService<IGeometryKernel>();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Terminal file outputs"),assets,kernel,services.GetRequiredService<ISessionDispatcher>());
        var messages=new CadMessageLog();var document=new CadDocumentViewModel(session,kernel,messages,fileService:services.GetRequiredService<IDocumentFileService>());
        var model=new CommandLineToolboxViewModel(services.GetRequiredService<ICadCommandLineService>(),services.GetRequiredService<IToolboxIconProvider>(),messages);
        var receipts=new List<JsonElement>();
        try
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(4,3,2,RigidTransform3d.Identity),"File Box"));var before=session.Snapshot;
            model.Bind(document);view.DataContext=model;await Idle();await Run("FILE CONTRACT");
            foreach(var (action,name,options) in new (string,string,object?)[]{("save","command-files.cadoryx",null),
                ("export","command-files.step",null),("report","command-files.csv",new{output="BomCsv"}),
                ("report","command-files.html",new{output="ReviewHtml"}),("report","command-files.zip",new{output="Package"}),
                ("report","command-files-audit.zip",new{output="ExchangeAudit"})})
            {
                var request=new Dictionary<string,object>{["action"]=action,["path"]=Path.GetFullPath(Path.Combine(output,name))};
                if(options is not null)request["options"]=options;var result=await Run("FILE "+JsonSerializer.Serialize(request));
                if(!result.GetProperty("success").GetBoolean()||!result.GetProperty("published").GetBoolean())throw new InvalidOperationException("Terminal output was not published.");
                var receipt=result.GetProperty("receipt");var path=receipt.GetProperty("path").GetString()!;
                if(receipt.GetProperty("stateId").GetString()!=before.StateId.ToString()||receipt.GetProperty("documentId").GetString()!=before.Id.ToString()||
                    receipt.GetProperty("bytes").GetInt64()!=new FileInfo(path).Length||
                    receipt.GetProperty("sha256").GetString()!=Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))))
                    throw new InvalidOperationException("Terminal receipt does not describe the actual file.");
                receipts.Add(receipt.Clone());
            }
            if(!ReferenceEquals(before,session.Snapshot)||session.IsDirty)throw new InvalidOperationException("File operations changed author state or failed to mark the captured save.");
            await Run("UNDO",json:false);if(session.Snapshot.Bodies.Count!=0)throw new InvalidOperationException("File outputs added undo history.");
            await Run("REDO",json:false);if(!ReferenceEquals(before,session.Snapshot))throw new InvalidOperationException("File output redo did not retain author snapshot.");
            model.ClearCommand.Execute(null);await Run("FILE "+JsonSerializer.Serialize(new{action="report",path=Path.GetFullPath(Path.Combine(output,"command-files-summary.csv")),options=new{output="BomCsv"}}));
            view.OutputList.SelectedIndex=model.Entries.Count-1;await Idle();view.UpdateLayout();
            var bitmap=new RenderTargetBitmap((int)view.ActualWidth,(int)view.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(view);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using(var stream=File.Create(Path.Combine(output,"command-files.png")))encoder.Save(stream);
            await File.WriteAllTextAsync(Path.Combine(output,"command-files-result.json"),JsonSerializer.Serialize(new{passed=true,input="Actual terminal TextBox binding and Execute Automation Invoke",receipts,
                undoRedo=true,nativeVolumeMm3=24,sourceDocumentId=before.Id.ToString(),sourceStateId=before.StateId.ToString(),physicalPointer="NOT RUN",providerRequest="NOT RUN"}));
        }
        finally
        {
            model.ExecuteCommand.Cancel();if(model.ExecuteCommand.ExecutionTask is {} task)await task;model.Bind(null);document.Detach();view.DataContext=original;
            await session.DisposeAsync();if(assets.Count!=0)throw new InvalidOperationException("Terminal file smoke retained assets.");
        }
        async Task<JsonElement> Run(string command,bool json=true)
        {
            var input=Find<TextBox>(view)??throw new InvalidOperationException("Actual terminal input is missing.");input.SetCurrentValue(TextBox.TextProperty,command);input.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            var button=Find<Button>(view,b=>ReferenceEquals(b.Command,model.ExecuteCommand))??throw new InvalidOperationException("Actual terminal Execute button is missing.");
            if(!button.IsEnabled)throw new InvalidOperationException("Actual terminal Execute button is disabled.");var peer=UIElementAutomationPeer.CreatePeerForElement(button)??new ButtonAutomationPeer(button);
            var invoke=peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider??throw new InvalidOperationException("Execute has no Invoke provider.");var count=model.Entries.Count;
            invoke.Invoke();await Idle();await (model.ExecuteCommand.ExecutionTask??throw new InvalidOperationException("Terminal execution did not start.")).WaitAsync(TimeSpan.FromSeconds(60));await Idle();
            var added=model.Entries.Skip(count).ToArray();if(added.Any(e=>e.Kind=="Error"))throw new InvalidOperationException("Terminal file command failed: "+string.Join("; ",added.Select(e=>e.Text)));
            if(!json)return default;using var parsed=JsonDocument.Parse(added.LastOrDefault(e=>e.Kind=="Output")?.Text??throw new InvalidOperationException("Terminal file command has no output."));return parsed.RootElement.Clone();
        }
    }
}
