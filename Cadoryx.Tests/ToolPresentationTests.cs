using System.Text.Json;
using Cadoryx.AI.LmStudio;
using Cadoryx.CommandLine;
using Cadoryx.ViewModels.Toolboxes;
using Xunit;

namespace Cadoryx.Tests;

public sealed class ToolPresentationTests
{
    [Fact]
    public void EveryProductionActionIsVisibleWithExpandedNativeIdentityFields()
    {
        var tools=new CadAgentToolset(new CadCommandLineService(),null);
        foreach(var tool in tools.ToolDefinitions)
        {
            var wire=ToolSchemaPresentation.Create(tool.Parameters);Assert.Equal(JsonValueKind.Object,wire.GetProperty("properties").ValueKind);
            Assert.DoesNotContain("$ref",wire.GetRawText());Assert.DoesNotContain("oneOf",wire.GetRawText());
            if(tool.Name is "cad_model" or "cad_datum" or "cad_parameter" or "cad_history" or "cad_file")
            {Assert.Equal("string",wire.GetProperty("properties").GetProperty("action").GetProperty("type").GetString());Assert.NotEmpty(wire.GetProperty("properties").GetProperty("action").GetProperty("enum").EnumerateArray());}
        }
        var model=ToolSchemaPresentation.Create(tools.ToolDefinitions.Single(t=>t.Name=="cad_model").Parameters);
        Assert.Equal(32768,model.GetProperty("properties").GetProperty("authorJson").GetProperty("maxLength").GetDouble());
        Assert.Equal("uuid",model.GetProperty("properties").GetProperty("documentId").GetProperty("format").GetString());
        var datum=ToolSchemaPresentation.Create(tools.ToolDefinitions.Single(t=>t.Name=="cad_datum").Parameters);
        var selection=datum.GetProperty("properties").GetProperty("author").GetProperty("properties").GetProperty("selection");
        Assert.Contains(selection.GetProperty("required").EnumerateArray(),v=>v.GetString()=="fingerprint");
        Assert.Equal(100000,selection.GetProperty("properties").GetProperty("index").GetProperty("maximum").GetDouble());
    }
    [Theory]
    [InlineData("{\"$ref\":\"https://example.com/schema\"}")]
    [InlineData("{\"$ref\":\"#/$defs/missing\"}")]
    [InlineData("{\"$defs\":{\"self\":{\"$ref\":\"#/$defs/self\"}},\"$ref\":\"#/$defs/self\"}")]
    public void ForeignMissingAndRecursiveReferencesAreRejected(string input)
    {using var schema=JsonDocument.Parse(input);Assert.Throws<ArgumentException>(()=>ToolSchemaPresentation.Create(schema.RootElement));}
    [Fact]
    public void OptionalAndNullableCursorPropertiesRemainVisible()
    {
        var tools=new CadAgentToolset(new CadCommandLineService(),null);var history=ToolSchemaPresentation.Create(tools.ToolDefinitions.Single(t=>t.Name=="cad_history").Parameters);
        var cursor=history.GetProperty("properties").GetProperty("cursor");Assert.Contains(cursor.GetProperty("type").EnumerateArray(),t=>t.GetString()=="null");
        Assert.Contains(cursor.GetProperty("type").EnumerateArray(),t=>t.GetString()=="object");Assert.True(cursor.GetProperty("properties").TryGetProperty("position",out _));
    }
}
