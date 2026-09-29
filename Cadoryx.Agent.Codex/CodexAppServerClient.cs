using System.Collections.Concurrent;
using System.Text.Json;
using Cadoryx.Agent;
using Cadoryx.AI.Contracts;

namespace Cadoryx.Agent.Codex;

public sealed class CodexAppServerClient : ICodexAgentClient, IDisposable
{
    private const string CadToolNamespace = "cadoryx";
    private const int NormalSafetyTokens = 640;
    private const int EstimatedThreadOverheadTokens = 256;
    private const string TruncatedContentMarker =
        "\n[content truncated to fit the model context window]";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ICodexAppServerTransportFactory _transportFactory;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pendingRequests = new();
    private ICodexAppServerTransport? _transport;
    private CancellationTokenSource? _connectionCancellation;
    private Task? _readLoop;
    private ActiveTurn? _activeTurn;
    private string? _connectionKey;
    private string? _threadId;
    private string? _threadKey;
    private int _estimatedThreadTokens;
    private long _nextRequestId;
    private bool _disposed;

    public CodexAppServerClient()
        : this(new CodexAppServerTransportFactory())
    {
    }

    internal CodexAppServerClient(ICodexAppServerTransportFactory transportFactory)
    {
        _transportFactory = transportFactory;
    }

    public async Task<IReadOnlyList<string>> GetModelsAsync(
        CodexAgentOptions options,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(options, cancellationToken);
        var response = await SendRequestAsync(
            "model/list",
            new { limit = 100, includeHidden = false },
            cancellationToken);
        if (!response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        return data.EnumerateArray()
            .Select(item =>
                item.TryGetProperty("model", out var model) ? model.GetString() :
                item.TryGetProperty("id", out var id) ? id.GetString() : null)
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<CodexAgentRunResult> RunAsync(
        CodexAgentRunRequest request,
        Func<AgentRunEvent, ValueTask>? reportEvent = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        await EnsureConnectedAsync(request.Options, cancellationToken);
        await EnsureThreadAsync(request, cancellationToken);

        var active = new ActiveTurn(
            _threadId!,
            request.Toolset,
            reportEvent,
            SynchronizationContext.Current);
        if (Interlocked.CompareExchange(ref _activeTurn, active, null) is not null)
            throw new InvalidOperationException("A Codex turn is already running.");

        using var registration = cancellationToken.Register(() =>
        {
            active.Completion.TrySetCanceled(cancellationToken);
            _ = InterruptTurnAsync(active.ThreadId);
        });

        try
        {
            var prompt = string.IsNullOrWhiteSpace(request.WorkspaceContext)
                ? request.Prompt
                : $"""
                   <cad_workspace_context>
                   {request.WorkspaceContext}
                   </cad_workspace_context>

                   <user_request>
                   {request.Prompt}
                   </user_request>
                   """;
            var contextWindow = Math.Clamp(
                request.Options.ContextWindowTokens,
                AiAssistantSettings.MinimumContextWindowTokens,
                AiAssistantSettings.MaximumContextWindowTokens);
            var maxOutputTokens = Math.Clamp(contextWindow / 8, 512, 2048);
            var contextReduced = false;
            var inputTokens = EstimateInputTokens(prompt, request.ContentParts);
            var freshThreadBudget = Math.Max(
                1024,
                contextWindow - EstimatedThreadOverheadTokens - maxOutputTokens - NormalSafetyTokens);
            if (_estimatedThreadTokens > 0 &&
                inputTokens <= freshThreadBudget &&
                _estimatedThreadTokens + inputTokens + maxOutputTokens + NormalSafetyTokens > contextWindow)
            {
                await ResetConversationAsync(cancellationToken);
                await EnsureThreadAsync(request, cancellationToken);
                active.ThreadId = _threadId!;
                contextReduced = true;
            }

            var availableTokens = Math.Max(
                1024,
                contextWindow - _estimatedThreadTokens - maxOutputTokens - NormalSafetyTokens);
            var limitedInput = inputTokens > availableTokens
                ? LimitInputToTokenBudget(prompt, request.ContentParts, availableTokens)
                : (Prompt: prompt, ContentParts: request.ContentParts);
            inputTokens = EstimateInputTokens(limitedInput.Prompt, limitedInput.ContentParts);
            contextReduced |= inputTokens < EstimateInputTokens(prompt, request.ContentParts);
            _estimatedThreadTokens += inputTokens + maxOutputTokens;
            if (contextReduced)
            {
                await ReportAsync(
                    active,
                    new AgentRunEvent(
                        AgentRunEventKind.ContextReduced,
                        ContextWindowTokens: contextWindow));
            }

            var turnResult = await SendRequestAsync(
                "turn/start",
                new
                {
                    threadId = active.ThreadId,
                    input = CreateTurnInput(limitedInput.Prompt, limitedInput.ContentParts),
                    model = NullIfWhiteSpace(request.Options.Model),
                    effort = NormalizeReasoningEffort(request.Options.ReasoningEffort)
                },
                cancellationToken);
            if (turnResult.TryGetProperty("turn", out var turn) &&
                turn.TryGetProperty("id", out var turnId))
            {
                active.TurnId = turnId.GetString();
            }

            await active.Completion.Task;
            return new CodexAgentRunResult(
                NullIfWhiteSpace(request.Options.Model),
                active.AssistantMessageCount == 0);
        }
        finally
        {
            Interlocked.CompareExchange(ref _activeTurn, null, active);
        }
    }

    private static IReadOnlyList<object> CreateTurnInput(
        string prompt,
        IReadOnlyList<AiChatContentPart>? contentParts)
    {
        var input = new List<object> { new { type = "text", text = prompt } };
        foreach (var part in contentParts ?? [])
        {
            if (part.Type == AiChatContentPartType.Image)
            {
                input.Add(new
                {
                    type = "image",
                    url = part.DataUrl ?? throw new ArgumentException("Image content requires a data URL.")
                });
                continue;
            }

            if (part.Type == AiChatContentPartType.Text && part.FileName is not null)
                input.Add(new { type = "text", text = part.Text ?? string.Empty });
        }

        return input;
    }

    private static (string Prompt, IReadOnlyList<AiChatContentPart>? ContentParts) LimitInputToTokenBudget(
        string prompt,
        IReadOnlyList<AiChatContentPart>? contentParts,
        int tokenBudget)
    {
        var textLength = prompt.Length + (contentParts ?? [])
            .Where(part => part.Type == AiChatContentPartType.Text)
            .Sum(part => part.Text?.Length ?? 0);
        if (textLength == 0)
            return (prompt, contentParts);

        var low = 0;
        var high = textLength;
        while (low < high)
        {
            var keptCharacters = low + (high - low + 1) / 2;
            var candidate = CreateCharacterBudgetInput(prompt, contentParts, keptCharacters);
            if (EstimateInputTokens(candidate.Prompt, candidate.ContentParts) <= tokenBudget)
                low = keptCharacters;
            else
                high = keptCharacters - 1;
        }

        return CreateCharacterBudgetInput(prompt, contentParts, low);
    }

    private static (string Prompt, IReadOnlyList<AiChatContentPart>? ContentParts) CreateCharacterBudgetInput(
        string prompt,
        IReadOnlyList<AiChatContentPart>? contentParts,
        int characterBudget)
    {
        var remaining = characterBudget;
        var limitedPrompt = AllocateText(prompt, ref remaining);
        var limitedParts = contentParts?
            .Select(part => part.Type == AiChatContentPartType.Text
                ? part with { Text = AllocateText(part.Text ?? string.Empty, ref remaining) }
                : part)
            .ToArray();
        return (limitedPrompt, limitedParts);
    }

    private static string AllocateText(string text, ref int remaining)
    {
        var kept = Math.Min(text.Length, Math.Max(remaining, 0));
        remaining -= kept;
        if (kept == text.Length)
            return text;
        if (kept <= TruncatedContentMarker.Length + 8)
            return text[..kept];

        var contentCharacters = kept - TruncatedContentMarker.Length;
        var prefixLength = (int)(contentCharacters * 0.75);
        var suffixLength = contentCharacters - prefixLength;
        return string.Concat(
            text.AsSpan(0, prefixLength),
            TruncatedContentMarker,
            text.AsSpan(text.Length - suffixLength));
    }

    private static int EstimateInputTokens(
        string prompt,
        IReadOnlyList<AiChatContentPart>? contentParts)
    {
        var total = 8 + EstimateTextTokens(prompt);
        foreach (var part in contentParts ?? [])
            total += part.Type == AiChatContentPartType.Image
                ? 1024
                : EstimateTextTokens(part.Text);
        return total;
    }

    private static int EstimateTextTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        var ascii = text.Count(character => character <= 0x7f);
        return (int)Math.Ceiling(ascii / 3.6 + (text.Length - ascii) / 1.25);
    }

    private static int EstimateThreadTokens(IReadOnlyList<AiToolDefinition> definitions) =>
        EstimatedThreadOverheadTokens + definitions.Sum(definition =>
            20 +
            EstimateTextTokens(definition.Name) +
            EstimateTextTokens(definition.Description) +
            EstimateTextTokens(definition.Parameters.GetRawText()));

    public async Task ResetConversationAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_threadId is not null && _transport is not null)
            {
                try
                {
                    await SendRequestAsync(
                        "thread/unsubscribe",
                        new { threadId = _threadId },
                        cancellationToken);
                }
                catch (InvalidOperationException)
                {
                }
            }

            _threadId = null;
            _threadKey = null;
            _estimatedThreadTokens = 0;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _connectionCancellation?.Cancel();
        _transport?.Dispose();
        _connectionCancellation?.Dispose();
        _lifecycleGate.Dispose();
        _writeGate.Dispose();
        FailPendingRequests(new ObjectDisposedException(nameof(CodexAppServerClient)));
    }

    private async Task EnsureConnectedAsync(
        CodexAgentOptions options,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var key = $"{options.ExecutablePath.Trim()}|{NormalizeServiceTier(options.ServiceTier)}";
        if (_transport is not null && string.Equals(_connectionKey, key, StringComparison.Ordinal))
            return;

        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_transport is not null && string.Equals(_connectionKey, key, StringComparison.Ordinal))
                return;

            StopConnection();
            _transport = _transportFactory.Start(options);
            _connectionCancellation = new CancellationTokenSource();
            _connectionKey = key;
            _threadId = null;
            _threadKey = null;
            _estimatedThreadTokens = 0;
            _readLoop = ReadLoopAsync(_transport, _connectionCancellation.Token);

            await SendRequestAsync(
                "initialize",
                new
                {
                    clientInfo = new
                    {
                        name = "cadoryx",
                        title = "Cadoryx",
                        version = "1.0"
                    },
                    capabilities = new { experimentalApi = true }
                },
                cancellationToken);
            await SendNotificationAsync("initialized", new { }, cancellationToken);
        }
        catch
        {
            StopConnection();
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task EnsureThreadAsync(
        CodexAgentRunRequest request,
        CancellationToken cancellationToken)
    {
        var definitions = request.Toolset?.ToolDefinitions ?? [];
        var toolNames = string.Join(
            ",",
            definitions.Select(definition => definition.Name).Order(StringComparer.Ordinal));
        var key = string.Join(
            "|",
            request.Options.Model,
            NormalizeReasoningEffort(request.Options.ReasoningEffort),
            NormalizeServiceTier(request.Options.ServiceTier),
            definitions.Count,
            toolNames,
            Math.Clamp(
                request.Options.ContextWindowTokens,
                AiAssistantSettings.MinimumContextWindowTokens,
                AiAssistantSettings.MaximumContextWindowTokens));
        if (_threadId is not null && string.Equals(_threadKey, key, StringComparison.Ordinal))
            return;

        await ResetConversationAsync(cancellationToken);
        var dynamicTools = definitions.Select(definition => new
        {
            name = definition.Name,
            @namespace = CadToolNamespace,
            description = definition.Description,
            inputSchema = definition.Parameters,
            deferLoading = true
        }).ToArray();
        var response = await SendRequestAsync(
            "thread/start",
            new
            {
                model = NullIfWhiteSpace(request.Options.Model),
                cwd = NormalizeWorkingDirectory(request.Options.WorkingDirectory),
                approvalPolicy = "never",
                sandbox = "read-only",
                ephemeral = true,
                developerInstructions =
                    "You are the CAD assistant embedded in Cadoryx. " +
                    "Use the provided dynamic CAD tools for every document inspection or modification. " +
                    "Do not use shell or file editing tools to change CAD documents. " +
                    "Never claim an operation succeeded before its CAD tool result confirms it.",
                dynamicTools
            },
            cancellationToken);

        if (!response.TryGetProperty("thread", out var thread) ||
            !thread.TryGetProperty("id", out var id) ||
            string.IsNullOrWhiteSpace(id.GetString()))
        {
            throw new InvalidOperationException("Codex app-server did not return a thread id.");
        }

        _threadId = id.GetString();
        _threadKey = key;
        _estimatedThreadTokens = EstimateThreadTokens(definitions);
    }

    private async Task<JsonElement> SendRequestAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingRequests.TryAdd(id, completion))
            throw new InvalidOperationException("Unable to register Codex app-server request.");

        try
        {
            await WriteMessageAsync(new { method, id, @params = parameters }, cancellationToken);
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            _pendingRequests.TryRemove(id, out _);
        }
    }

    private Task SendNotificationAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken) =>
        WriteMessageAsync(new { method, @params = parameters }, cancellationToken);

    private async Task WriteMessageAsync(object message, CancellationToken cancellationToken)
    {
        var transport = _transport ??
                        throw new InvalidOperationException("Codex app-server is not connected.");
        var json = JsonSerializer.Serialize(message, JsonOptions);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await transport.WriteLineAsync(json, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync(
        ICodexAppServerTransport transport,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await transport.ReadLineAsync(cancellationToken) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement.Clone();
                if (message.TryGetProperty("id", out var idElement) &&
                    idElement.TryGetInt64(out var id))
                {
                    if (message.TryGetProperty("method", out _))
                        _ = HandleServerRequestAsync(id, message, cancellationToken);
                    else
                        CompletePendingRequest(id, message);
                    continue;
                }

                if (message.TryGetProperty("method", out var method))
                    await HandleNotificationAsync(method.GetString(), message);
            }

            throw new InvalidOperationException(
                $"Codex app-server closed its output stream. {transport.GetErrorSummary()}".Trim());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            FailPendingRequests(exception);
            _activeTurn?.Completion.TrySetException(exception);
        }
    }

    private void CompletePendingRequest(long id, JsonElement message)
    {
        if (!_pendingRequests.TryGetValue(id, out var completion))
            return;
        if (message.TryGetProperty("error", out var error))
        {
            var detail = error.TryGetProperty("message", out var errorMessage)
                ? errorMessage.GetString()
                : error.GetRawText();
            completion.TrySetException(new InvalidOperationException($"Codex app-server: {detail}"));
            return;
        }

        completion.TrySetResult(
            message.TryGetProperty("result", out var result)
                ? result.Clone()
                : JsonSerializer.SerializeToElement(new { }));
    }

    private async Task HandleServerRequestAsync(
        long id,
        JsonElement message,
        CancellationToken cancellationToken)
    {
        try
        {
            var method = message.GetProperty("method").GetString();
            if (!string.Equals(method, "item/tool/call", StringComparison.Ordinal))
            {
                await WriteMessageAsync(
                    new
                    {
                        id,
                        error = new { code = -32601, message = $"Unsupported server request: {method}" }
                    },
                    cancellationToken);
                return;
            }

            var active = _activeTurn ??
                         throw new InvalidOperationException("Codex requested a tool without an active turn.");
            var parameters = message.GetProperty("params");
            var toolNamespace = parameters.TryGetProperty("namespace", out var namespaceElement) &&
                                namespaceElement.ValueKind == JsonValueKind.String
                ? namespaceElement.GetString()
                : null;
            if (!string.Equals(toolNamespace, CadToolNamespace, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Unsupported dynamic tool namespace: {toolNamespace ?? "<missing>"}.");
            }

            var toolName = parameters.GetProperty("tool").GetString() ??
                           throw new InvalidOperationException("Codex tool request did not include a tool name.");
            var arguments = parameters.TryGetProperty("arguments", out var argumentsElement)
                ? argumentsElement.GetRawText()
                : "{}";
            if (active.Toolset is null)
                throw new InvalidOperationException("CAD tools are disabled.");

            var result = await InvokeAsync(
                active.SynchronizationContext,
                () => active.Toolset.ExecuteAsync(
                    new AiToolCall(
                        parameters.GetProperty("callId").GetString() ?? Guid.NewGuid().ToString("N"),
                        toolName,
                        arguments),
                    cancellationToken));
            await ReportAsync(
                active,
                new AgentRunEvent(AgentRunEventKind.ToolResult, result, toolName));
            await WriteMessageAsync(
                new
                {
                    id,
                    result = new
                    {
                        contentItems = new[] { new { type = "inputText", text = result } },
                        success = IsSuccessfulToolResult(result)
                    }
                },
                cancellationToken);
        }
        catch (Exception exception)
        {
            await WriteMessageAsync(
                new
                {
                    id,
                    result = new
                    {
                        contentItems = new[]
                        {
                            new { type = "inputText", text = $"Tool execution failed: {exception.Message}" }
                        },
                        success = false
                    }
                },
                CancellationToken.None);
        }
    }

    private async Task HandleNotificationAsync(string? method, JsonElement message)
    {
        var active = _activeTurn;
        if (active is null || !message.TryGetProperty("params", out var parameters))
            return;

        if (string.Equals(method, "item/completed", StringComparison.Ordinal) &&
            parameters.TryGetProperty("item", out var item) &&
            item.TryGetProperty("type", out var type) &&
            string.Equals(type.GetString(), "agentMessage", StringComparison.Ordinal) &&
            item.TryGetProperty("text", out var text) &&
            !string.IsNullOrWhiteSpace(text.GetString()))
        {
            active.AssistantMessageCount++;
            await ReportAsync(
                active,
                new AgentRunEvent(AgentRunEventKind.AssistantMessage, text.GetString()!.Trim()));
            return;
        }

        if (!string.Equals(method, "turn/completed", StringComparison.Ordinal) ||
            !parameters.TryGetProperty("turn", out var turn))
        {
            return;
        }

        var status = turn.TryGetProperty("status", out var statusElement)
            ? statusElement.GetString()
            : null;
        if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            active.Completion.TrySetResult(true);
            return;
        }

        var error = turn.TryGetProperty("error", out var errorElement) &&
                    errorElement.ValueKind != JsonValueKind.Null
            ? errorElement.GetRawText()
            : status ?? "unknown error";
        active.Completion.TrySetException(
            new InvalidOperationException($"Codex turn ended with status '{status}': {error}"));
    }

    private async Task InterruptTurnAsync(string threadId)
    {
        try
        {
            await SendRequestAsync(
                "turn/interrupt",
                new { threadId },
                CancellationToken.None);
        }
        catch (Exception)
        {
        }
    }

    private static async Task ReportAsync(ActiveTurn active, AgentRunEvent agentEvent)
    {
        if (active.ReportEvent is null)
            return;
        await InvokeAsync(
            active.SynchronizationContext,
            async () =>
            {
                await active.ReportEvent(agentEvent);
                return true;
            });
    }

    private static Task<T> InvokeAsync<T>(
        SynchronizationContext? synchronizationContext,
        Func<Task<T>> action)
    {
        if (synchronizationContext is null || ReferenceEquals(SynchronizationContext.Current, synchronizationContext))
            return action();

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        synchronizationContext.Post(
            async _ =>
            {
                try
                {
                    completion.TrySetResult(await action());
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            },
            null);
        return completion.Task;
    }

    private void StopConnection()
    {
        _connectionCancellation?.Cancel();
        _transport?.Dispose();
        _transport = null;
        _connectionCancellation?.Dispose();
        _connectionCancellation = null;
        _readLoop = null;
        _connectionKey = null;
        _threadId = null;
        _threadKey = null;
        FailPendingRequests(new InvalidOperationException("Codex app-server connection was reset."));
    }

    private void FailPendingRequests(Exception exception)
    {
        foreach (var completion in _pendingRequests.Values)
            completion.TrySetException(exception);
        _pendingRequests.Clear();
    }

    private static bool IsSuccessfulToolResult(string result)
    {
        try
        {
            using var document = JsonDocument.Parse(result);
            return !document.RootElement.TryGetProperty("success", out var success) ||
                   success.ValueKind != JsonValueKind.False;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static string NormalizeReasoningEffort(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "none" => "none",
            "minimal" => "minimal",
            "low" => "low",
            "high" => "high",
            "xhigh" => "xhigh",
            _ => "medium"
        };

    private static string NormalizeServiceTier(string value) =>
        string.Equals(value, "fast", StringComparison.OrdinalIgnoreCase) ? "fast" : "default";

    private static string NormalizeWorkingDirectory(string value) =>
        Directory.Exists(value) ? Path.GetFullPath(value) : Environment.CurrentDirectory;

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class ActiveTurn(
        string threadId,
        IAgentToolset? toolset,
        Func<AgentRunEvent, ValueTask>? reportEvent,
        SynchronizationContext? synchronizationContext)
    {
        public string ThreadId { get; set; } = threadId;
        public IAgentToolset? Toolset { get; } = toolset;
        public Func<AgentRunEvent, ValueTask>? ReportEvent { get; } = reportEvent;
        public SynchronizationContext? SynchronizationContext { get; } = synchronizationContext;
        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? TurnId { get; set; }
        public int AssistantMessageCount { get; set; }
    }
}
