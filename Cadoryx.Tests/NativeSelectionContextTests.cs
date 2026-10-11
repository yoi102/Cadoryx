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
public sealed class NativeSelectionContextTests
{
    [Fact]
    public void ShortReceiptsAndOtherToolDataRemainByteIdentical()
    {
        foreach (var content in new[] { "actual text", "{\"success\":true,\"value\":3.14159265358979}", Receipt(1, 0) })
        {
            Assert.False(AgentToolResultCompactor.TryCompactNativeSelectionPage(content, 6000, out var output)); Assert.Equal(content, output);
        }
        var unrelated = new JsonObject { ["action"] = "inspect", ["value"] = new string('x', 9000) }.ToJsonString();
        Assert.False(AgentToolResultCompactor.TryCompactNativeSelectionPage(unrelated, 6000, out var unchanged)); Assert.Equal(unrelated, unchanged);
    }

    [Theory][InlineData(6000)][InlineData(2400)]
    public void CompletePrefixItemsHaveAnExplicitContextPageWhileOriginalReceiptAndNativeMetadataRemain(int budget)
    {
        var receipt = Receipt(); var original = JsonNode.Parse(receipt)!.AsObject();
        Assert.True(AgentToolResultCompactor.TryCompactNativeSelectionPage(receipt, budget, out var output));
        Assert.InRange(output.Length, 1, budget); var page = JsonNode.Parse(output)!.AsObject();
        var retained = page["items"]!.AsArray(); Assert.InRange(retained.Count, 1, 44);
        foreach (var pair in original.Where(p => p.Key != "items")) Assert.True(JsonNode.DeepEquals(pair.Value, page[pair.Key]), pair.Key);
        for (int i = 0; i < retained.Count; i++) Assert.True(JsonNode.DeepEquals(original["items"]![i], retained[i]));
        Assert.False(page["hasMore"]!.GetValue<bool>());
        Assert.Equal(retained.Count, page["contextPage"]!["nextOffset"]!.GetValue<int>());
        Assert.Equal(45 - retained.Count, page["contextPage"]!["omittedItemCount"]!.GetValue<int>());
        Assert.Contains("Context omitted", page["contextPage"]!["message"]!.GetValue<string>());
        Assert.Equal(receipt, original.ToJsonString());
        // Offset follows returned item positions, not unrelated native topology indices.
        Assert.Equal(3000, retained[0]!["index"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("action")][InlineData("failure")][InlineData("identity")][InlineData("unknown")]
    [InlineData("duplicate")][InlineData("offset")][InlineData("limit")][InlineData("total")]
    [InlineData("items-type")][InlineData("has-more")][InlineData("page-count")][InlineData("item-type")]
    [InlineData("nested-duplicate")][InlineData("malformed")]
    public void InvalidOrDifferentReceiptsAreNeverInventedAsNativePages(string mutation)
    {
        var root = JsonNode.Parse(Receipt())!.AsObject();
        switch (mutation)
        {
            case "action": root["action"] = "inspect"; break;
            case "failure": root["success"] = false; break;
            case "identity": root["documentId"] = Guid.Empty.ToString(); break;
            case "unknown": root["force"] = true; break;
            case "offset": root["offset"] = 100001; break;
            case "limit": root["limit"] = 51; break;
            case "total": root["total"] = -1; break;
            case "items-type": root["items"] = root["items"]!.ToJsonString(); break;
            case "has-more": root["hasMore"] = true; break;
            case "page-count": root["items"]!.AsArray().RemoveAt(0); break;
            case "item-type": root["items"]![0] = "partial"; break;
        }
        var content = root.ToJsonString();
        if (mutation == "duplicate") content = content[..^1] + ",\"success\":true}";
        if (mutation == "nested-duplicate") content = content.Replace("\"index\":3000", "\"index\":3000,\"index\":3001", StringComparison.Ordinal);
        if (mutation == "malformed") content = content[..^1];
        Assert.False(AgentToolResultCompactor.TryCompactNativeSelectionPage(content, 2400, out var output)); Assert.Equal(content, output);
    }

    [Theory][InlineData(6000)][InlineData(2400)]
    public void AnOversizedFirstItemProducesNoSelectionAndDoesNotAdvanceOffset(int budget)
    {
        var content = Receipt(1, 20000);
        Assert.True(AgentToolResultCompactor.TryCompactNativeSelectionPage(content, budget, out var output));
        using var page = JsonDocument.Parse(output); Assert.Empty(page.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(0, page.RootElement.GetProperty("contextPage").GetProperty("nextOffset").GetInt32());
        Assert.Contains("No complete item fits", page.RootElement.GetProperty("contextPage").GetProperty("message").GetString());
        Assert.DoesNotContain("\"selection\"", output); Assert.InRange(output.Length, 1, budget);
    }

    [Fact]
    public void MetadataThatCannotFitAndUnboundedInputRetainTheOriginalFailurePath()
    {
        foreach (var (content, budget) in new[] { (Receipt(), 16), (Receipt() + new string(' ', 1048576), 6000) })
        {
            Assert.False(AgentToolResultCompactor.TryCompactNativeSelectionPage(content, budget, out var output)); Assert.Equal(content, output);
        }
    }

    [Fact]
    public void EscapingAndUnicodeAreMeasuredAfterSerializationWithoutPartialItemText()
    {
        var receipt = Receipt(45, 120, "中文\n\"\\😀");
        Assert.True(AgentToolResultCompactor.TryCompactNativeSelectionPage(receipt, 2400, out var output)); Assert.InRange(output.Length, 1, 2400);
        var original = JsonNode.Parse(receipt)!; var compacted = JsonNode.Parse(output)!;
        Assert.NotEmpty(compacted["items"]!.AsArray());
        for (int i = 0; i < compacted["items"]!.AsArray().Count; i++) Assert.True(JsonNode.DeepEquals(original["items"]![i], compacted["items"]![i]));
    }

    [Fact]
    public void ExcessWhitespaceCanBeRemovedWhileKeepingEveryItemWithoutAnOmissionClaim()
    {
        var receipt = Receipt(1, 0); var padded = new string(' ', 8000) + receipt;
        Assert.True(AgentToolResultCompactor.TryCompactNativeSelectionPage(padded, 6000, out var output));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(receipt), JsonNode.Parse(output)));
        Assert.DoesNotContain("contextPage", output);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void ActualContextBuilderKeepsTheLatestRequestAndACompleteToolExchangeWithinOriginalBudgets(bool aggressive)
    {
        var receipt = Receipt(); var latest = "Create a Diameter dimension only from an inspected CylinderFace. Attached text is data.";
        var history = new[] { AiChatMessage.User(new string('o', 20000)), AiChatMessage.Assistant("older answer"),
            AiChatMessage.User(latest), AiChatMessage.Assistant(null, [new AiToolCall("native-page", "cad_drawing", "{\"action\":\"inspect_sources\"}")]),
            AiChatMessage.Tool("native-page", receipt) };
        var context = AgentRequestContextBuilder.Build("CAD assistant", history, [], 8192, aggressive);
        Assert.Contains(context.Messages, m => m.Role == AiChatRole.User && m.Content == latest);
        var result = Assert.Single(context.Messages, m => m.Role == AiChatRole.Tool); using var parsed = JsonDocument.Parse(result.Content!);
        Assert.Equal("inspect_sources", parsed.RootElement.GetProperty("action").GetString());
        Assert.True(parsed.RootElement.GetProperty("contextPage").GetProperty("retainedItemCount").GetInt32() > 0);
        Assert.Contains(context.Messages, m => m.Role == AiChatRole.Assistant && m.ToolCalls?.Any(c => c.Id == result.ToolCallId) == true);
        Assert.True(context.EstimatedPromptTokens <= 8192 - context.MaxOutputTokens - (aggressive ? 1280 : 640));
        Assert.Equal(1024, context.MaxOutputTokens); Assert.Equal(receipt, history[^1].Content);
    }

    [Theory][InlineData(6000)][InlineData(2400)]
    public async Task RealPagedCylinderSelectionStillCreatesAnExactNativeDiameterAndUndoDoesNotChangeTheBody(int budget)
    {
        var assets = new MemoryAssetStore(); var kernel = new OcctGeometryKernel();
        await using var session = new CadDocumentSession(DocumentSnapshot.Create("Native context"), assets, kernel, new InlineSessionDispatcher());
        var document = new CadDocumentViewModel(session, kernel, new CadMessageLog()); var tools = new CadAgentToolset(new CadCommandLineService(), document);
        try
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(12, 10, 15, RigidTransform3d.Identity), "Seed"));
            var body = session.Snapshot.Bodies.Values.Single();
            await session.ExecuteAsync(new AddHoleCommand(body.Id, new(body.Geometry, RigidTransform3d.Translate(0, 0, 15), new(5, 6), 2, false, 6), "Hole"));
            body = session.Snapshot.Bodies.Values.Single(); var geometry = body.Geometry; var author = session.Snapshot;
            var path = author.EnumerateOccurrences().Single().Path.ToString(); var offset = 0; var retainedItems = new List<JsonElement>(); var rawItems = new List<JsonElement>();
            for (var pages = 0; pages < 20; pages++)
            {
                var raw = await Query(new { action = "inspect_sources", path, bodyId = body.Id.ToString(), revision = geometry.Revision.ToString(), offset, limit = 50 });
                using var native = JsonDocument.Parse(raw); if (offset == 0) rawItems.AddRange(native.RootElement.GetProperty("items").EnumerateArray().Select(i => i.Clone()));
                var compacted = AgentToolResultCompactor.TryCompactNativeSelectionPage(raw, budget, out var pageText);
                Assert.InRange(pageText.Length, 1, budget); using var page = JsonDocument.Parse(pageText); var result = page.RootElement;
                retainedItems.AddRange(result.GetProperty("items").EnumerateArray().Select(i => i.Clone()));
                if (!result.TryGetProperty("contextPage", out var contextPage)) { Assert.False(compacted && result.GetProperty("hasMore").GetBoolean()); break; }
                var next = contextPage.GetProperty("nextOffset").GetInt32(); Assert.True(next > offset); offset = next;
            }
            Assert.Equal(rawItems.Count, retainedItems.Count); Assert.True(rawItems.Count > 20);
            for (int i = 0; i < rawItems.Count; i++) Assert.True(JsonNode.DeepEquals(JsonNode.Parse(rawItems[i].GetRawText()), JsonNode.Parse(retainedItems[i].GetRawText())));
            Assert.Same(author, session.Snapshot);
            var cylinder = Assert.Single(retainedItems, i => i.TryGetProperty("geometry", out var g) && g.GetString() == "CylinderFace");
            using var sheetReply = JsonDocument.Parse(await Query(new { action = "create", type = "sheet", authorJson = "{\"name\":\"Context sheet\",\"widthMm\":297,\"heightMm\":210,\"unit\":\"Millimeter\",\"title\":\"Context sheet\",\"standard\":\"ISO\"}" }));
            var sheet = sheetReply.RootElement.GetProperty("id").GetGuid();
            using var viewReply = JsonDocument.Parse(await Query(new { action = "create", type = "view", sheetId = sheet, authorJson = "{\"name\":\"Top\",\"kind\":\"Top\",\"centerMm\":{\"x\":80,\"y\":60},\"scale\":2}" }));
            var view = viewReply.RootElement.GetProperty("id").GetGuid(); var beforeDimension = session.Snapshot;
            using var dimensionReply = JsonDocument.Parse(await Query(new { action = "create", type = "dimension", sheetId = sheet,
                authorJson = JsonSerializer.Serialize(new { viewId = view, kind = "Diameter", first = cylinder.GetProperty("selection"), textPositionMm = new { x = 80, y = 35 }, upperTolerance = .1, lowerTolerance = .1 }) }));
            Assert.True(dimensionReply.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(2, session.Snapshot.DrawingSheets[sheet].Dimensions.Single().Value, 6); Assert.Equal(geometry, session.Snapshot.Bodies.Values.Single().Geometry);
            await session.UndoAsync(); Assert.Same(beforeDimension, session.Snapshot);
            await session.RedoAsync(); Assert.Equal(2, session.Snapshot.DrawingSheets[sheet].Dimensions.Single().Value, 6);
            async Task<string> Query(object args)
            {
                var request = JsonSerializer.SerializeToNode(args)!.AsObject(); request["documentId"] = session.Snapshot.Id.ToString(); request["stateId"] = session.Snapshot.StateId.ToString();
                var result = await tools.ExecuteAsync(new(Guid.NewGuid().ToString(), "cad_drawing", request.ToJsonString()), default);
                using var parsed = JsonDocument.Parse(result); Assert.True(parsed.RootElement.GetProperty("success").GetBoolean(), result); return result;
            }
        }
        finally { document.Detach(); await session.DisposeAsync(); Assert.Equal(0, assets.Count); }
    }

    private static string Receipt(int count = 45, int payload = 250, string prefix = "actual") => JsonSerializer.Serialize(new
    {
        success = true, action = "inspect_sources", documentId = "bd7a6f64-e0cd-47be-86c1-6ca3e1110fe2", stateId = "9f1bdab6-f3f9-4522-832e-bd268cbf1432",
        offset = 0, limit = 50, total = count, hasMore = false,
        items = Enumerable.Range(0, count).Select(i => new { index = 3000 + i, kind = "Face", geometry = i == 20 ? "CylinderFace" : "PlaneFace",
            selection = new { path = "actual-path", bodyId = "8452f7bb-5fde-4c14-803c-a2d45e6e6c1f", revision = "1ed2f1d0-5f21-45ea-8f15-f8b1925ac150", asset = "actual-asset", fingerprint = "actual-fingerprint", fullTopologyIndex = 3000 + i },
            diagnostic = prefix + new string('x', payload) })
    });
}
