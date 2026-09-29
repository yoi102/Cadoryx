using System.Text.Json;

namespace Cadoryx.AI.Contracts;

public enum AiChatRole
{
    System,
    User,
    Assistant,
    Tool
}

public sealed record AiToolCall(string Id, string Name, string ArgumentsJson);

public enum AiChatContentPartType
{
    Text,
    Image
}

public sealed record AiChatContentPart(
    AiChatContentPartType Type,
    string? Text = null,
    string? DataUrl = null,
    string? FileName = null,
    string? ContentType = null)
{
    public static AiChatContentPart TextPart(string text) =>
        new(AiChatContentPartType.Text, Text: text);

    public static AiChatContentPart Image(string dataUrl) =>
        new(AiChatContentPartType.Image, DataUrl: dataUrl);

    public static AiChatContentPart FileText(
        string fileName,
        string contentType,
        string text) =>
        new(
            AiChatContentPartType.Text,
            Text: $"[Attached file: {fileName} ({contentType})]\n{text}",
            FileName: fileName,
            ContentType: contentType);
}

public sealed record AiChatMessage(
    AiChatRole Role,
    string? Content,
    IReadOnlyList<AiToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    IReadOnlyList<AiChatContentPart>? ContentParts = null)
{
    public static AiChatMessage System(string content) => new(AiChatRole.System, content);
    public static AiChatMessage User(
        string content,
        IReadOnlyList<AiChatContentPart>? contentParts = null) =>
        new(AiChatRole.User, content, ContentParts: contentParts);
    public static AiChatMessage Assistant(string? content, IReadOnlyList<AiToolCall>? toolCalls = null) =>
        new(AiChatRole.Assistant, content, toolCalls);
    public static AiChatMessage Tool(string toolCallId, string content) =>
        new(AiChatRole.Tool, content, ToolCallId: toolCallId);
}

public sealed record AiToolDefinition(
    string Name,
    string Description,
    JsonElement Parameters);

public sealed record AiChatRequest(
    string Endpoint,
    string Model,
    IReadOnlyList<AiChatMessage> Messages,
    IReadOnlyList<AiToolDefinition> Tools,
    double Temperature = 0.2,
    int MaxOutputTokens = 1024);

public sealed record AiChatCompletion(
    string? Content,
    IReadOnlyList<AiToolCall> ToolCalls,
    string? Model);

public interface IAiChatClient
{
    Task<IReadOnlyList<string>> GetModelsAsync(
        string endpoint,
        CancellationToken cancellationToken = default);

    Task<AiChatCompletion> CompleteAsync(
        AiChatRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class AiContextWindowExceededException : HttpRequestException
{
    public AiContextWindowExceededException(
        string message,
        System.Net.HttpStatusCode? statusCode,
        int? promptTokens,
        int? contextWindowTokens)
        : base(message, null, statusCode)
    {
        PromptTokens = promptTokens;
        ContextWindowTokens = contextWindowTokens;
    }

    public int? PromptTokens { get; }
    public int? ContextWindowTokens { get; }
}

public interface IAiAssistantSettingsStore
{
    AiAssistantSettings Load();
    void Save(AiAssistantSettings settings);
}
