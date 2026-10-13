using System.Text.Json;
using System.Text.Json.Nodes;
using Cadoryx.Agent;
using Cadoryx.AI.Contracts;
using Xunit;

namespace Cadoryx.Tests;

public sealed class NativeCompleteSourceContextTests
{
    private static (AiToolCall Call, string Receipt) Captured()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "AgentContext", "NativeSelectionPage.json")));
        var root = fixture.RootElement;
        return (new("captured-native-read", root.GetProperty("toolName").GetString()!,
            root.GetProperty("arguments").GetString()!), root.GetProperty("receipt").GetString()!);
    }

    [Fact]
    public void CompleteCapturedCylinderRowSurvivesTheOriginalNormalBudgetWithoutAnInventedSelection()
    {
        var (call, receipt) = Captured();
        Assert.True(AgentToolResultCompactor.TryCompactNativeSelectionPage(receipt, 6000, out var content, call));
        Assert.InRange(content.Length, 1, 6000);
        using var original = JsonDocument.Parse(receipt);
        using var compacted = JsonDocument.Parse(content);
        var page = compacted.RootElement;
        var prefix = page.GetProperty("items");
        Assert.InRange(prefix.GetArrayLength(), 1, 37);
        for (var index = 0; index < prefix.GetArrayLength(); index++)
            Assert.Equal(original.RootElement.GetProperty("items")[index].GetRawText(), prefix[index].GetRawText());
        var context = page.GetProperty("contextPage");
        var additional = context.GetProperty("completeSourceItems");
        var cylinder = Assert.Single(additional.EnumerateArray(), row =>
            row.GetProperty("item").GetProperty("geometry").GetString() == "CylinderFace");
        Assert.Equal(38, cylinder.GetProperty("offset").GetInt32());
        Assert.Equal(original.RootElement.GetProperty("items")[38].GetRawText(), cylinder.GetProperty("item").GetRawText());
        foreach (var row in additional.EnumerateArray())
            Assert.Equal(original.RootElement.GetProperty("items")[row.GetProperty("offset").GetInt32()].GetRawText(), row.GetProperty("item").GetRawText());
        Assert.Equal(prefix.GetArrayLength(), context.GetProperty("nextRequest").GetProperty("offset").GetInt32());
        Assert.Equal(45 - prefix.GetArrayLength(), context.GetProperty("omittedItemCount").GetInt32());
        Assert.Equal(prefix.GetArrayLength() + additional.GetArrayLength(), context.GetProperty("includedItemCount").GetInt32());
        Assert.Equal(45 - prefix.GetArrayLength() - additional.GetArrayLength(), context.GetProperty("remainingOmittedItemCount").GetInt32());
        Assert.Contains("No selection or edit was executed", context.GetProperty("completeSourceItemsNote").GetString());
        Assert.Equal(receipt, Captured().Receipt);
        Assert.Equal(call, Captured().Call);
    }

    [Theory]
    [InlineData(6000)]
    [InlineData(2400)]
    public void BothOriginalBudgetsRetainCompletePrefixRowsAndEveryAdditionalRowHasItsActualPosition(int budget)
    {
        var (call, receipt) = Captured();
        Assert.True(AgentToolResultCompactor.TryCompactNativeSelectionPage(receipt, budget, out var content, call));
        Assert.InRange(content.Length, 1, budget);
        using var original = JsonDocument.Parse(receipt);
        using var compacted = JsonDocument.Parse(content);
        var page = compacted.RootElement;
        var prefix = page.GetProperty("items");
        Assert.True(prefix.GetArrayLength() > 0);
        for (var index = 0; index < prefix.GetArrayLength(); index++)
            Assert.Equal(original.RootElement.GetProperty("items")[index].GetRawText(), prefix[index].GetRawText());
        if (page.GetProperty("contextPage").TryGetProperty("completeSourceItems", out var rows))
            foreach (var row in rows.EnumerateArray())
                Assert.Equal(original.RootElement.GetProperty("items")[row.GetProperty("offset").GetInt32()].GetRawText(), row.GetProperty("item").GetRawText());
    }

    [Theory]
    [InlineData("no-call")]
    [InlineData("documentId")]
    [InlineData("stateId")]
    [InlineData("bodyId")]
    [InlineData("revision")]
    [InlineData("path")]
    [InlineData("offset")]
    [InlineData("limit")]
    [InlineData("unknown")]
    public void UnpairedOrMismatchedQueriesCannotExposeAdditionalNativeSelections(string fault)
    {
        var (call, receipt) = Captured();
        var args = JsonNode.Parse(call.ArgumentsJson)!.AsObject();
        if (fault == "unknown") args["id"] = Guid.NewGuid().ToString();
        else if (fault == "path") args["path"] = "foreign-path";
        else if (fault == "offset") args["offset"] = 1;
        else if (fault == "limit") args["limit"] = 49;
        else if (fault != "no-call") args[fault] = Guid.NewGuid().ToString();
        call = call with { ArgumentsJson = args.ToJsonString() };
        Assert.True(AgentToolResultCompactor.TryCompactNativeSelectionPage(receipt, 6000, out var content,
            fault == "no-call" ? null : call));
        using var compacted = JsonDocument.Parse(content);
        var context = compacted.RootElement.GetProperty("contextPage");
        Assert.False(context.TryGetProperty("completeSourceItems", out _));
        Assert.False(context.TryGetProperty("nextRequest", out _));
    }

    [Fact]
    public void ACompleteReceiptRemainsByteIdenticalWithoutContextMetadata()
    {
        var (call, receipt) = Captured();
        Assert.False(AgentToolResultCompactor.TryCompactNativeSelectionPage(receipt, receipt.Length, out var unchanged, call));
        Assert.Equal(receipt, unchanged);
    }
}
