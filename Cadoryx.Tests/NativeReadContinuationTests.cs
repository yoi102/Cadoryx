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
public sealed class NativeReadContinuationTests
{
    private const string Document="bd7a6f64-e0cd-47be-86c1-6ca3e1110fe2",State="9f1bdab6-f3f9-4522-832e-bd268cbf1432";
    private const string Body="8452f7bb-5fde-4c14-803c-a2d45e6e6c1f",Revision="1ed2f1d0-5f21-45ea-8f15-f8b1925ac150",Path="actual-source-path";

    [Theory][InlineData(6000)][InlineData(2400)]
    public void AContinuationCopiesTheActualQueryAndAdvancesOnlyByCompleteReturnedItems(int budget)
    {
        var raw=Receipt();var call=Call();
        Assert.True(AgentToolResultCompactor.TryCompactNativeSelectionPage(raw,budget,out var text,call));
        Assert.InRange(text.Length,1,budget);using var page=JsonDocument.Parse(text);var root=page.RootElement;
        var context=root.GetProperty("contextPage");var query=context.GetProperty("nextRequest");
        var count=root.GetProperty("items").GetArrayLength();Assert.InRange(count,1,44);
        Assert.Equal(count,query.GetProperty("offset").GetInt32());Assert.Equal(count,context.GetProperty("nextOffset").GetInt32());
        Assert.Equal(Math.Min(50,count),query.GetProperty("limit").GetInt32());
        Assert.Equal(Document,query.GetProperty("documentId").GetString());Assert.Equal(State,query.GetProperty("stateId").GetString());
        Assert.Equal(Body,query.GetProperty("bodyId").GetString());Assert.Equal(Revision,query.GetProperty("revision").GetString());Assert.Equal(Path,query.GetProperty("path").GetString());
        Assert.Equal("inspect_sources",query.GetProperty("action").GetString());Assert.Equal("cad_drawing",context.GetProperty("toolName").GetString());
        Assert.Contains("Not executed",context.GetProperty("requestNote").GetString());Assert.False(root.GetProperty("hasMore").GetBoolean());
        using var original=JsonDocument.Parse(raw);for(int i=0;i<count;i++)Assert.Equal(original.RootElement.GetProperty("items")[i].GetRawText(),root.GetProperty("items")[i].GetRawText());
        Assert.Equal(Call().ArgumentsJson,call.ArgumentsJson);
    }

    [Theory]
    [InlineData("no-call")][InlineData("tool")][InlineData("document")][InlineData("state")]
    [InlineData("path")][InlineData("body")][InlineData("revision")][InlineData("offset")]
    [InlineData("limit")][InlineData("unknown")][InlineData("duplicate")][InlineData("malformed")]
    [InlineData("numeric-offset")]
    public void MissingAmbiguousOrMismatchedQueriesKeepTheOriginalPageWithoutAnInventedRequest(string fault)
    {
        var call=Call();var args=JsonNode.Parse(call.ArgumentsJson)!.AsObject();
        switch(fault)
        {
            case "document":args["documentId"]=Guid.NewGuid().ToString();break;
            case "state":args["stateId"]=Guid.NewGuid().ToString();break;
            case "path":args["path"]="different-path";break;
            case "body":args["bodyId"]=Guid.NewGuid().ToString();break;
            case "revision":args["revision"]=Guid.NewGuid().ToString();break;
            case "offset":args["offset"]=1;break;
            case "limit":args["limit"]=49;break;
            case "unknown":args["force"]=true;break;
            case "numeric-offset":args["offset"]="0";break;
        }
        var json=args.ToJsonString();if(fault=="duplicate")json=json[..^1]+",\"bodyId\":\""+Body+"\"}";if(fault=="malformed")json=json[..^1];
        call=call with{Name=fault=="tool"?"cad_model":call.Name,ArgumentsJson=json};
        Assert.True(AgentToolResultCompactor.TryCompactNativeSelectionPage(Receipt(),2400,out var output,fault=="no-call"?null:call));
        using var parsed=JsonDocument.Parse(output);Assert.False(parsed.RootElement.GetProperty("contextPage").TryGetProperty("nextRequest",out _));
        Assert.True(AgentToolResultCompactor.TryCompactNativeSelectionPage(Receipt(),2400,out var original));Assert.Equal(original,output);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void ContextBuilderPairsOnlyOneActualCallAndKeepsTheOriginalHistory(bool duplicate)
    {
        var call=Call();var receipt=Receipt();var history=new List<AiChatMessage>{AiChatMessage.User("Inspect native pages"),AiChatMessage.Assistant(null,[call]),AiChatMessage.Tool(call.Id,receipt)};
        if(duplicate){history.Add(AiChatMessage.Assistant(null,[call]));history.Add(AiChatMessage.Tool(call.Id,receipt));}
        var original=JsonSerializer.Serialize(history);var context=AgentRequestContextBuilder.Build("CAD assistant",history,[],8192);
        var native=context.Messages.First(m=>m.Role==AiChatRole.Tool);using var page=JsonDocument.Parse(native.Content!);
        Assert.Equal(!duplicate,page.RootElement.GetProperty("contextPage").TryGetProperty("nextRequest",out _));
        Assert.Equal(original,JsonSerializer.Serialize(history));Assert.True(context.EstimatedPromptTokens<=8192-context.MaxOutputTokens-640);
    }

    [Theory][InlineData(6000)][InlineData(2400)]
    public async Task RealContinuationRequestsRetrieveTheActualCylinderWithoutEditingOrChangingIdentity(int budget)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Native continuation"),assets,kernel,new InlineSessionDispatcher());
        var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());var tools=new CadAgentToolset(new CadCommandLineService(),document);
        try
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(12,10,15,RigidTransform3d.Identity),"Seed"));var body=session.Snapshot.Bodies.Values.Single();
            await session.ExecuteAsync(new AddHoleCommand(body.Id,new(body.Geometry,RigidTransform3d.Translate(0,0,15),new(5,6),2,false,6),"Hole"));
            body=session.Snapshot.Bodies.Values.Single();var before=session.Snapshot;
            var query=new AiToolCall("native-0","cad_drawing",JsonSerializer.Serialize(new{action="inspect_sources",documentId=before.Id.ToString(),stateId=before.StateId.ToString(),
                path=before.EnumerateOccurrences().Single().Path.ToString(),bodyId=body.Id.ToString(),revision=body.Geometry.Revision.ToString(),offset=0,limit=50}));
            var items=new List<JsonElement>();var originalItems=new List<JsonElement>();var reached=false;
            for(int round=0;round<16;round++)
            {
                var raw=await tools.ExecuteAsync(query,default);using var result=JsonDocument.Parse(raw);Assert.True(result.RootElement.GetProperty("success").GetBoolean());
                if(round==0)originalItems.AddRange(result.RootElement.GetProperty("items").EnumerateArray().Select(i=>i.Clone()));
                AgentToolResultCompactor.TryCompactNativeSelectionPage(raw,budget,out var output,query);Assert.InRange(output.Length,1,budget);
                using var page=JsonDocument.Parse(output);var root=page.RootElement;items.AddRange(root.GetProperty("items").EnumerateArray().Select(i=>i.Clone()));
                reached|=root.GetProperty("items").EnumerateArray().Any(i=>i.TryGetProperty("geometry",out var geometry)&&geometry.GetString()=="CylinderFace");
                if(root.TryGetProperty("contextPage",out var continuation))
                {
                    var next=continuation.GetProperty("nextRequest");Assert.True(next.GetProperty("offset").GetInt32()>result.RootElement.GetProperty("offset").GetInt32());
                    query=new("native-"+(round+1),continuation.GetProperty("toolName").GetString()!,next.GetRawText());
                }
                else if(root.GetProperty("hasMore").GetBoolean())
                {
                    // Complete native pages use their real original pagination.
                    var next=JsonNode.Parse(query.ArgumentsJson)!.AsObject();next["offset"]=root.GetProperty("offset").GetInt32()+root.GetProperty("items").GetArrayLength();
                    query=query with{Id="native-"+(round+1),ArgumentsJson=next.ToJsonString()};
                }
                else break;
                Assert.Same(before,session.Snapshot);
            }
            Assert.True(reached);Assert.Equal(originalItems.Count,items.Count);for(int i=0;i<items.Count;i++)Assert.Equal(originalItems[i].GetRawText(),items[i].GetRawText());
            Assert.Same(before,session.Snapshot);Assert.Equal(body.Geometry,session.Snapshot.Bodies.Values.Single().Geometry);
        }
        finally{document.Detach();await session.DisposeAsync();Assert.Equal(0,assets.Count);}
    }

    private static AiToolCall Call()=>new("native-query","cad_drawing",JsonSerializer.Serialize(new{action="inspect_sources",documentId=Document,stateId=State,path=Path,bodyId=Body,revision=Revision,offset=0,limit=50}));
    private static string Receipt()=>JsonSerializer.Serialize(new{success=true,action="inspect_sources",documentId=Document,stateId=State,offset=0,limit=50,total=45,hasMore=false,
        items=Enumerable.Range(0,45).Select(i=>new{index=3000+i,kind="Face",geometry=i==38?"CylinderFace":"PlaneFace",
            selection=new{path=Path,bodyId=Body,revision=Revision,asset="actual-asset",fingerprint="actual-fingerprint",index=3000+i},diagnostic=new string('x',200)})});
}
