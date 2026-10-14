using System.Net;
using System.Text;
using System.Text.Json;
using Cadoryx.Agent;
using Cadoryx.AI.Contracts;
using Cadoryx.AI.LmStudio;
using Xunit;

namespace Cadoryx.Tests;

public sealed class LmStudioCompletionIntegrityTests
{
    private const string ValidCall = "{\"id\":\"one\",\"function\":{\"name\":\"write\",\"arguments\":\"{}\"}}";
    private static string Completion(string calls, string reason = "tool_calls", string content = "null") =>
        "{\"model\":\"fixture\",\"choices\":[{\"finish_reason\":\"" + reason + "\",\"message\":{\"content\":" + content + ",\"tool_calls\":" + calls + "}}]}";

    public static IEnumerable<object[]> InvalidBatches()
    {
        foreach (string invalid in new[]
        {
            "null", "42", "{}", "{\"id\":\"two\"}",
            "{\"id\":\"two\",\"function\":null}",
            "{\"id\":\"two\",\"function\":{\"arguments\":\"{}\"}}",
            "{\"id\":\" \",\"function\":{\"name\":\"write\"}}",
            "{\"id\":42,\"function\":{\"name\":\"write\"}}",
            "{\"id\":\"two\",\"function\":{\"name\":42}}",
            "{\"id\":\"two\",\"function\":{\"name\":\"write\",\"arguments\":{}}}",
            "{\"id\":\"two\",\"function\":{\"name\":\"write\",\"arguments\":\"{\"}}",
            "{\"id\":\"two\",\"function\":{\"name\":\"write\",\"arguments\":\"[]\"}}",
            "{\"id\":\"two\",\"type\":\"custom\",\"function\":{\"name\":\"write\"}}",
            "{\"id\":\"two\",\"id\":\"three\",\"function\":{\"name\":\"write\"}}",
            "{\"id\":\"two\",\"function\":{\"name\":\"read\",\"name\":\"write\"}}",
            ValidCall
        }) yield return [Completion("[" + ValidCall + "," + invalid + "]"), "invalid tool-call batch"];
        yield return [Completion("{}"), "invalid tool-call batch"];
        yield return [Completion("[]"), "invalid tool-call batch"];
        yield return [Completion("null"), "invalid tool-call batch"];
        yield return ["{\"choices\":[{\"message\":null}]}", "without an assistant message"];
        yield return ["{\"choices\":[null]}", "without an assistant message"];
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task TruncatedClientResponseRejectsEvenApparentlyCompleteToolCalls(bool hasCalls)
    {
        using var handler = new Responses(Completion(hasCalls ? "[" + ValidCall + "]" : "[]", "length", "\"unfinished answer\""));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<AiResponseTruncatedException>(() => new LmStudioChatClient(http).CompleteAsync(
            new("http://localhost/v1", "fixture", [AiChatMessage.User("Write")], [])));
        Assert.Contains("token limit", error.Message);
        Assert.Single(handler.Requests);
    }

    [Theory, MemberData(nameof(InvalidBatches))]
    public async Task IncompleteOrMalformedBatchNeverDispatchesItsValidFirstWrite(string response, string expected)
    {
        using var handler = new Responses(response);
        using var http = new HttpClient(handler);
        var conversation = new AgentConversation(); conversation.AddUser("Make two writes");
        var tools = new Writes();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new AgentRunner(new LmStudioChatClient(http)).RunAsync(
            new("http://localhost/v1", "fixture", "CAD assistant", "Make two writes", conversation, 8192, .2, tools, 16)));
        Assert.Contains(expected, error.Message);
        Assert.Equal(0, tools.Calls);
        Assert.Single(conversation.Messages);
        Assert.Equal(AiChatRole.User, conversation.Messages[0].Role);
        Assert.Single(handler.Requests);
        using var wire = JsonDocument.Parse(handler.Requests[0]);
        Assert.Equal(1024, wire.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(wire.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task ACompleteOrderedBatchStillDispatchesEveryCallExactlyOnce()
    {
        string second = ValidCall.Replace("one", "two", StringComparison.Ordinal);
        using var handler = new Responses(Completion("[" + ValidCall + "," + second + "]"), Completion("[]", "stop", "\"done\""));
        using var http = new HttpClient(handler);
        var conversation = new AgentConversation(); conversation.AddUser("Make two writes");
        var tools = new Writes();
        var result = await new AgentRunner(new LmStudioChatClient(http)).RunAsync(
            new("http://localhost/v1", "fixture", "CAD assistant", "Make two writes", conversation, 8192, .2, tools, 16));
        Assert.False(result.ResponseWasEmpty);
        Assert.Equal(2, tools.Calls);
        Assert.Equal(new[] { "one", "two" }, conversation.Messages.Where(m => m.Role == AiChatRole.Tool).Select(m => m.ToolCallId));
        Assert.Equal("done", conversation.Messages[^1].Content);
    }

    [Fact]
    public async Task TruncationOnALaterResponseRetainsEarlierReceiptsWithoutRepeatingTheirEdits()
    {
        using var handler = new Responses(Completion("[" + ValidCall + "]"), Completion("[" + ValidCall.Replace("one", "two", StringComparison.Ordinal) + "]", "length"), Completion("[]", "stop", "\"done\""));
        using var http = new HttpClient(handler);
        var conversation = new AgentConversation(); conversation.AddUser("Make writes"); var tools = new Writes();
        var events = new List<AgentRunEvent>();
        var result = await new AgentRunner(new LmStudioChatClient(http)).RunAsync(
            new("http://localhost/v1", "fixture", "CAD assistant", "Make writes", conversation, 8192, .2, tools, 16), e => { events.Add(e); return ValueTask.CompletedTask; });
        Assert.False(result.ResponseWasEmpty);
        Assert.Equal(1, tools.Calls);
        Assert.Equal("one", Assert.Single(conversation.Messages, m => m.Role == AiChatRole.Tool).ToolCallId);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(new[] { AiChatRole.User, AiChatRole.Assistant, AiChatRole.Tool, AiChatRole.Assistant }, conversation.Messages.Select(m => m.Role));
        Assert.Single(events, e => e.Kind == AgentRunEventKind.TruncatedResponseRetry);
        using var recovery = JsonDocument.Parse(handler.Requests[^1]);
        var messages = recovery.RootElement.GetProperty("messages");
        Assert.Contains("Truncated reply: use receipts", messages[0].GetProperty("content").GetString());
        Assert.Equal("one", messages[2].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("one", messages[3].GetProperty("tool_call_id").GetString());
        Assert.All(handler.Requests, body => { using var wire = JsonDocument.Parse(body); Assert.Equal(1024, wire.RootElement.GetProperty("max_tokens").GetInt32()); });
    }

    [Theory, InlineData("length", "length"), InlineData("stop", "length"), InlineData("length", "stop")]
    public async Task TruncationAndEmptyReplyShareExactlyOneRecoveryAllowance(string first, string second)
    {
        using var handler = new Responses(Completion("[]", first), Completion("[]", second));
        using var http = new HttpClient(handler);
        var conversation = new AgentConversation(); conversation.AddUser("Read");
        var events = new List<AgentRunEvent>();
        Task<AgentRunResult> Run() => new AgentRunner(new LmStudioChatClient(http)).RunAsync(
            new("http://localhost/v1", "fixture", "CAD assistant", "Read", conversation, 8192, .2, null, 16), e => { events.Add(e); return ValueTask.CompletedTask; });
        if (second == "length") await Assert.ThrowsAsync<AiResponseTruncatedException>(Run);
        else Assert.True((await Run()).ResponseWasEmpty);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Single(events);
        Assert.Equal(first == "length" ? AgentRunEventKind.TruncatedResponseRetry : AgentRunEventKind.EmptyResponseRetry, events[0].Kind);
        Assert.Equal(1 + (first == "stop" ? 1 : 0) + (second == "stop" ? 1 : 0), conversation.Messages.Count);
        Assert.DoesNotContain(conversation.Messages, m => m.Role == AiChatRole.Tool);
    }

    [Fact]
    public async Task TruncationInTheLastRoundCannotCreateAnExtraRequest()
    {
        using var handler = new Responses(Completion("[" + ValidCall + "]", "length"));
        using var http = new HttpClient(handler);
        var conversation = new AgentConversation(); conversation.AddUser("Write"); var tools = new Writes();
        var events = new List<AgentRunEvent>();
        await Assert.ThrowsAsync<AiResponseTruncatedException>(() => new AgentRunner(new LmStudioChatClient(http)).RunAsync(
            new("http://localhost/v1", "fixture", "CAD assistant", "Write", conversation, 8192, .2, tools, 1), e => { events.Add(e); return ValueTask.CompletedTask; }));
        Assert.Single(handler.Requests); Assert.Single(conversation.Messages); Assert.Empty(events); Assert.Equal(0, tools.Calls);
    }

    [Fact]
    public async Task CancellationAtTheTruncationEventPreventsTheRecoveryRequest()
    {
        using var handler = new Responses(Completion("[" + ValidCall + "]", "length"));
        using var http = new HttpClient(handler); using var cancellation = new CancellationTokenSource();
        var conversation = new AgentConversation(); conversation.AddUser("Write"); var tools = new Writes();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AgentRunner(new LmStudioChatClient(http)).RunAsync(
            new("http://localhost/v1", "fixture", "CAD assistant", "Write", conversation, 8192, .2, tools, 16), e => {
                if (e.Kind == AgentRunEventKind.TruncatedResponseRetry) cancellation.Cancel(); return ValueTask.CompletedTask;
            }, cancellation.Token));
        Assert.Single(handler.Requests); Assert.Single(conversation.Messages); Assert.Equal(0, tools.Calls);
    }

    [Fact]
    public async Task RecoveryToolCallConsumesTheLastRoundAndRecoveryInstructionIsTemporary()
    {
        using var handler = new Responses(Completion("[]", "length"), Completion("[" + ValidCall + "]"));
        using var http = new HttpClient(handler);
        var conversation = new AgentConversation(); conversation.AddUser("Write"); var tools = new Writes();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new AgentRunner(new LmStudioChatClient(http)).RunAsync(
            new("http://localhost/v1", "fixture", "CAD assistant", "Write", conversation, 8192, .2, tools, 2)));
        Assert.Contains("maximum number", error.Message); Assert.Equal(2, handler.Requests.Count); Assert.Equal(1, tools.Calls);
        Assert.Equal(new[] { AiChatRole.User, AiChatRole.Assistant, AiChatRole.Tool }, conversation.Messages.Select(m => m.Role));
        Assert.DoesNotContain(conversation.Messages, m => m.Content?.Contains("Truncated reply", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task RecoveryUsesExistingConservativeContextWithoutChangingOutputCap()
    {
        string receipt = "{\"success\":true,\"details\":\"" + new string('x', 5500) + "\"}";
        using var handler = new Responses(Completion("[" + ValidCall + "]"), Completion("[]", "length"), Completion("[]", "stop", "\"done\""));
        using var http = new HttpClient(handler);
        var conversation = new AgentConversation(); conversation.AddUser("Write"); var tools = new Writes(receipt);
        var result = await new AgentRunner(new LmStudioChatClient(http)).RunAsync(
            new("http://localhost/v1", "fixture", "CAD assistant", "Write", conversation, 8192, .2, tools, 16));
        Assert.False(result.ResponseWasEmpty); Assert.Equal(8192, result.ContextWindowTokens); Assert.Equal(1, tools.Calls);
        Assert.Equal(new[] { false, true }, tools.Selections);
        Assert.True(handler.Requests[^1].Length < handler.Requests[^2].Length);
        Assert.Equal(receipt, Assert.Single(conversation.Messages, m => m.Role == AiChatRole.Tool).Content);
        Assert.All(handler.Requests, body => { using var wire = JsonDocument.Parse(body); Assert.Equal(1024, wire.RootElement.GetProperty("max_tokens").GetInt32()); });
    }

    [Fact]
    public async Task LaterToolRoundsKeepConservativeContextButRemoveTemporaryRecoveryGuidance()
    {
        string second = ValidCall.Replace("one", "two", StringComparison.Ordinal);
        using var handler = new Responses(Completion("[]", "length"), Completion("[" + ValidCall + "]"), Completion("[" + second + "]"), Completion("[]", "stop", "\"done\""));
        using var http = new HttpClient(handler);
        var conversation = new AgentConversation(); conversation.AddUser("Write"); var tools = new Writes();
        var events = new List<AgentRunEvent>();
        var result = await new AgentRunner(new LmStudioChatClient(http)).RunAsync(
            new("http://localhost/v1", "fixture", "CAD assistant", "Write", conversation, 8192, .2, tools, 4), e => { events.Add(e); return ValueTask.CompletedTask; });
        Assert.False(result.ResponseWasEmpty); Assert.Equal(2, tools.Calls);
        Assert.Equal(new[] { false, true, true, true }, tools.Selections);
        Assert.Single(events, e => e.Kind == AgentRunEventKind.TruncatedResponseRetry);
        for (var i = 0; i < handler.Requests.Count; i++)
        {
            using var wire = JsonDocument.Parse(handler.Requests[i]);
            string? system = wire.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
            if (i == 1) Assert.Contains("Truncated reply", system);
            else Assert.Equal("CAD assistant", system);
            Assert.Equal(1024, wire.RootElement.GetProperty("max_tokens").GetInt32());
        }
        Assert.Equal(new[] { "one", "two" }, conversation.Messages.Where(m => m.Role == AiChatRole.Tool).Select(m => m.ToolCallId));
    }

    [Theory, InlineData("null"), InlineData("[]")]
    public async Task OrdinaryEmptyStopStillUsesOnlyTheExistingOneRetry(string calls)
    {
        using var handler = new Responses(Completion(calls, "stop"), Completion("[]", "stop", "\"done\""));
        using var http = new HttpClient(handler);
        var conversation = new AgentConversation(); conversation.AddUser("Read"); var tools = new Writes();
        var result = await new AgentRunner(new LmStudioChatClient(http)).RunAsync(
            new("http://localhost/v1", "fixture", "CAD assistant", "Read", conversation, 8192, .2, tools, 16));
        Assert.False(result.ResponseWasEmpty); Assert.Equal(0, tools.Calls); Assert.Equal(2, handler.Requests.Count);
        Assert.Null(conversation.Messages[1].Content);
    }

    [Theory, InlineData(""), InlineData(",\"arguments\":null"), InlineData(",\"arguments\":\"{}\"")]
    public async Task ExistingOmittedNullAndEmptyObjectArgumentsRemainCompatible(string arguments)
    {
        using var handler = new Responses(Completion("[{\"id\":\"one\",\"function\":{\"name\":\"write\"" + arguments + "}}]"));
        using var http = new HttpClient(handler);
        var completion = await new LmStudioChatClient(http).CompleteAsync(new("http://localhost/v1", "fixture", [AiChatMessage.User("Read")], []));
        Assert.Equal("{}", Assert.Single(completion.ToolCalls).ArgumentsJson);
    }

    [Fact]
    public async Task ValidArgumentWhitespaceAndDuplicatePropertiesStayUntouchedForTheProductionValidator()
    {
        const string arguments = " { \"id\":\"one\", \"id\":\"two\" } ";
        using var handler = new Responses(Completion("[{\"id\":\"one\",\"function\":{\"name\":\"write\",\"arguments\":" + JsonSerializer.Serialize(arguments) + "}}]"));
        using var http = new HttpClient(handler);
        var completion = await new LmStudioChatClient(http).CompleteAsync(new("http://localhost/v1", "fixture", [AiChatMessage.User("Read")], []));
        Assert.Equal(arguments, Assert.Single(completion.ToolCalls).ArgumentsJson);
    }

    [Fact]
    public async Task LegacyAnswerWithoutFinishReasonOrToolCallsRemainsCompatible()
    {
        using var handler = new Responses("{\"choices\":[{\"message\":{\"content\":\"done\"}}]}");
        using var http = new HttpClient(handler);
        var completion = await new LmStudioChatClient(http).CompleteAsync(new("http://localhost/v1", "fixture", [AiChatMessage.User("Read")], []));
        Assert.Equal("done", completion.Content); Assert.Empty(completion.ToolCalls);
    }

    private sealed class Responses(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> pending = new(responses);
        internal List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(token));
            return new(HttpStatusCode.OK) { Content = new StringContent(pending.Dequeue(), Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Writes(string receipt = "{\"success\":true}") : IAgentToolset
    {
        private static readonly AiToolDefinition Definition = new("write", "Write", JsonSerializer.SerializeToElement(new { type = "object" }));
        internal int Calls { get; private set; }
        internal List<bool> Selections { get; } = [];
        public IReadOnlyList<AiToolDefinition> ToolDefinitions { get; } = [Definition];
        public IReadOnlyList<AiToolDefinition> SelectTools(string prompt, bool aggressive = false) { Selections.Add(aggressive); return ToolDefinitions; }
        public Task<string> ExecuteAsync(AiToolCall call, CancellationToken token) { Calls++; return Task.FromResult(receipt); }
    }
}
