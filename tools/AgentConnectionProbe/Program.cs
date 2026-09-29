using Cadoryx.Agent.Codex;
using Cadoryx.Agent;
using Cadoryx.AI.Contracts;
using Cadoryx.AI.LmStudio;
using System.Text.Json;

var provider = Arg("--provider") ?? "codex";
var prompt = Arg("--message");
var model = Arg("--model") ?? "";
var file = Arg("--file");
var useTool = args.Contains("--tool-roundtrip", StringComparer.Ordinal);
var useAgentRunner = args.Contains("--agent-runner", StringComparer.Ordinal);
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
try
{
    if (provider == "codex")
    {
        using var client = new CodexAppServerClient();
        var options = new CodexAgentOptions(Arg("--executable") ?? "codex", model,
            "medium", "default", Environment.CurrentDirectory);
        var models = await client.GetModelsAsync(options, timeout.Token);
        Console.WriteLine($"Codex app-server connected; models: {models.Count}");
        foreach (var item in models.Take(10)) Console.WriteLine(item);
        if (prompt is not null)
        {
            if (string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("--model is required with --message.");
            var parts = ContentParts(prompt, file);
            var toolset = useTool ? new ProbeToolset() : null;
            var result = await client.RunAsync(new CodexAgentRunRequest(prompt,
                "Connection probe only; no CAD document is active.", options, toolset, parts),
                e => { if (e.Content is not null) Console.WriteLine(e.Content); return ValueTask.CompletedTask; },
                timeout.Token);
            Console.WriteLine($"Response empty: {result.ResponseWasEmpty}");
            if (toolset is not null && toolset.Calls == 0)
                throw new InvalidOperationException("The model did not call the probe tool.");
        }
    }
    else if (provider == "lmstudio")
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(110) };
        var client = new LmStudioChatClient(http);
        var endpoint = Arg("--endpoint") ?? AiAssistantSettings.DefaultEndpoint;
        var models = await client.GetModelsAsync(endpoint, timeout.Token);
        Console.WriteLine($"LM Studio connected; models: {models.Count}");
        foreach (var item in models.Take(10)) Console.WriteLine(item);
        if (prompt is not null)
        {
            if (string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("--model is required with --message.");
            if (useAgentRunner)
            {
                var conversation = new AgentConversation();
                conversation.AddUser(prompt, ContentParts(prompt, file));
                var result = await new AgentRunner(client).RunAsync(new AgentRunRequest(endpoint, model,
                    "You are a concise assistant.", prompt, conversation, 8192, 0.2),
                    e => { if (e.Content is not null) Console.WriteLine(e.Content); return ValueTask.CompletedTask; },
                    timeout.Token);
                if (result.ResponseWasEmpty) throw new InvalidOperationException("Agent runner received no assistant text.");
            }
            else
            {
                var completion = await client.CompleteAsync(new AiChatRequest(endpoint, model,
                    [AiChatMessage.User(prompt, ContentParts(prompt, file))], [], MaxOutputTokens: 4096), timeout.Token);
                if (string.IsNullOrWhiteSpace(completion.Content))
                    throw new InvalidOperationException("LM Studio returned no assistant text.");
                Console.WriteLine(completion.Content);
            }
        }
    }
    else throw new ArgumentException("--provider must be codex or lmstudio.");
}
catch (Exception e)
{
    Console.Error.WriteLine($"Connection probe failed: {e.Message}");
    Environment.ExitCode = 1;
}

string? Arg(string key)
{
    var index = Array.IndexOf(args, key);
    return index < 0 || index + 1 == args.Length ? null : args[index + 1];
}

static IReadOnlyList<AiChatContentPart> ContentParts(string prompt, string? file)
{
    var parts = new List<AiChatContentPart> { AiChatContentPart.TextPart(prompt) };
    if (file is null) return parts;
    var info = new FileInfo(file);
    if (!info.Exists) throw new FileNotFoundException("Attachment does not exist.", file);
    var extension = info.Extension.ToLowerInvariant();
    if (extension is ".png" or ".jpg" or ".jpeg")
    {
        if (info.Length > 8 * 1024 * 1024) throw new ArgumentException("Image exceeds 8 MB.");
        var mime = extension == ".png" ? "image/png" : "image/jpeg";
        parts.Add(AiChatContentPart.Image($"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(file))}"));
    }
    else if (extension is ".txt" or ".md" or ".csv" or ".json")
    {
        if (info.Length > 1024 * 1024) throw new ArgumentException("Text attachment exceeds 1 MB.");
        parts.Add(AiChatContentPart.FileText(info.Name, "text/plain", File.ReadAllText(file)));
    }
    else throw new ArgumentException("Unsupported attachment type.");
    Console.WriteLine($"Attachment sent: {info.Name} ({info.Length} bytes)");
    return parts;
}

internal sealed class ProbeToolset : IAgentToolset
{
    public int Calls { get; private set; }
    public IReadOnlyList<AiToolDefinition> ToolDefinitions { get; } =
        [new("probe_echo", "Echo an exact code to prove dynamic tool dispatch.",
            JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"code\":{\"type\":\"string\"}},\"required\":[\"code\"],\"additionalProperties\":false}").RootElement.Clone())];
    public IReadOnlyList<AiToolDefinition> SelectTools(string prompt, bool aggressive = false) => ToolDefinitions;
    public Task<string> ExecuteAsync(AiToolCall call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (call.Name != "probe_echo") throw new ArgumentException("Unexpected tool.");
        Calls++;
        Console.WriteLine($"Tool called: {call.Name}");
        return Task.FromResult(JsonSerializer.Serialize(new { echoed = call.ArgumentsJson }));
    }
}
