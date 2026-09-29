namespace Cadoryx.AI.Contracts;

public enum AiAssistantProvider
{
    LmStudio,
    Codex
}

public sealed class AiAssistantSettings
{
    public const string DefaultEndpoint = "http://localhost:1234/v1";
    public const int DefaultContextWindowTokens = 8192;
    public const int MinimumContextWindowTokens = 4096;
    public const int MaximumContextWindowTokens = 262144;
    public const string DefaultCodexServiceTier = "default";
    public const string FastCodexServiceTier = "fast";

    public AiAssistantProvider Provider { get; set; } = AiAssistantProvider.LmStudio;
    public string Endpoint { get; set; } = DefaultEndpoint;
    public string Model { get; set; } = string.Empty;
    public double Temperature { get; set; } = 0.2;
    public bool EnableCadTools { get; set; } = true;
    public int ContextWindowTokens { get; set; } = DefaultContextWindowTokens;
    public string CodexExecutablePath { get; set; } = "codex";
    public string CodexModel { get; set; } = string.Empty;
    public string CodexReasoningEffort { get; set; } = "medium";
    public string CodexServiceTier { get; set; } = DefaultCodexServiceTier;

    public void Normalize()
    {
        Endpoint = string.IsNullOrWhiteSpace(Endpoint)
            ? DefaultEndpoint
            : Endpoint.Trim().TrimEnd('/');
        Model = Model?.Trim() ?? string.Empty;
        Temperature = double.IsFinite(Temperature)
            ? Math.Clamp(Temperature, 0, 2)
            : 0.2;
        ContextWindowTokens = Math.Clamp(
            ContextWindowTokens,
            MinimumContextWindowTokens,
            MaximumContextWindowTokens);
        CodexExecutablePath = string.IsNullOrWhiteSpace(CodexExecutablePath)
            ? "codex"
            : CodexExecutablePath.Trim();
        CodexModel = CodexModel?.Trim() ?? string.Empty;
        CodexReasoningEffort = NormalizeReasoningEffort(CodexReasoningEffort);
        CodexServiceTier = string.Equals(
            CodexServiceTier,
            FastCodexServiceTier,
            StringComparison.OrdinalIgnoreCase)
            ? FastCodexServiceTier
            : DefaultCodexServiceTier;
    }

    public AiAssistantSettings Clone()
    {
        var clone = new AiAssistantSettings
        {
            Provider = Provider,
            Endpoint = Endpoint,
            Model = Model,
            Temperature = Temperature,
            EnableCadTools = EnableCadTools,
            ContextWindowTokens = ContextWindowTokens,
            CodexExecutablePath = CodexExecutablePath,
            CodexModel = CodexModel,
            CodexReasoningEffort = CodexReasoningEffort,
            CodexServiceTier = CodexServiceTier
        };
        clone.Normalize();
        return clone;
    }

    private static string NormalizeReasoningEffort(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "none" => "none",
            "minimal" => "minimal",
            "low" => "low",
            "high" => "high",
            "xhigh" => "xhigh",
            _ => "medium"
        };
}
