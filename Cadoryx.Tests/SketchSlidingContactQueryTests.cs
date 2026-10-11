using System.Text.Json;
using Cadoryx.Agent;
using Cadoryx.AI.Contracts;
using Cadoryx.AI.LmStudio;
using Cadoryx.Commands;
using Cadoryx.CommandLine;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Toolboxes;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchSlidingContactQueryTests
{
    [Fact]
    public async Task AgentAndTerminalReadCurrentLocalStateWithCorrectSketchPaginationAndNoHistoryChange()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        foreach(int index in new[]{1,2,3})
        {
            var sketch=SketchSlidingContactSolveTests.Pair("Ellipse",false,FiniteContactContinuity.Curvature,FiniteContactParameterMode.SolveBoth) with
                {Id=new(Guid.Parse($"00000000-0000-0000-0000-{index:000000000000}")),PartId=part};
            await session.ExecuteAsync(new UpsertSketchCommand(sketch,new ManagedSketchConstraintSolver()));
        }
        var before=session.Snapshot;long generation=session.Generation;
        var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            var service=new CadCommandLineService();var tools=new CadAgentToolset(service,document);
            using(var page=JsonDocument.Parse(await tools.ExecuteAsync(new("read","cad_inspect","{\"area\":\"sketch_geometry\",\"offset\":1,\"limit\":1}"),default)))
            {
                var root=page.RootElement;Assert.True(root.GetProperty("success").GetBoolean());
                Assert.Equal(3,root.GetProperty("total").GetInt32());Assert.True(root.GetProperty("hasMore").GetBoolean());
                var item=Assert.Single(root.GetProperty("items").EnumerateArray());
                Assert.Equal("00000000-0000-0000-0000-000000000002",item.GetProperty("id").GetString());
                var analysis=item.GetProperty("constraintState").GetProperty("analysis");
                Assert.Equal("Evaluated",analysis.GetProperty("status").GetString());Assert.True(analysis.GetProperty("isSatisfied").GetBoolean());
                Assert.Equal(0,analysis.GetProperty("degreesOfFreedom").GetInt32());
                Assert.Equal(analysis.GetProperty("variableCount").GetInt32(),analysis.GetProperty("rank").GetInt32());
            }
            using(var empty=JsonDocument.Parse(await tools.ExecuteAsync(new("read","cad_inspect","{\"area\":\"sketch_geometry\",\"offset\":3,\"limit\":1}"),default)))
            {
                Assert.Equal(3,empty.RootElement.GetProperty("total").GetInt32());
                Assert.False(empty.RootElement.GetProperty("hasMore").GetBoolean());Assert.Empty(empty.RootElement.GetProperty("items").EnumerateArray());
            }
            var terminal=await service.ExecuteAsync("SKETCH_GEOMETRY",new CadCommandContext(document));Assert.True(terminal.Success,terminal.Message);
            using var terminalJson=JsonDocument.Parse(terminal.Message);
            Assert.Equal(3,terminalJson.RootElement.GetProperty("items").GetArrayLength());
            Assert.All(terminalJson.RootElement.GetProperty("items").EnumerateArray(),item=>Assert.Equal(0,item.GetProperty("constraintState").GetProperty("iterations").GetInt32()));
            Assert.Same(before,session.Snapshot);Assert.Equal(generation,session.Generation);Assert.Equal(0,assets.Count);
        }
        finally{document.Detach();}
    }

    [Theory]
    [InlineData(false,"Set sketch finite contact using automatic parameter seeds and bounds, then inspect local freedom")]
    [InlineData(true,"Set sketch finite contact using automatic parameter seeds and bounds, then inspect local freedom")]
    [InlineData(false,"设置草图接触参数自动求解、种子和区间，然后查询局部自由度")]
    [InlineData(true,"设置草图接触参数自动求解、种子和区间，然后查询局部自由度")]
    public void SlidingAuthorsRemainInProductionAndLmStudioSchemasWithinUnchangedContextBudgets(bool aggressive,string request)
    {
        var tools=new CadAgentToolset(new CadCommandLineService(),null);
        var context=AgentRequestContextBuilder.Build("CAD assistant",[AiChatMessage.User(request)],tools.SelectTools(request,aggressive),8192,aggressive);
        Assert.Equal(12,context.Tools.Count);
        Assert.True(context.EstimatedPromptTokens<=8192-context.MaxOutputTokens-(aggressive?1280:640));
        var schema=context.Tools.Single(t=>t.Name=="cad_edit").Parameters;
        foreach(var representation in new[]{schema,ToolSchemaPresentation.Create(schema)})
        {
            var properties=representation.GetProperty("properties");
            Assert.Equal(new[]{"Fixed","SolveA","SolveB","SolveBoth"},properties.GetProperty("parameterMode").GetProperty("enum").EnumerateArray().Select(v=>v.GetString()));
            foreach(string field in new[]{"seedA","lowerA","upperA","seedB","lowerB","upperB"})
                Assert.Equal("number",properties.GetProperty(field).GetProperty("type").GetString());
            Assert.False(representation.GetProperty("additionalProperties").GetBoolean());
        }
        var longContext=AgentRequestContextBuilder.Build("CAD assistant",[AiChatMessage.User(new string('x',20000)),AiChatMessage.User(request)],tools.SelectTools(request,aggressive),8192,aggressive);
        Assert.Contains(longContext.Tools,t=>t.Name=="cad_edit");Assert.Contains(longContext.Tools,t=>t.Name=="cad_inspect");
        Assert.True(longContext.EstimatedPromptTokens<=8192-longContext.MaxOutputTokens-(aggressive?1280:640));
    }
}
