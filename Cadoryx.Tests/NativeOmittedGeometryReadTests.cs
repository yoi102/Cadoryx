using System.Text.Json;
using System.Text.Json.Nodes;
using Cadoryx.Agent;
using Cadoryx.AI.Contracts;
using Cadoryx.CommandLine;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Toolboxes;
using Xunit;

namespace Cadoryx.Tests;

[Collection("ModelAgentNativeGates")]
public sealed class NativeOmittedGeometryReadTests
{
    private const string Document="bd7a6f64-e0cd-47be-86c1-6ca3e1110fe2",State="9f1bdab6-f3f9-4522-832e-bd268cbf1432";
    private const string Body="8452f7bb-5fde-4c14-803c-a2d45e6e6c1f",Revision="1ed2f1d0-5f21-45ea-8f15-f8b1925ac150",Path="actual-source-path";

    [Theory][InlineData(6000,0)][InlineData(2400,0)][InlineData(6000,3)][InlineData(2400,3)]
    public async Task ActualOmittedCylinderReadAuthorsDiameterOnlyFromItsFreshNativeSelection(int budget,int offset)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Omitted native discovery"),assets,kernel,new InlineSessionDispatcher());
        var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());var tools=new CadAgentToolset(new CadCommandLineService(),document);
        try
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(12,10,15,RigidTransform3d.Identity),"Seed"));
            var body=Assert.Single(session.Snapshot.Bodies.Values);
            await session.ExecuteAsync(new AddHoleCommand(body.Id,new(body.Geometry,RigidTransform3d.Translate(0,0,15),new(5,6),2,false,6),"Hole"));
            body=Assert.Single(session.Snapshot.Bodies.Values);var before=session.Snapshot;var geometry=body.Geometry;
            var call=new AiToolCall("native-discovery","cad_drawing",JsonSerializer.Serialize(new{action="inspect_sources",
                documentId=before.Id.ToString(),stateId=before.StateId.ToString(),path=Assert.Single(before.EnumerateOccurrences()).Path.ToString(),
                bodyId=body.Id.ToString(),revision=geometry.Revision.ToString(),offset,limit=50}));
            string raw=await tools.ExecuteAsync(call,default);using var full=JsonDocument.Parse(raw);
            Assert.True(full.RootElement.GetProperty("success").GetBoolean());
            Assert.True(AgentToolResultCompactor.TryCompactNativeSelectionPage(raw,budget,out var compacted,call));
            Assert.InRange(compacted.Length,1,budget);using var parsed=JsonDocument.Parse(compacted);var page=parsed.RootElement;
            Assert.DoesNotContain(page.GetProperty("items").EnumerateArray(), IsCylinder);
            var context=page.GetProperty("contextPage");
            var hint=Assert.Single(context.GetProperty("omittedGeometryReads").EnumerateArray(),h=>h.GetProperty("geometry").GetString()=="CylinderFace");
            int position=hint.GetProperty("offset").GetInt32()-offset;
            Assert.True(position>=page.GetProperty("items").GetArrayLength());
            var expected=full.RootElement.GetProperty("items")[position];Assert.True(IsCylinder(expected));
            Assert.False(hint.TryGetProperty("selection",out _));Assert.Equal(1,hint.GetProperty("limit").GetInt32());
            var read=JsonNode.Parse(context.GetProperty("nextRequest").GetRawText())!.AsObject();
            read["offset"]=hint.GetProperty("offset").GetInt32();read["limit"]=hint.GetProperty("limit").GetInt32();
            using var fresh=JsonDocument.Parse(await tools.ExecuteAsync(new("fresh-cylinder","cad_drawing",read.ToJsonString()),default));
            Assert.True(fresh.RootElement.GetProperty("success").GetBoolean());
            var actual=Assert.Single(fresh.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(expected.GetRawText(),actual.GetRawText());Assert.Same(before,session.Snapshot);
            Assert.Equal(call.ArgumentsJson,JsonSerializer.Serialize(new{action="inspect_sources",documentId=before.Id.ToString(),stateId=before.StateId.ToString(),
                path=Assert.Single(before.EnumerateOccurrences()).Path.ToString(),bodyId=body.Id.ToString(),revision=geometry.Revision.ToString(),offset,limit=50}));
            var sheetReply=await Author(new{action="create",type="sheet",authorJson="{\"name\":\"Discovery sheet\",\"widthMm\":297,\"heightMm\":210,\"unit\":\"Millimeter\",\"title\":\"Discovery\",\"standard\":\"ISO\"}"});
            var sheet=sheetReply.GetProperty("id").GetGuid();
            var viewReply=await Author(new{action="create",type="view",sheetId=sheet,authorJson="{\"name\":\"Top\",\"kind\":\"Top\",\"centerMm\":{\"x\":80,\"y\":60},\"scale\":2}"});
            var view=viewReply.GetProperty("id").GetGuid();var beforeDimension=session.Snapshot;
            await Author(new{action="create",type="dimension",sheetId=sheet,authorJson=JsonSerializer.Serialize(new{viewId=view,kind="Diameter",
                first=actual.GetProperty("selection"),textPositionMm=new{x=80,y=35},upperTolerance=.1,lowerTolerance=.1})});
            var dimension=Assert.Single(session.Snapshot.DrawingSheets[sheet].Dimensions);Assert.Equal(2,dimension.Value,6);
            Assert.Equal(geometry,Assert.Single(session.Snapshot.Bodies.Values).Geometry);
            var afterDimension=session.Snapshot;await session.UndoAsync();Assert.Same(beforeDimension,session.Snapshot);
            await session.RedoAsync();Assert.Same(afterDimension,session.Snapshot);
            async Task<JsonElement> Author(object input)
            {
                // A deterministic caller supplies the exact current state. The
                // feedback code never repairs or executes a rejected mutation.
                var args=JsonSerializer.SerializeToNode(input)!.AsObject();var current=session.Snapshot;
                args["documentId"]=current.Id.ToString();args["stateId"]=current.StateId.ToString();
                using var result=JsonDocument.Parse(await tools.ExecuteAsync(new("author","cad_drawing",args.ToJsonString()),default));
                Assert.True(result.RootElement.GetProperty("success").GetBoolean(),result.RootElement.GetRawText());return result.RootElement.Clone();
            }
        }
        finally{document.Detach();await session.DisposeAsync();Assert.Equal(0,assets.Count);}
    }

    [Theory][InlineData(0)][InlineData(37)][InlineData(99999)]
    public void SyntheticDiscoveryUsesAbsoluteResultPositionsAndNeverNativeTopologyIndices(int offset)
    {
        var raw=Receipt(offset);var call=Query(offset);
        Assert.True(AgentToolResultCompactor.TryCompactNativeSelectionPage(raw,2400,out var text,call));
        Assert.InRange(text.Length,1,2400);using var full=JsonDocument.Parse(raw);using var page=JsonDocument.Parse(text);
        var context=page.RootElement.GetProperty("contextPage");
        if(offset==99999){Assert.False(context.TryGetProperty("omittedGeometryReads",out _));return;}
        var cylinder=Assert.Single(context.GetProperty("omittedGeometryReads").EnumerateArray(),h=>h.GetProperty("geometry").GetString()=="CylinderFace");
        Assert.Equal(offset+38,cylinder.GetProperty("offset").GetInt32());Assert.Equal(1,cylinder.GetProperty("limit").GetInt32());
        Assert.NotEqual(full.RootElement.GetProperty("items")[38].GetProperty("index").GetInt32(),cylinder.GetProperty("offset").GetInt32());
        int retained=page.RootElement.GetProperty("items").GetArrayLength();
        for(int i=0;i<retained;i++)Assert.Equal(full.RootElement.GetProperty("items")[i].GetRawText(),page.RootElement.GetProperty("items")[i].GetRawText());
        Assert.Equal(offset+retained,context.GetProperty("nextOffset").GetInt32());Assert.False(cylinder.TryGetProperty("selection",out _));
    }

    [Theory][InlineData(2400,1100)][InlineData(6000,4650)]
    public void OptionalDiscoveryMetadataCannotDisplaceEveryCompleteOriginalItem(int budget,int diagnosticLength)
    {
        var raw=Receipt(0,diagnosticLength,allGeometryKinds:true);
        Assert.True(AgentToolResultCompactor.TryCompactNativeSelectionPage(raw,budget,out var text,Query()));
        Assert.InRange(text.Length,1,budget);using var full=JsonDocument.Parse(raw);using var page=JsonDocument.Parse(text);
        int count=page.RootElement.GetProperty("items").GetArrayLength();Assert.True(count>0);
        for(int i=0;i<count;i++)Assert.Equal(full.RootElement.GetProperty("items")[i].GetRawText(),page.RootElement.GetProperty("items")[i].GetRawText());
        Assert.False(page.RootElement.GetProperty("contextPage").TryGetProperty("omittedGeometryReads",out _));
    }

    [Theory][InlineData("unknown")][InlineData("long")][InlineData("numeric")]
    [InlineData("kind-mismatch")][InlineData("kind-missing")][InlineData("kind-numeric")]
    public void UnknownOrIncompatibleGeometryDescriptorsRemainDataWithoutReadHints(string fault)
    {
        var value=JsonNode.Parse(Receipt())!.AsObject();
        foreach(var item in value["items"]!.AsArray())
        {
            var row=item!.AsObject();
            switch(fault)
            {
                case "unknown":row["geometry"]="UnknownGeometry";break;
                case "long":row["geometry"]=new string('x',80);break;
                case "numeric":row["geometry"]=38;break;
                case "kind-mismatch":row["kind"]="Edge";break;
                case "kind-missing":row.Remove("kind");break;
                case "kind-numeric":row["kind"]=2;break;
            }
        }
        var raw=value.ToJsonString();Assert.True(AgentToolResultCompactor.TryCompactNativeSelectionPage(raw,2400,out var text,Query()));
        using var page=JsonDocument.Parse(text);Assert.False(page.RootElement.GetProperty("contextPage").TryGetProperty("omittedGeometryReads",out _));
        using var original=JsonDocument.Parse(raw);int count=page.RootElement.GetProperty("items").GetArrayLength();Assert.True(count>0);
        for(int i=0;i<count;i++)Assert.Equal(original.RootElement.GetProperty("items")[i].GetRawText(),page.RootElement.GetProperty("items")[i].GetRawText());
    }

    private static bool IsCylinder(JsonElement item)=>item.TryGetProperty("geometry",out var value)&&value.ValueKind==JsonValueKind.String&&value.GetString()=="CylinderFace";
    private static AiToolCall Query(int offset=0)=>new("discovery","cad_drawing",JsonSerializer.Serialize(new{action="inspect_sources",
        documentId=Document,stateId=State,path=Path,bodyId=Body,revision=Revision,offset,limit=50}));
    private static string Receipt(int offset=0,int diagnosticLength=200,bool allGeometryKinds=false)
    {
        string[] kinds=["GeneralFace","PlaneFace","CylinderFace","GeneralEdge","LineEdge","CircleEdge"];
        return JsonSerializer.Serialize(new{success=true,action="inspect_sources",documentId=Document,stateId=State,offset,limit=50,total=offset+45,hasMore=false,
            items=Enumerable.Range(0,45).Select(i=>
            {
                string geometry=allGeometryKinds?kinds[i%kinds.Length]:i==38?"CylinderFace":"PlaneFace";
                return new{index=3000+i,kind=geometry.EndsWith("Face",StringComparison.Ordinal)?"Face":"Edge",geometry,
                    selection=new{path=Path,bodyId=Body,revision=Revision,asset="actual-asset",fingerprint="actual-fingerprint",index=3000+i},
                    diagnostic=new string('x',diagnosticLength)};
            })});
    }
}
