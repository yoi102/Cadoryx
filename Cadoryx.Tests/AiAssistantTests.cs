using System.Text.Json;
using System.Net;
using System.Text;
using Cadoryx.Agent;
using Cadoryx.Agent.Codex;
using Cadoryx.AI.LmStudio;
using Cadoryx.AI.Contracts;
using Cadoryx.CommandLine;
using Cadoryx.ViewModels.Toolboxes;
using Cadoryx.ViewModels.Services.Platform;
using Xunit;

namespace Cadoryx.Tests;

public sealed class AiAssistantTests
{
    [Fact]
    public async Task AgentRunsToolRoundAndStoresResultBeforeFinalAnswer()
    {
        var client = new FakeChatClient(); var conversation = new AgentConversation();
        conversation.AddUser("inspect", [AiChatContentPart.TextPart("inspect"), AiChatContentPart.Image("data:image/png;base64,AA==")]);
        var toolset = new FakeToolset();
        var result = await new AgentRunner(client).RunAsync(new AgentRunRequest(
            "http://localhost:1234/v1", "test", "Inspect CAD", "inspect", conversation,
            8192, 0.2, toolset));
        Assert.False(result.ResponseWasEmpty);
        Assert.Equal(1, toolset.Calls);
        Assert.Equal(AiChatRole.Tool, conversation.Messages[2].Role);
        Assert.Equal("ok", conversation.Messages[^1].Content);
        Assert.Contains(client.LastRequest!.Messages,
            message => message.ContentParts?.Any(part => part.Type == AiChatContentPartType.Image) == true);
    }

    [Fact]
    public async Task CadAgentToolsetUsesBoundedCommandCatalog()
    {
        var tools = new CadAgentToolset(new CadCommandLineService(), null);
        Assert.Equal(5, tools.ToolDefinitions.Count);
        Assert.Contains("\"active\":false", await tools.ExecuteAsync(new AiToolCall("1", "cad_status", "{}"), default));
        Assert.Contains("\"success\":true", await tools.ExecuteAsync(new AiToolCall("2", "cad_command", "{\"command\":\"HELP\"}"), default));
        Assert.Contains("\"success\":false", await tools.ExecuteAsync(new AiToolCall("3", "cad_command", "{\"command\":\"BOX Demo 1 2 3\"}"), default));
        Assert.Contains("Invalid tool arguments", await tools.ExecuteAsync(new AiToolCall("bad", "cad_command", "{"), default));
        Assert.Contains("Unknown tool", await tools.ExecuteAsync(new AiToolCall("4", "shell", "{}"), default));
    }

    [Fact]
    public void DefaultAgentContextRetainsInspectSelectAndEditTools()
    {
        var tools = new CadAgentToolset(new CadCommandLineService(), null);
        var context = AgentRequestContextBuilder.Build("CAD assistant",
            [AiChatMessage.User("Select the exact bracket instance")], tools.ToolDefinitions,
            AiAssistantSettings.DefaultContextWindowTokens);

        Assert.Contains(context.Tools, tool => tool.Name == "cad_inspect");
        Assert.Contains(context.Tools, tool => tool.Name == "cad_select");
        Assert.Contains(context.Tools, tool => tool.Name == "cad_edit");
    }

    [Fact]
    public void CancellingSettingsRestoresDraftWithoutResettingConversation()
    {
        var store = new FakeSettingsStore(); var codex = new FakeCodexClient();
        using var model = CreateAssistant(store, codex);
        var originalMessage = model.Messages[0];
        model.BeginSettingsEdit();
        model.Provider = AiAssistantProvider.Codex;
        model.ReasoningEffort = "high";
        model.CancelSettingsEdit();
        Assert.Equal(AiAssistantProvider.LmStudio, model.Provider);
        Assert.Equal("medium", model.ReasoningEffort);
        Assert.Same(originalMessage, model.Messages[0]);
        Assert.Equal(0, codex.ResetCount);
        model.BeginSettingsEdit();
        model.Provider = AiAssistantProvider.Codex;
        Assert.True(model.TrySaveSettings());
        Assert.Equal(AiAssistantProvider.Codex, store.Load().Provider);
        Assert.Equal(1, codex.ResetCount);
    }

    [Fact]
    public async Task MissingModelKeepsDraftPromptAndAttachment()
    {
        using var model = CreateAssistant(new FakeSettingsStore(), new FakeCodexClient());
        model.UserInput = "Inspect this";
        Assert.True(model.AddAttachment(new CadChatAttachment("part.txt",
            AiChatContentPart.FileText("part.txt", "text/plain", "part"))));
        await model.SendCommand.ExecuteAsync(null);
        Assert.Equal("Inspect this", model.UserInput);
        Assert.Single(model.Attachments);
        Assert.False(model.IsBusy);
        Assert.Contains("Select an LM Studio model", model.ConnectionStatus);
    }

    [Fact]
    public async Task ToolboxSendsTextAndImageAttachmentsThroughLmStudioRequest()
    {
        var chat = new FakeChatClient();
        using var model = CreateAssistant(new FakeSettingsStore(), new FakeCodexClient(), chat);
        model.Model = "test";
        model.UserInput = "Inspect these";
        Assert.True(model.AddAttachment(new CadChatAttachment("part.txt",
            AiChatContentPart.FileText("part.txt", "text/plain", "part contents"))));
        Assert.True(model.AddAttachment(new CadChatAttachment("image.png",
            AiChatContentPart.Image("data:image/png;base64,AA=="))));
        await model.SendCommand.ExecuteAsync(null);
        var user = Assert.Single(chat.LastRequest!.Messages, x => x.Role == AiChatRole.User);
        Assert.Contains(user.ContentParts!, x => x.FileName == "part.txt" && x.Text!.Contains("part contents"));
        Assert.Contains(user.ContentParts!, x => x.Type == AiChatContentPartType.Image &&
            x.DataUrl == "data:image/png;base64,AA==");
        Assert.Empty(model.Attachments);
    }

    [Fact]
    public async Task LmStudioHttpPayloadContainsBothAttachmentKindsAndToolSchema()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var client = new LmStudioChatClient(http);
        var tools = new CadAgentToolset(new CadCommandLineService(), null);
        var message = AiChatMessage.User("Inspect", [AiChatContentPart.TextPart("Inspect"),
            AiChatContentPart.FileText("part.txt", "text/plain", "part contents"),
            AiChatContentPart.Image("data:image/png;base64,AA==")]);
        var result = await client.CompleteAsync(new AiChatRequest("http://localhost:1234/v1", "test",
            [message], tools.ToolDefinitions));
        Assert.Equal("ok", result.Content);
        using var payload = JsonDocument.Parse(handler.Body!);
        var root = payload.RootElement;
        Assert.Equal("test", root.GetProperty("model").GetString());
        var parts = root.GetProperty("messages")[0].GetProperty("content");
        Assert.Contains(parts.EnumerateArray(), x => x.GetProperty("type").GetString() == "text" &&
            x.GetProperty("text").GetString()!.Contains("part contents"));
        Assert.Contains(parts.EnumerateArray(), x => x.GetProperty("type").GetString() == "image_url" &&
            x.GetProperty("image_url").GetProperty("url").GetString() == "data:image/png;base64,AA==");
        Assert.Equal(5, root.GetProperty("tools").GetArrayLength());
    }

    [Fact]
    public async Task ClosingSettingsCancelsPendingModelDiscovery()
    {
        var chat = new FakeChatClient { WaitForModels = true };
        using var model = CreateAssistant(new FakeSettingsStore(), new FakeCodexClient(), chat);
        model.BeginSettingsEdit();
        var loading = model.LoadModelsCommand.ExecuteAsync(null);
        Assert.True(model.IsBusy);
        model.CancelSettingsEdit();
        await loading;
        Assert.False(model.IsBusy);
        Assert.Equal("LM Studio", model.ConnectionStatus);
        Assert.DoesNotContain(model.Messages, entry => entry.Role == "Error");
    }

    [Fact]
    public async Task ToolResultIsReturnedWhenCancellationArrivesAfterCommandCompletion()
    {
        using var cancellation = new CancellationTokenSource();
        var tools = new CadAgentToolset(new CancellingCommandService(cancellation), null);
        var result = await tools.ExecuteAsync(new AiToolCall("1", "cad_command", "{\"command\":\"HELP\"}"),
            cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Contains("\"success\":true", result);
    }

    [Fact]
    public async Task SwitchingProviderWhileReplyIsPendingIgnoresStaleAssistantOutput()
    {
        var runner = new DelayedRunner();
        using var model = new AiAssistantToolboxViewModel(new FakeSettingsStore(), runner,
            new FakeChatClient(), new FakeCodexClient(), new CadCommandLineService(), new FakeIcons());
        model.Model = "test";
        model.UserInput = "Inspect";
        var sending = model.SendCommand.ExecuteAsync(null);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        model.Provider = AiAssistantProvider.Codex;
        runner.Release.TrySetResult(true);
        await sending.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("Codex", model.ConnectionStatus);
        Assert.DoesNotContain(model.Messages, message => message.Content == "stale answer");
        Assert.False(model.IsBusy);
    }

    private static AiAssistantToolboxViewModel CreateAssistant(FakeSettingsStore store, FakeCodexClient codex,
        FakeChatClient? chat = null)
    {
        chat ??= new FakeChatClient();
        return new AiAssistantToolboxViewModel(store, new AgentRunner(chat), chat, codex,
            new CadCommandLineService(), new FakeIcons());
    }

    private sealed class FakeSettingsStore : IAiAssistantSettingsStore
    {
        private AiAssistantSettings settings = new();
        public AiAssistantSettings Load() => settings.Clone();
        public void Save(AiAssistantSettings value) => settings = value.Clone();
    }

    private sealed class DelayedRunner : IAgentRunner
    {
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AgentRunResult> RunAsync(AgentRunRequest request,
            Func<AgentRunEvent, ValueTask>? reportEvent = null,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(true);
            await Release.Task;
            if (reportEvent is not null)
                await reportEvent(new AgentRunEvent(AgentRunEventKind.AssistantMessage, "stale answer"));
            return new AgentRunResult("test", request.ContextWindowTokens, false);
        }
    }

    private sealed class CancellingCommandService(CancellationTokenSource cancellation) : ICadCommandLineService
    {
        public IReadOnlyList<CadCommandDescriptor> Commands => [];
        public IReadOnlyList<string> Complete(string prefix, int limit = 12) => [];
        public Task<CadCommandResult> ExecuteAsync(string input, ICadCommandContext? context)
        {
            cancellation.Cancel();
            return Task.FromResult(new CadCommandResult(true, "Committed."));
        }
    }

    private sealed class FakeCodexClient : ICodexAgentClient
    {
        public int ResetCount { get; private set; }
        public Task ResetConversationAsync(CancellationToken cancellationToken = default)
        { ResetCount++; return Task.CompletedTask; }
        public Task<IReadOnlyList<string>> GetModelsAsync(CodexAgentOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<CodexAgentRunResult> RunAsync(CodexAgentRunRequest request,
            Func<AgentRunEvent, ValueTask>? reportEvent = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodexAgentRunResult(null, true));
    }

    private sealed class FakeIcons : IToolboxIconProvider
    {
        public object ModelTree { get; } = new();
        public object Properties { get; } = new();
        public object Modeling { get; } = new();
        public object Messages { get; } = new();
    }

    private sealed class FakeChatClient : IAiChatClient
    {
        private int count;
        public bool WaitForModels { get; init; }
        public AiChatRequest? LastRequest { get; private set; }
        public async Task<IReadOnlyList<string>> GetModelsAsync(string endpoint, CancellationToken cancellationToken = default)
        {
            if (WaitForModels) await Task.Delay(Timeout.Infinite, cancellationToken);
            return ["test"];
        }
        public Task<AiChatCompletion> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest ??= request;
            return Task.FromResult(count++ == 0
                ? new AiChatCompletion(null, [new AiToolCall("call-1", "cad_status", "{}")], "test")
                : new AiChatCompletion("ok", [], "test"));
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"ok\"}}],\"model\":\"test\"}",
                    Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class FakeToolset : IAgentToolset
    {
        public int Calls { get; private set; }
        public IReadOnlyList<AiToolDefinition> ToolDefinitions =>
            [new("cad_status", "Read status", JsonDocument.Parse("{}").RootElement.Clone())];
        public IReadOnlyList<AiToolDefinition> SelectTools(string prompt, bool aggressive = false) => ToolDefinitions;
        public Task<string> ExecuteAsync(AiToolCall toolCall, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult("{\"bodies\":1}"); }
    }
}
