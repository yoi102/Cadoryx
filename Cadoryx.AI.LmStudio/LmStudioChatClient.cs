using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Cadoryx.AI.Contracts;

namespace Cadoryx.AI.LmStudio;

public sealed class LmStudioChatClient(HttpClient httpClient) : IAiChatClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<IReadOnlyList<string>> GetModelsAsync(
        string endpoint,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            BuildEndpoint(endpoint, "models"),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        return data.EnumerateArray()
            .Select(item => item.TryGetProperty("id", out var id) ? id.GetString() : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<AiChatCompletion> CompleteAsync(
        AiChatRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new InvalidOperationException("Select an LM Studio model before sending a message.");

        var payload = new ChatCompletionRequestPayload(
            request.Model,
            request.Messages.Select(CreateMessagePayload).ToArray(),
            request.Tools.Count == 0
                ? null
                : request.Tools.Select(CreateToolPayload).ToArray(),
            request.Tools.Count == 0 ? null : "auto",
            Math.Clamp(request.Temperature, 0, 2),
            Math.Clamp(request.MaxOutputTokens, 1, 32768),
            Stream: false);

        using var response = await httpClient.PostAsJsonAsync(
            BuildEndpoint(request.Endpoint, "chat/completions"),
            payload,
            SerializerOptions,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0 ||
            choices[0].ValueKind != JsonValueKind.Object ||
            !choices[0].TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("LM Studio returned a response without an assistant message.");
        }

        // A token-limited response can contain an apparently complete first call
        // followed by a truncated second call. Never dispatch a partial batch.
        if (choices[0].TryGetProperty("finish_reason", out var finish) &&
            finish.ValueKind == JsonValueKind.String && finish.GetString() == "length")
        {
            throw new AiResponseTruncatedException(
                "LM Studio reached a token limit before completing the response. " +
                "No tool calls from this incomplete response were accepted.");
        }

        var content = message.TryGetProperty("content", out var contentElement) &&
                      contentElement.ValueKind == JsonValueKind.String
            ? contentElement.GetString()
            : null;
        var toolCalls = ParseToolCalls(message);
        if (finish.ValueKind == JsonValueKind.String && finish.GetString() == "tool_calls" && toolCalls.Count == 0)
            throw InvalidToolCalls();
        var model = root.TryGetProperty("model", out var modelElement)
            ? modelElement.GetString()
            : null;
        return new AiChatCompletion(content, toolCalls, model);
    }

    private static ChatMessagePayload CreateMessagePayload(AiChatMessage message)
    {
        return new ChatMessagePayload(
            Role: message.Role switch
            {
                AiChatRole.System => "system",
                AiChatRole.User => "user",
                AiChatRole.Assistant => "assistant",
                AiChatRole.Tool => "tool",
                _ => throw new ArgumentOutOfRangeException(nameof(message))
            },
            Content: message.ContentParts is { Count: > 0 } contentParts
                ? contentParts.Select(CreateContentPartPayload).ToArray()
                : message.Content,
            ToolCalls: message.ToolCalls?.Select(call => new ToolCallPayload(
                call.Id,
                "function",
                new ToolCallFunctionPayload(call.Name, call.ArgumentsJson))).ToArray(),
            ToolCallId: message.ToolCallId);
    }

    private static MessageContentPartPayload CreateContentPartPayload(AiChatContentPart part) =>
        part.Type switch
        {
            AiChatContentPartType.Text => new MessageContentPartPayload(
                "text",
                part.Text,
                null),
            AiChatContentPartType.Image => new MessageContentPartPayload(
                "image_url",
                null,
                new ImageUrlPayload(part.DataUrl ?? throw new ArgumentException("Image content requires a data URL."))),
            _ => throw new ArgumentOutOfRangeException(nameof(part))
        };

    private static ToolDefinitionPayload CreateToolPayload(AiToolDefinition definition) =>
        new("function", new ToolFunctionPayload(
            definition.Name,
            definition.Description,
            ToolSchemaPresentation.Create(definition.Parameters)));

    private static IReadOnlyList<AiToolCall> ParseToolCalls(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var calls) || calls.ValueKind == JsonValueKind.Null)
            return [];
        if (calls.ValueKind != JsonValueKind.Array)
            throw InvalidToolCalls();

        var result = new List<AiToolCall>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var call in calls.EnumerateArray())
        {
            if (call.ValueKind != JsonValueKind.Object ||
                !call.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object ||
                HasDuplicateProperties(call) || HasDuplicateProperties(function) ||
                (call.TryGetProperty("type", out var type) && (type.ValueKind != JsonValueKind.String || type.GetString() != "function")))
                throw InvalidToolCalls();

            var id = call.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()
                : null;
            var name = function.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || !ids.Add(id))
                throw InvalidToolCalls();
            string arguments = "{}";
            if (function.TryGetProperty("arguments", out var argumentsElement) && argumentsElement.ValueKind != JsonValueKind.Null)
            {
                if (argumentsElement.ValueKind != JsonValueKind.String) throw InvalidToolCalls();
                arguments = argumentsElement.GetString()!;
            }
            try
            {
                using var parsedArguments = JsonDocument.Parse(arguments);
                if (parsedArguments.RootElement.ValueKind != JsonValueKind.Object) throw InvalidToolCalls();
            }
            catch (JsonException) { throw InvalidToolCalls(); }
            result.Add(new AiToolCall(id, name, arguments));
        }

        return result;
    }

    private static InvalidOperationException InvalidToolCalls() => new(
        "LM Studio returned an incomplete or invalid tool-call batch. No calls from this response were accepted.");

    private static bool HasDuplicateProperties(JsonElement value)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().Any(property => !names.Add(property.Name));
    }

    private static Uri BuildEndpoint(string endpoint, string relativePath)
    {
        var normalized = string.IsNullOrWhiteSpace(endpoint)
            ? AiAssistantSettings.DefaultEndpoint
            : endpoint.Trim().TrimEnd('/');
        if (!Uri.TryCreate($"{normalized}/{relativePath}", UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("LM Studio endpoint must be an absolute HTTP or HTTPS URL.", nameof(endpoint));
        }

        return uri;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (IsContextWindowExceeded(detail))
        {
            throw new AiContextWindowExceededException(
                $"LM Studio context window is too small. {detail}".Trim(),
                response.StatusCode,
                ReadTokenCount(detail, "n_prompt_tokens"),
                ReadTokenCount(detail, "n_ctx"));
        }
        if (detail.Length > 800)
            detail = detail[..800];
        throw new HttpRequestException(
            $"LM Studio returned {(int)response.StatusCode} ({response.ReasonPhrase}). {detail}".Trim(),
            null,
            response.StatusCode);
    }

    private static bool IsContextWindowExceeded(string detail) =>
        detail.Contains("exceed_context_size_error", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("exceeds the available context size", StringComparison.OrdinalIgnoreCase);

    private static int? ReadTokenCount(string detail, string propertyName)
    {
        var match = Regex.Match(
            detail,
            $"{Regex.Escape(propertyName)}[^0-9]{{0,16}}(?<value>\\d+)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups["value"].Value, out var value)
            ? value
            : null;
    }

    private sealed record ChatCompletionRequestPayload(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessagePayload> Messages,
        [property: JsonPropertyName("tools")] IReadOnlyList<ToolDefinitionPayload>? Tools,
        [property: JsonPropertyName("tool_choice")] string? ToolChoice,
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("stream")] bool Stream);

    private sealed record ChatMessagePayload(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] object? Content,
        [property: JsonPropertyName("tool_calls")] IReadOnlyList<ToolCallPayload>? ToolCalls,
        [property: JsonPropertyName("tool_call_id")] string? ToolCallId);

    private sealed record MessageContentPartPayload(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("image_url")] ImageUrlPayload? ImageUrl);

    private sealed record ImageUrlPayload(
        [property: JsonPropertyName("url")] string Url);

    private sealed record ToolDefinitionPayload(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("function")] ToolFunctionPayload Function);

    private sealed record ToolFunctionPayload(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("parameters")] JsonElement Parameters);

    private sealed record ToolCallPayload(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("function")] ToolCallFunctionPayload Function);

    private sealed record ToolCallFunctionPayload(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("arguments")] string Arguments);
}
