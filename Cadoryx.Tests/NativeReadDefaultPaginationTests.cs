using System.Text.Json;
using System.Text.Json.Nodes;
using Cadoryx.Agent;
using Cadoryx.AI.Contracts;
using Xunit;

namespace Cadoryx.Tests;

public sealed class NativeReadDefaultPaginationTests
{
    private const string Document = "bd7a6f64-e0cd-47be-86c1-6ca3e1110fe2";
    private const string State = "9f1bdab6-f3f9-4522-832e-bd268cbf1432";
    private const string Body = "8452f7bb-5fde-4c14-803c-a2d45e6e6c1f";
    private const string Revision = "1ed2f1d0-5f21-45ea-8f15-f8b1925ac150";
    private const string Path = Document + "/4f8691db-4c84-47d8-ad77-7c59e9cabc0b";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void OmittedAndExplicitDefaultsPairWithTheSameProductionPage(bool explicitOffset, bool explicitLimit)
    {
        var call = Query(explicitOffset ? 0 : null, explicitLimit ? 8 : null);
        var raw = Receipt(0, 8);
        using var receipt = JsonDocument.Parse(raw);
        var continuation = AgentNativeReadContinuation.FromActualQuery(call, receipt.RootElement,
            receipt.RootElement.GetProperty("items").EnumerateArray().ToArray());
        Assert.NotNull(continuation);
        using var next = JsonDocument.Parse(JsonSerializer.Serialize(continuation.NextRequest(2, 8, 2)));
        Assert.Equal(2, next.RootElement.GetProperty("offset").GetInt32());
        Assert.Equal(2, next.RootElement.GetProperty("limit").GetInt32());
        Assert.Equal(Path, next.RootElement.GetProperty("path").GetString());
        Assert.Equal(call, Query(explicitOffset ? 0 : null, explicitLimit ? 8 : null));
    }

    [Theory]
    [InlineData(37, 8, false, false)]
    [InlineData(0, 50, false, false)]
    [InlineData(37, 50, false, false)]
    [InlineData(37, 8, false, true)]
    [InlineData(0, 50, true, false)]
    public void AReceiptFromAnotherPageCannotInventContinuationForAnOmittedParameter(
        int offset, int limit, bool explicitOffset, bool explicitLimit)
    {
        var call = Query(explicitOffset ? offset : null, explicitLimit ? limit : null);
        var raw = Receipt(offset, limit);
        Assert.True(AgentToolResultCompactor.TryCompactNativeSelectionPage(raw, 2400, out var output, call));
        Assert.True(AgentToolResultCompactor.TryCompactNativeSelectionPage(raw, 2400, out var unpaired));
        Assert.Equal(unpaired, output);
        using var page = JsonDocument.Parse(output);
        Assert.False(page.RootElement.GetProperty("contextPage").TryGetProperty("nextRequest", out _));
        Assert.InRange(output.Length, 1, 2400);
    }

    [Fact]
    public void ExplicitNonDefaultPagesStillProduceTheirOwnContinuation()
    {
        var call = Query(37, 50);
        Assert.True(AgentToolResultCompactor.TryCompactNativeSelectionPage(Receipt(37, 50), 2400, out var output, call));
        using var page = JsonDocument.Parse(output);
        var retained = page.RootElement.GetProperty("items").GetArrayLength();
        Assert.True(retained > 0);
        var next = page.RootElement.GetProperty("contextPage").GetProperty("nextRequest");
        Assert.Equal(37 + retained, next.GetProperty("offset").GetInt32());
        Assert.Equal(Math.Min(retained, 50), next.GetProperty("limit").GetInt32());
    }

    [Theory]
    [InlineData("offset", "null")]
    [InlineData("offset", "\"0\"")]
    [InlineData("offset", "0.5")]
    [InlineData("limit", "null")]
    [InlineData("limit", "\"8\"")]
    [InlineData("limit", "8.5")]
    public void InvalidExplicitParametersDoNotFallBackToDefaults(string field, string value)
    {
        var arguments = JsonNode.Parse(Query(null, null).ArgumentsJson)!.AsObject();
        arguments[field] = JsonNode.Parse(value);
        var call = new AiToolCall("read", "cad_drawing", arguments.ToJsonString());
        using var receipt = JsonDocument.Parse(Receipt(0, 8));
        Assert.Null(AgentNativeReadContinuation.FromActualQuery(call, receipt.RootElement,
            receipt.RootElement.GetProperty("items").EnumerateArray().ToArray()));
    }

    private static AiToolCall Query(int? offset, int? limit)
    {
        var args = JsonSerializer.SerializeToNode(new
        {
            action = "inspect_sources", documentId = Document, stateId = State,
            path = Path, bodyId = Body, revision = Revision
        })!.AsObject();
        if (offset.HasValue) args["offset"] = offset.Value;
        if (limit.HasValue) args["limit"] = limit.Value;
        return new("read", "cad_drawing", args.ToJsonString());
    }

    private static string Receipt(int offset, int limit) => JsonSerializer.Serialize(new
    {
        success = true, action = "inspect_sources", documentId = Document, stateId = State,
        offset, limit, total = offset + limit, hasMore = false,
        items = Enumerable.Range(0, limit).Select(position => new
        {
            index = 3000 + position, kind = "Face", geometry = "PlaneFace",
            selection = new { path = Path, bodyId = Body, revision = Revision,
                asset = "original-asset", fingerprint = "original-fingerprint", index = 3000 + position },
            diagnostic = new string('x', 400)
        })
    });
}
