using System.Text.Json;
using System.Threading.Channels;
using Cadoryx.Agent;
using Cadoryx.Agent.Codex;
using Cadoryx.AI.Contracts;
using Xunit;

namespace Cadoryx.Tests;

public sealed class CodexAppServerClientTests
{
    [Fact]
    public async Task ModelDiscoveryDeduplicatesResultsAndReconnectsWhenServiceTierChanges()
    {
        var factory = new FakeTransportFactory();
        using var client = new CodexAppServerClient(factory);
        var options = Options();

        Assert.Equal(["gpt-6-sol", "gpt-6-luna"], await client.GetModelsAsync(options));
        Assert.Equal(["gpt-6-sol", "gpt-6-luna"], await client.GetModelsAsync(options));
        Assert.Single(factory.Transports);

        Assert.Equal(["gpt-6-sol", "gpt-6-luna"], await client.GetModelsAsync(options with { ServiceTier = "FAST" }));
        Assert.Equal(2, factory.Transports.Count);
        Assert.True(factory.Transports[0].Disposed);
        Assert.Contains(factory.Transports[1].Writes, line => Method(line) == "initialize");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TurnRoutesOnlyCadToolsAndReturnsTheirActualSuccess(bool toolSucceeds)
    {
        var factory = new FakeTransportFactory(FakeTurnMode.CallTool);
        using var client = new CodexAppServerClient(factory);
        var toolset = new RecordingToolset(toolSucceeds);
        var events = new List<AgentRunEvent>();
        var request = new CodexAgentRunRequest(
            "Inspect the part", "Current document: Bracket", Options(), toolset,
            [AiChatContentPart.FileText("part.txt", "text/plain", "steel"),
             AiChatContentPart.Image("data:image/png;base64,AA==")]);

        var result = await client.RunAsync(request, entry =>
        {
            events.Add(entry);
            return ValueTask.CompletedTask;
        }).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(result.ResponseWasEmpty);
        Assert.Equal("gpt-6-sol", result.Model);
        Assert.Equal("cad_status", Assert.Single(toolset.Calls).Name);
        var server = Assert.Single(factory.Transports);
        using var thread = FindRequest(server, "thread/start");
        var settings = thread.RootElement.GetProperty("params");
        Assert.Equal("read-only", settings.GetProperty("sandbox").GetString());
        Assert.Equal("never", settings.GetProperty("approvalPolicy").GetString());
        Assert.Equal("cadoryx", settings.GetProperty("dynamicTools")[0].GetProperty("namespace").GetString());

        using var turn = FindRequest(server, "turn/start");
        var input = turn.RootElement.GetProperty("params").GetProperty("input");
        Assert.Contains("Current document: Bracket", input[0].GetProperty("text").GetString());
        Assert.Contains(input.EnumerateArray(), item => item.GetProperty("type").GetString() == "text" &&
            item.GetProperty("text").GetString()!.Contains("steel"));
        Assert.Contains(input.EnumerateArray(), item => item.GetProperty("type").GetString() == "image");
        Assert.Equal(toolSucceeds, server.ToolResponse!.Value.GetProperty("result").GetProperty("success").GetBoolean());
        Assert.Contains(events, entry => entry.Kind == AgentRunEventKind.ToolResult && entry.ToolName == "cad_status");
        Assert.Contains(events, entry => entry.Kind == AgentRunEventKind.AssistantMessage && entry.Content == "CAD result");
    }

    [Fact]
    public async Task CancellingAnActiveTurnSendsInterruptAndCompletesPromptly()
    {
        var factory = new FakeTransportFactory(FakeTurnMode.Hold);
        using var client = new CodexAppServerClient(factory);
        using var cancellation = new CancellationTokenSource();
        var pending = client.RunAsync(new CodexAgentRunRequest("Inspect", "", Options(), null),
            cancellationToken: cancellation.Token);
        var server = Assert.Single(factory.Transports);
        await server.TurnStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        await server.Interrupted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(server.Writes, line => Method(line) == "turn/interrupt");
    }

    private static CodexAgentOptions Options() =>
        new("fake-codex", "gpt-6-sol", "medium", "default", Environment.CurrentDirectory);

    private static string? Method(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("method", out var method) ? method.GetString() : null;
    }

    private static JsonDocument FindRequest(FakeTransport server, string method) =>
        JsonDocument.Parse(Assert.Single(server.Writes, line => Method(line) == method));

    private enum FakeTurnMode { Complete, CallTool, Hold }

    private sealed class FakeTransportFactory(FakeTurnMode mode = FakeTurnMode.Complete)
        : ICodexAppServerTransportFactory
    {
        public List<FakeTransport> Transports { get; } = [];

        public ICodexAppServerTransport Start(CodexAgentOptions options)
        {
            var transport = new FakeTransport(mode);
            Transports.Add(transport);
            return transport;
        }
    }

    private sealed class FakeTransport(FakeTurnMode mode) : ICodexAppServerTransport
    {
        private readonly Channel<string> incoming = Channel.CreateUnbounded<string>();
        private readonly object gate = new();
        private readonly List<string> writes = [];

        public IReadOnlyList<string> Writes
        {
            get { lock (gate) return writes.ToArray(); }
        }

        public TaskCompletionSource<bool> TurnStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Interrupted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public JsonElement? ToolResponse { get; private set; }
        public bool Disposed { get; private set; }

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            while (await incoming.Reader.WaitToReadAsync(cancellationToken))
                if (incoming.Reader.TryRead(out var line)) return line;
            return null;
        }

        public Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            lock (gate) writes.Add(line);
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("method", out var method))
            {
                ToolResponse = root.Clone();
                CompleteTurn();
                return Task.CompletedTask;
            }

            var name = method.GetString();
            if (!root.TryGetProperty("id", out var id)) return Task.CompletedTask;
            var requestId = id.GetInt64();
            switch (name)
            {
                case "initialize":
                case "thread/unsubscribe":
                case "turn/interrupt":
                    Respond(requestId, new { });
                    if (name == "turn/interrupt") Interrupted.TrySetResult(true);
                    break;
                case "model/list":
                    Respond(requestId, new { data = new object[]
                    {
                        new { model = "gpt-6-sol" }, new { id = "gpt-6-luna" },
                        new { model = "gpt-6-sol" }, new { model = "" }
                    }});
                    break;
                case "thread/start":
                    Respond(requestId, new { thread = new { id = "thread-1" } });
                    break;
                case "turn/start":
                    Respond(requestId, new { turn = new { id = "turn-1" } });
                    TurnStarted.TrySetResult(true);
                    if (mode == FakeTurnMode.CallTool)
                        Push(new { id = 900, method = "item/tool/call", @params = new
                        {
                            @namespace = "cadoryx", tool = "cad_status", callId = "call-1",
                            arguments = new { section = "document" }
                        }});
                    else if (mode == FakeTurnMode.Complete)
                        CompleteTurn();
                    break;
            }
            return Task.CompletedTask;
        }

        public string GetErrorSummary() => string.Empty;

        public void Dispose()
        {
            Disposed = true;
            incoming.Writer.TryComplete();
        }

        private void Respond(long id, object result) => Push(new { id, result });

        private void CompleteTurn()
        {
            Push(new { method = "item/completed", @params = new
            {
                item = new { type = "agentMessage", text = "CAD result" }
            }});
            Push(new { method = "turn/completed", @params = new
            {
                turn = new { status = "completed" }
            }});
        }

        private void Push(object message) => incoming.Writer.TryWrite(JsonSerializer.Serialize(message));
    }

    private sealed class RecordingToolset(bool success) : IAgentToolset
    {
        public List<AiToolCall> Calls { get; } = [];
        public IReadOnlyList<AiToolDefinition> ToolDefinitions =>
            [new("cad_status", "Read document status", JsonDocument.Parse("{}").RootElement.Clone())];
        public IReadOnlyList<AiToolDefinition> SelectTools(string prompt, bool aggressive = false) => ToolDefinitions;
        public Task<string> ExecuteAsync(AiToolCall toolCall, CancellationToken cancellationToken)
        {
            Calls.Add(toolCall);
            return Task.FromResult(JsonSerializer.Serialize(new { success, document = "Bracket" }));
        }
    }
}
