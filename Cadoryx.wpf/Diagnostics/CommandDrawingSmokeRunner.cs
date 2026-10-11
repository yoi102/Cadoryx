using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cadoryx.CommandLine;
using Cadoryx.Commands;
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
    private static async Task CheckDrawingAuthors(MainWindow window,IServiceProvider services,string output)
    {
        var view=Find<CommandLineToolboxView>(window)??throw new InvalidOperationException("Actual command view is missing.");var original=view.DataContext;
        var assets=new MemoryAssetStore();var kernel=services.GetRequiredService<IGeometryKernel>();var storage=services.GetRequiredService<IDocumentStorage>();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Terminal engineering drawing"),assets,kernel,services.GetRequiredService<ISessionDispatcher>());
        var messages=new CadMessageLog();var document=new CadDocumentViewModel(session,kernel,messages,storage,fileService:services.GetRequiredService<IDocumentFileService>());
        var model=new CommandLineToolboxViewModel(services.GetRequiredService<ICadCommandLineService>(),services.GetRequiredService<IToolboxIconProvider>(),messages);
        var receipts=new List<JsonElement>();
        try
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(12,10,15,RigidTransform3d.Identity),"Drawing part"));var body=session.Snapshot.Bodies.Values.Single();
            await session.ExecuteAsync(new AddHoleCommand(body.Id,new(body.Geometry,RigidTransform3d.Translate(0,0,15),new(5,6),2,false,6),"Drawing hole"));
            model.Bind(document);view.DataContext=model;await Idle();
            await Run("DRAWING CONTRACT");await Run("DRAWING CONTRACT sheet");
            var template=DrawingSheetTemplate.Create("Manufacturing").Template!;
            var sheetResult=await Draw(new{action="create",type="sheet",author=new{name="Manufacturing",widthMm=297,heightMm=210,unit="Millimeter",title="CADoryx",standard="ISO",template}});
            var sheet=sheetResult.GetProperty("id").GetGuid();receipts.Add(sheetResult);
            var topResult=await Draw(new{action="create",type="view",sheetId=sheet,author=new{name="Top",kind="Top",centerMm=new{x=80,y=60},scale=2}});var top=topResult.GetProperty("id").GetGuid();
            receipts.Add(topResult);
            var section=await Draw(new{action="create",type="view",sheetId=sheet,author=new{name="Section",kind="Section",centerMm=new{x=150,y=60},scale=2,sectionNormal=new{x=0,y=0,z=1},sectionOffsetMm=10}});
            receipts.Add(section);
            receipts.Add(await Draw(new{action="create",type="view",sheetId=sheet,author=new{name="Detail",kind="Detail",parentView=section.GetProperty("id").GetGuid(),centerMm=new{x=220,y=65},scale=3,detailCenter=new{x=5,y=6},detailRadius=3}}));
            body=session.Snapshot.Bodies.Values.Single();var path=session.Snapshot.EnumerateOccurrences().Single().Path;
            var sources=await Draw(new{action="inspect_sources",path=path.ToString(),bodyId=body.Id.ToString(),revision=body.Geometry.Revision.ToString(),limit=50});
            var shaft=sources.GetProperty("items").EnumerateArray().First(s=>s.TryGetProperty("geometry",out var g)&&g.GetString()=="CylinderFace").GetProperty("selection");
            var plane=sources.GetProperty("items").EnumerateArray().First(s=>s.TryGetProperty("geometry",out var g)&&g.GetString()=="PlaneFace").GetProperty("selection");
            receipts.Add(await Draw(new{action="create",type="dimension",sheetId=sheet,author=new{viewId=top,kind="Diameter",first=shaft,textPositionMm=new{x=80,y=35},upperTolerance=.1,lowerTolerance=.1}}));
            receipts.Add(await Draw(new{action="create",type="datum",sheetId=sheet,author=new{viewId=top,name="A",source=plane,positionMm=new{x=105,y=40}}}));
            receipts.Add(await Draw(new{action="create",type="control_frame",sheetId=sheet,author=new{viewId=top,source=plane,positionMm=new{x=125,y=40},characteristic="Flatness",toleranceMm=.02}}));
            receipts.Add(await Draw(new{action="create",type="hole_table",sheetId=sheet,author=new{viewId=top,name="Hole locations",coordinatePath=path.ToString(),frame=new{origin=new{x=0,y=0,z=15},normal=new{x=0,y=0,z=1},u=new{x=1,y=0,z=0}},positionMm=new{x=25,y=90},rows=new[]{new{number=1,source=shaft}}}}));
            receipts.Add(await Draw(new{action="create",type="note",sheetId=sheet,author=new{text="Deburr edges",positionMm=new{x=25,y=120},widthMm=90}}));
            var inspect=await Draw(new{action="inspect",type="dimension",sheetId=sheet});var edit=JsonNode.Parse(inspect.GetProperty("items")[0].GetProperty("edit").GetRawText())!.AsObject();
            edit.Remove("documentId");edit.Remove("stateId");var author=JsonNode.Parse(edit["authorJson"]!.GetValue<string>())!.AsObject();author["upperTolerance"]=.2;edit["authorJson"]=author.ToJsonString();
            receipts.Add(await Run("DRAWING "+edit.ToJsonString()));var after=session.Snapshot;
            await Run("UNDO",false);if(session.Snapshot.DrawingSheets[sheet].Dimensions.Single().UpperTolerance!=.1)throw new InvalidOperationException("Drawing terminal undo failed.");
            await Run("REDO",false);if(!ReferenceEquals(after,session.Snapshot))throw new InvalidOperationException("Drawing terminal redo failed.");
            var savedTemplate=await Draw(new{action="save_template",sheetId=sheet,name="Terminal shop"});receipts.Add(savedTemplate);
            var templatePath=Path.GetFullPath(Path.Combine(output,"command-drawing-template.json"));receipts.Add(await Run("FILE "+JsonSerializer.Serialize(new{action="template_export",id=savedTemplate.GetProperty("id").GetGuid(),path=templatePath})));
            var nativePath=Path.GetFullPath(Path.Combine(output,"command-drawing.cadoryx"));receipts.Add(await Run("FILE "+JsonSerializer.Serialize(new{action="save",path=nativePath})));
            using(var loaded=await storage.LoadAsync(nativePath,assets))
            {
                var restored=loaded.Snapshot.DrawingSheets[sheet];if(restored.Views.Length!=3||restored.Dimensions.Length!=1||restored.HoleTables.Length!=1||restored.ControlFrames.Length!=1)
                    throw new InvalidOperationException("Drawing native re-open lost author content.");
            }
            var pdfPath=Path.GetFullPath(Path.Combine(output,"command-drawing.pdf"));receipts.Add(await Run("FILE "+JsonSerializer.Serialize(new{action="pdf",path=pdfPath})));
            var final=session.Snapshot.DrawingSheets[sheet];
            if(final.Views.Any(v=>v.StaleReason is not null)||Math.Abs(final.HoleTables.Single().Rows.Single().Evidence.DiameterMm-2)>1e-6||Math.Abs(final.Dimensions.Single().Value-2)>1e-6)
                throw new InvalidOperationException("Terminal drawing is stale or has incorrect native dimensions.");
            view.OutputList.SelectedIndex=model.Entries.Count-1;await Idle();view.UpdateLayout();
            var bitmap=new RenderTargetBitmap((int)view.ActualWidth,(int)view.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(view);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(Path.Combine(output,"command-drawing.png")))encoder.Save(stream);
            await File.WriteAllTextAsync(Path.Combine(output,"command-drawing-result.json"),JsonSerializer.Serialize(new{passed=true,
                input="Actual production terminal TextBox binding and Execute Automation Invoke",receipts,views=3,nativeDiameterMm=2,
                undoRedo=true,nativeReopen=true,vectorPdf=true,templateFile=true,physicalPointer="NOT RUN",provider="NOT RUN"}));
        }
        finally{model.Bind(null);view.DataContext=original;document.Detach();await session.DisposeAsync();if(assets.Count!=0)throw new InvalidOperationException("Drawing smoke retained assets.");}
        Task<JsonElement> Draw(object request)=>Run("DRAWING "+JsonSerializer.Serialize(request,new JsonSerializerOptions(JsonSerializerDefaults.Web){Converters={new System.Text.Json.Serialization.JsonStringEnumConverter()}}));
        async Task<JsonElement> Run(string command,bool json=true)
        {
            var input=Find<TextBox>(view)??throw new InvalidOperationException("Terminal input missing.");input.SetCurrentValue(TextBox.TextProperty,command);input.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            var button=Find<Button>(view,b=>ReferenceEquals(b.Command,model.ExecuteCommand))??throw new InvalidOperationException("Terminal Execute missing.");
            var peer=UIElementAutomationPeer.CreatePeerForElement(button)??new ButtonAutomationPeer(button);var invoke=peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider??throw new InvalidOperationException("Terminal Invoke unavailable.");
            var count=model.Entries.Count;invoke.Invoke();await Idle();await (model.ExecuteCommand.ExecutionTask??throw new InvalidOperationException("Terminal command did not start.")).WaitAsync(TimeSpan.FromSeconds(60));await Idle();
            var added=model.Entries.Skip(count).ToArray();if(added.Any(e=>e.Kind=="Error"))throw new InvalidOperationException("Drawing terminal failed: "+string.Join("; ",added.Select(e=>e.Text)));
            if(!json)return default;using var parsed=JsonDocument.Parse(added.LastOrDefault(e=>e.Kind=="Output")?.Text??throw new InvalidOperationException("Drawing receipt missing."));
            if(!parsed.RootElement.GetProperty("success").GetBoolean())throw new InvalidOperationException("Drawing receipt failed.");return parsed.RootElement.Clone();
        }
    }
}
