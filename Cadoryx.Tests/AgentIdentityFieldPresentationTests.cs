using System.Text.Json;
using Cadoryx.Agent;
using Cadoryx.AI.Contracts;
using Cadoryx.AI.LmStudio;
using Cadoryx.CommandLine;
using Cadoryx.ViewModels.Toolboxes;
using Xunit;

namespace Cadoryx.Tests;

public sealed class AgentIdentityFieldPresentationTests
{
    [Fact]public void DrawingIdentityRequirementSurvivesActualWirePresentation()
    {
        var tools=new CadAgentToolset(new CadCommandLineService(),null);
        var tool=tools.ToolDefinitions.Single(row=>row.Name=="cad_drawing");
        foreach(var schema in new[]{tool.Parameters,ToolSchemaPresentation.Create(tool.Parameters)})
        {
            var fields=schema.GetProperty("properties");
            foreach(var name in new[]{"documentId","stateId"})
            {
                Assert.Equal("string",fields.GetProperty(name).GetProperty("type").GetString());
                Assert.Equal("uuid",fields.GetProperty(name).GetProperty("format").GetString());
                var explanation=fields.GetProperty(name).GetProperty("description").GetString()!;
                Assert.Contains("REQUIRED except contract",explanation);
                Assert.Contains("cad_status."+name,explanation);
            }
            Assert.Contains("inspect_sources",fields.GetProperty("action").GetProperty("description").GetString()!);
            Assert.Equal(new[]{"action"},schema.GetProperty("required").EnumerateArray().Select(row=>row.GetString()));
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        }
    }
    [Fact]public void NullableWorkspaceFieldsExplainExactActiveIdentityOnTheWire()
    {
        var tools=new CadAgentToolset(new CadCommandLineService(),null);
        var tool=tools.ToolDefinitions.Single(row=>row.Name=="cad_workspace");
        foreach(var schema in new[]{tool.Parameters,ToolSchemaPresentation.Create(tool.Parameters)})
        {
            var expected=schema.GetProperty("properties").GetProperty("expected");
            Assert.Contains("list.expected verbatim",expected.GetProperty("description").GetString()!);
            Assert.Equal(new[]{"workspaceStateId","activeSessionId","documentId","stateId"},
                expected.GetProperty("required").EnumerateArray().Select(row=>row.GetString()));
            foreach(var name in new[]{"activeSessionId","documentId","stateId"})
            {
                var field=expected.GetProperty("properties").GetProperty(name);
                Assert.Equal(new[]{"string","null"},field.GetProperty("type").EnumerateArray().Select(row=>row.GetString()));
                Assert.Contains("Null only if latest list.expected."+name+" is null",field.GetProperty("description").GetString()!);
            }
        }
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void IdentityHintsRetainEveryToolAndOriginal8192OutputSafetyBudget(bool aggressive)
    {
        var tools=new CadAgentToolset(new CadCommandLineService(),null);
        var context=AgentRequestContextBuilder.Build("CAD assistant",[AiChatMessage.User("Use cad_drawing contract(view) and cad_workspace list")],
            tools.SelectTools("Use cad_drawing contract(view) and cad_workspace list",aggressive),8192,aggressive);
        Assert.Equal(12,context.Tools.Count);Assert.Equal(1024,context.MaxOutputTokens);
        Assert.True(context.EstimatedPromptTokens<=8192-1024-(aggressive?1280:640));
    }
}
