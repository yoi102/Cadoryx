using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private static async Task CheckModelAuthoring(MainWindow window,IServiceProvider services,string output)
    {
        var view=Find<CommandLineToolboxView>(window)??throw new InvalidOperationException("Command view is missing.");var original=view.DataContext;
        var assets=new MemoryAssetStore();var kernel=services.GetRequiredService<IGeometryKernel>();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Terminal model authors"),assets,kernel,services.GetRequiredService<ISessionDispatcher>());
        var messages=new CadMessageLog();var document=new CadDocumentViewModel(session,kernel,messages);
        var model=new CommandLineToolboxViewModel(services.GetRequiredService<ICadCommandLineService>(),services.GetRequiredService<IToolboxIconProvider>(),messages);
        try
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,RigidTransform3d.Identity),"Base"));var seed=session.Snapshot.Features.Values.Single();
            model.Bind(document);view.DataContext=model;await Idle();
            object Frame(double z)=>new{origin=new[]{0d,0d,z},normal=new[]{0d,0d,1d},u=new[]{1d,0d,0d}};
            object Input(FeatureId id)=>new{featureId=id.ToString(),revision=session.Snapshot.Features[id].Result.Revision.ToString()};
            async Task<JsonElement> Create(object author)=>Parse(await Run("MODEL "+JsonSerializer.Serialize(new{action="create",partId=seed.PartId.ToString(),name="Terminal model",author})));
            var contract=Parse(await Run("MODEL CONTRACT extrude 0 2"));if(!contract.GetProperty("success").GetBoolean())throw new InvalidOperationException("Terminal model contract failed.");
            var created=await Create(new{type="extrude",section=new{profile=new{points=new[]{new{x=0d,y=0d},new{x=4d,y=0d},new{x=4d,y=3d},new{x=0d,y=3d}}},frame=Frame(0)},distance=2});
            var extrude=new FeatureId(created.GetProperty("id").GetGuid());if(Math.Abs(session.Snapshot.Features[extrude].Result.VolumeMm3-24)>1e-6)throw new InvalidOperationException("Terminal extrusion volume differs.");
            var recipes=Parse(await Run("RECIPES 0 2"));var edit=JsonNode.Parse(recipes.GetProperty("items").EnumerateArray().Single(x=>x.GetProperty("id").GetString()==extrude.ToString()).GetProperty("edit").GetRawText())!.AsObject();
            edit.Remove("documentId");edit.Remove("stateId");var author=JsonNode.Parse(edit["authorJson"]!.GetValue<string>())!.AsObject();author["distance"]=5;edit["authorJson"]=author.ToJsonString();
            await Run("MODEL "+edit.ToJsonString());if(Math.Abs(session.Snapshot.Features[extrude].Result.VolumeMm3-60)>1e-6)throw new InvalidOperationException("Terminal recipe edit did not recompute native geometry.");
            var after=session.Snapshot;await Run("UNDO");if(Math.Abs(session.Snapshot.Features[extrude].Result.VolumeMm3-24)>1e-6)throw new InvalidOperationException("Terminal model undo failed.");
            await Run("REDO");if(!ReferenceEquals(after,session.Snapshot))throw new InvalidOperationException("Terminal model redo did not restore the exact snapshot.");
            var hole=await Create(new{type="hole",input=Input(seed.Id),plane=Frame(10),position=new{x=3d,y=3d},diameter=1});var holeId=new FeatureId(hole.GetProperty("id").GetGuid());
            var replay=await Create(new{type="hole_pattern",input=Input(holeId),layout=new{kind="Linear",frame=Frame(0),count=2,step=3,output="Fused"}});var replayId=new FeatureId(replay.GetProperty("id").GetGuid());
            var expected=1000-5*Math.PI;if(Math.Abs(session.Snapshot.Features[replayId].Result.VolumeMm3-expected)>1e-5)throw new InvalidOperationException("Terminal replay copied the base or drilled the wrong positions.");
            var path=session.Snapshot.EnumerateOccurrences().Single(x=>x.DefinitionId==seed.PartId).Path;
            var instances=Parse(await Run("MODEL "+JsonSerializer.Serialize(new{action="set_assembly",path=path.ToString(),name="Terminal instances",author=new{type="assembly_pattern",kind="Linear",frame=Frame(0),count=3,step=12}})));
            if(instances.GetProperty("createdSlotIds").GetArrayLength()!=2)throw new InvalidOperationException("Terminal assembly creation returned wrong output identities.");
            await Run("ASSEMBLY_PATTERNS");session.Snapshot.Validate();await session.SaveAsync(services.GetRequiredService<IDocumentStorage>(),Path.Combine(output,"command-models.cadoryx"));
            view.OutputList.SelectedIndex=Math.Max(0,view.OutputList.Items.Count-1);await Idle();view.UpdateLayout();
            var bitmap=new RenderTargetBitmap((int)view.ActualWidth,(int)view.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(view);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using(var stream=File.Create(Path.Combine(output,"command-models.png")))encoder.Save(stream);
            await File.WriteAllTextAsync(Path.Combine(output,"command-models-result.json"),JsonSerializer.Serialize(new{passed=true,input="Actual terminal TextBox binding and Execute Automation Invoke",
                extrusionFeature=extrude.ToString(),extrusionVolumeMm3=60,undoRedo=true,holeFeature=holeId.ToString(),replayFeature=replayId.ToString(),replayVolumeMm3=session.Snapshot.Features[replayId].Result.VolumeMm3,
                assemblyPatternId=instances.GetProperty("id").GetGuid(),createdInstanceSlots=2,contractPagination=true,physicalPointer="NOT RUN",providerRequest="NOT RUN"}));
        }
        finally
        {
            model.ExecuteCommand.Cancel();if(model.ExecuteCommand.ExecutionTask is {} pending)await pending;model.Bind(null);document.Detach();view.DataContext=original;
            await session.DisposeAsync();if(assets.Count!=0)throw new InvalidOperationException("Terminal model smoke retained native assets.");
        }
        async Task<string> Run(string command)
        {
            var input=Find<TextBox>(view)??throw new InvalidOperationException("Actual terminal TextBox missing.");input.SetCurrentValue(TextBox.TextProperty,command);input.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            var button=Find<Button>(view,b=>ReferenceEquals(b.Command,model.ExecuteCommand))??throw new InvalidOperationException("Actual terminal Execute button missing.");
            if(!button.IsEnabled)throw new InvalidOperationException("Actual terminal Execute button disabled.");var peer=UIElementAutomationPeer.CreatePeerForElement(button)??new ButtonAutomationPeer(button);
            if(peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)throw new InvalidOperationException("Execute has no Invoke provider.");var count=model.Entries.Count;invoke.Invoke();await Idle();
            await (model.ExecuteCommand.ExecutionTask??throw new InvalidOperationException("Terminal execution did not start.")).WaitAsync(TimeSpan.FromSeconds(30));await Idle();var added=model.Entries.Skip(count).ToArray();
            if(added.Any(e=>e.Kind=="Error"))throw new InvalidOperationException("Terminal model command failed: "+string.Join("; ",added.Select(e=>e.Text)));
            return added.LastOrDefault(e=>e.Kind=="Output")?.Text??throw new InvalidOperationException("Terminal model command had no output.");
        }
        static JsonElement Parse(string json){using var parsed=JsonDocument.Parse(json);return parsed.RootElement.Clone();}
    }
}
