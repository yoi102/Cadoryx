using System.Net;
using System.Text.Json;
using Cadoryx.Agent;
using Cadoryx.AI.Contracts;
using Cadoryx.AI.LmStudio;
using Cadoryx.CommandLine;
using Cadoryx.ViewModels.Toolboxes;
using Xunit;

namespace Cadoryx.Tests;

public sealed class MaterialAgentSelectionTests
{
    [Theory]
    [InlineData("cad_material contract", false)]
    [InlineData("cad_material contract", true)]
    [InlineData("修改材质的正反面颜色和粗糙度", false)]
    [InlineData("修改材质的正反面颜色和粗糙度", true)]
    [InlineData("导入纹理并检查映射", false)]
    [InlineData("导入纹理并检查映射", true)]
    [InlineData("Edit material appearance and metallic roughness", false)]
    [InlineData("Edit material appearance and metallic roughness", true)]
    [InlineData("Inspect texture mapping", false)]
    [InlineData("Inspect texture mapping", true)]
    [InlineData("マテリアルとテクスチャを編集", false)]
    [InlineData("マテリアルとテクスチャを編集", true)]
    public void MaterialRequestIsAdvertisedInBothProviderSchemasWithoutChangingTheDefaultTwelveOrBudgets(string prompt, bool aggressive)
    {
        var tools = new CadAgentToolset(new CadCommandLineService(), null);
        Assert.Equal(12, tools.ToolDefinitions.Count); Assert.DoesNotContain(tools.ToolDefinitions, t => t.Name == "cad_material");
        var defaultTools = tools.SelectTools("Create an engineering drawing", aggressive);
        Assert.Equal(12, defaultTools.Count); Assert.DoesNotContain(defaultTools, t => t.Name == "cad_material");
        var selected = tools.SelectTools(prompt, aggressive);
        Assert.Equal(13, selected.Count); Assert.Equal(13, selected.Select(t => t.Name).Distinct().Count());
        Assert.All(tools.ToolDefinitions, original => Assert.Contains(selected, t => t.Name == original.Name));
        var context = AgentRequestContextBuilder.Build("CAD assistant", [AiChatMessage.User(prompt)], selected, 8192, aggressive);
        var material = Assert.Single(context.Tools, t => t.Name == "cad_material");
        Assert.Contains(context.Tools, t => t.Name == "cad_status"); Assert.Contains(context.Tools, t => t.Name == "cad_inspect");
        Assert.True(context.EstimatedPromptTokens <= 8192 - context.MaxOutputTokens - (aggressive ? 1280 : 640));
        foreach (var schema in new[] { material.Parameters, ToolSchemaPresentation.Create(material.Parameters) })
        {
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
            var properties = schema.GetProperty("properties");
            Assert.Contains(properties.GetProperty("action").GetProperty("enum").EnumerateArray(), e => e.GetString() == "texture_pixels");
            Assert.Equal(new[] { "materials", "bodies" }, properties.GetProperty("collection").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal(50, properties.GetProperty("limit").GetProperty("maximum").GetInt32());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualAgentRunnerAdvertisesAndExecutesOptionalMaterialContractThroughContextRetry(bool contextRetry)
    {
        var client = new ContractClient(contextRetry); var conversation = new AgentConversation(); var prompt = "Read the material appearance contract";
        conversation.AddUser(prompt); var tools = new CadAgentToolset(new CadCommandLineService(), null);
        var result = await new AgentRunner(client).RunAsync(new("http://fixture/v1", "fixture", "CAD assistant", prompt, conversation, 8192, .2, tools, 16));
        Assert.False(result.ResponseWasEmpty); Assert.Equal(contextRetry ? 3 : 2, client.Calls);
        var receipt = Assert.Single(conversation.Messages, m => m.Role == AiChatRole.Tool);
        using var json = JsonDocument.Parse(receipt.Content!); Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("kg/m3", json.RootElement.GetProperty("densityUnit").GetString());
        Assert.Contains("body", json.RootElement.GetProperty("inspection").GetString());
        Assert.Equal(12, tools.ToolDefinitions.Count);
    }

    private sealed class ContractClient(bool contextRetry) : IAiChatClient
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<string>> GetModelsAsync(string endpoint, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(["fixture"]);
        public Task<AiChatCompletion> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++;
            Assert.Contains(request.Tools, t => t.Name == "cad_material");
            Assert.Contains(request.Tools, t => t.Name == "cad_status"); Assert.Contains(request.Tools, t => t.Name == "cad_inspect");
            if (contextRetry && Calls == 1) throw new AiContextWindowExceededException("Fixture context retry", HttpStatusCode.BadRequest, 9000, 8192);
            return Task.FromResult(request.Messages.Any(m => m.Role == AiChatRole.Tool)
                ? new AiChatCompletion("Material contract inspected", [], "fixture")
                : new AiChatCompletion(null, [new("material-contract", "cad_material", "{\"action\":\"contract\"}")], "fixture"));
        }
    }
}
